using System.Collections.Concurrent;
using System.IO;
using System.Security;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Idmux.Core;

namespace Idmux.Daemon;

internal delegate Task ChunkSender(string stream, string data, CancellationToken cancellationToken);

internal sealed class DaemonRuntime(IdmuxConfig config, DaemonLog log) : IAsyncDisposable
{
    private readonly IdmuxConfig _config = config;
    private readonly DaemonLog _log = log;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly ConcurrentDictionary<string, CredentialEntry> _credentials = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SessionContext> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, BlockEntry> _blocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ActiveExecution> _activeBlocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _subscriberGate = new();
    private readonly Dictionary<Guid, Channel<string>> _subscribers = [];
    private volatile bool _paused;
    private DateTimeOffset _lastRequestAt = DateTimeOffset.UtcNow;
    private long _sessionCounter;

    public Action? RequestStop { get; set; }

    public async Task<JsonNode?> DispatchAsync(JsonRpcRequest request, ChunkSender? chunkSender, CancellationToken cancellationToken)
    {
        Touch();

        if (_paused && request.Method is not (RpcMethods.Status or RpcMethods.DaemonStatus or RpcMethods.DaemonResume or RpcMethods.DaemonStop))
        {
            throw new RpcException(RpcErrorCodes.DaemonPaused, "The daemon is paused.");
        }

        return request.Method switch
        {
            RpcMethods.Ping => IdmuxJson.ToNode(new { pong = true }),
            RpcMethods.Status => IdmuxJson.ToNode(GetStatusSnapshot()),
            RpcMethods.Capture => IdmuxJson.ToNode(await CaptureAsync(Parse<CaptureRequest>(request.Params), cancellationToken)),
            RpcMethods.Drop => IdmuxJson.ToNode(await DropAsync(Parse<DropRequest>(request.Params))),
            RpcMethods.SessionCreate => IdmuxJson.ToNode(await CreateSessionAsync(Parse<SessionCreateRequest>(request.Params), cancellationToken)),
            RpcMethods.SessionList => IdmuxJson.ToNode(ListSessions()),
            RpcMethods.SessionDestroy => IdmuxJson.ToNode(await DestroySessionAsync(Parse<SessionDestroyRequest>(request.Params))),
            RpcMethods.Run => IdmuxJson.ToNode(await RunAsync(Parse<RunRequest>(request.Params), chunkSender, cancellationToken)),
            RpcMethods.BlockList => IdmuxJson.ToNode(ListBlocks(Parse<BlockListRequest>(request.Params))),
            RpcMethods.BlockGet => HandleBlockGet(Parse<BlockGetRequest>(request.Params)),
            RpcMethods.BlockCancel => IdmuxJson.ToNode(await CancelBlockAsync(Parse<BlockCancelRequest>(request.Params), cancellationToken)),
            RpcMethods.BlockTail => IdmuxJson.ToNode(await TailBlockAsync(Parse<BlockTailRequest>(request.Params), chunkSender, cancellationToken)),
            RpcMethods.DaemonStatus => IdmuxJson.ToNode(GetDaemonStatus()),
            RpcMethods.DaemonPause => IdmuxJson.ToNode(await PauseAsync()),
            RpcMethods.DaemonResume => IdmuxJson.ToNode(await ResumeAsync()),
            RpcMethods.DaemonStop => IdmuxJson.ToNode(await StopAsync()),
            _ => throw new RpcException(RpcErrorCodes.MethodNotFound, $"Method '{request.Method}' was not found.")
        };
    }

    public StatusSnapshot GetStatusSnapshot()
    {
        var sessions = _sessions.Values
            .Select(context => CloneSession(context.Session))
            .OrderByDescending(session => session.LastUsedAt)
            .ThenBy(session => session.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var recentBlocks = _blocks.Values
            .Select(entry => entry.ToSummary())
            .OrderByDescending(block => block.StartedAt)
            .Take(20)
            .ToList();

        var credentials = _credentials.Values
            .Select(entry => CloneCredentialInfo(entry.Info))
            .OrderBy(credential => credential.Identity, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new StatusSnapshot
        {
            Daemon = CreateDaemonInfo(),
            Credentials = credentials,
            Sessions = sessions,
            RecentBlocks = recentBlocks
        };
    }

    public List<SessionRecord> ListSessions()
    {
        return _sessions.Values
            .Select(context => CloneSession(context.Session))
            .OrderBy(session => session.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public SessionWithBlocks GetSessionWithBlocks(string name, int limit = 50)
    {
        var context = GetSessionContext(name);
        var blocks = _blocks.Values
            .Where(block => string.Equals(block.Record.SessionName, name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(block => block.Record.StartedAt)
            .Take(limit)
            .Select(block => block.ToSummary())
            .ToList();

        return new SessionWithBlocks
        {
            Session = CloneSession(context.Session),
            Blocks = blocks
        };
    }

    public List<BlockSummary> ListBlocksForSession(string name, DateTimeOffset? since, int? limit)
    {
        if (!_sessions.ContainsKey(name))
        {
            throw new RpcException(RpcErrorCodes.SessionNotFound, $"Session '{name}' was not found.");
        }

        return _blocks.Values
            .Where(block => string.Equals(block.Record.SessionName, name, StringComparison.OrdinalIgnoreCase))
            .Where(block => since is null || block.Record.StartedAt >= since.Value)
            .OrderByDescending(block => block.Record.StartedAt)
            .Take(limit.GetValueOrDefault(100))
            .Select(block => block.ToSummary())
            .ToList();
    }

    public BlockRecord GetBlockRecord(string id)
    {
        return GetBlockEntry(id).ToRecord();
    }

    public async Task<object> DropCredentialFromWebAsync(string identity)
    {
        return await DropAsync(new DropRequest { Identity = identity });
    }

    public async Task<object> DestroySessionFromWebAsync(string name)
    {
        return await DestroySessionAsync(new SessionDestroyRequest { Name = name });
    }

    public async Task<object> CancelBlockFromWebAsync(string id, CancellationToken cancellationToken)
    {
        return await CancelBlockAsync(new BlockCancelRequest { Id = id }, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var active in _activeBlocks.Values)
        {
            await active.Worker.CancelCurrentAsync(CancelReason.Shutdown);
        }

        foreach (var session in _sessions.Values)
        {
            if (session.Worker is not null)
            {
                await session.Worker.DisposeAsync();
            }

            session.Gate.Dispose();
        }

        foreach (var credential in _credentials.Values)
        {
            credential.Dispose();
        }
    }

    public ChannelSubscription Subscribe()
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(1000)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        var id = Guid.NewGuid();
        lock (_subscriberGate)
        {
            _subscribers[id] = channel;
        }

        return new ChannelSubscription(id, channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        lock (_subscriberGate)
        {
            _subscribers.Remove(id);
        }
    }

    public async Task MaintenanceLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
            SweepExpiredCredentials();
            TryIdleShutdown();
        }
    }

    private async Task<HeldCredentialInfo> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.Identity))
        {
            throw new RpcException(RpcErrorCodes.InvalidParams, "capture requires --identity.");
        }

        CredentialEntry entry;
        if (ShouldEmulateCapture())
        {
            var secure = new SecureString();
            secure.MakeReadOnly();
            entry = new CredentialEntry(new HeldCredentialInfo
            {
                Identity = request.Identity,
                CapturedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(_config.Daemon.CredentialIdleMinutes),
                Emulated = true
            }, secure);
        }
        else
        {
            _log.Info($"Credential capture requested for '{request.Identity}'; waiting for password dialog.");
            BroadcastEvent(new
            {
                @event = "credential_capture_started",
                identity = request.Identity
            });

            var dialog = CredentialDialog.Prompt(request.Identity, string.IsNullOrWhiteSpace(request.Caption) ? $"Capture credential for {request.Identity}" : request.Caption!);
            if (!dialog.Ok || dialog.Password is null)
            {
                throw new RpcException(RpcErrorCodes.DialogCancelled, "Credential capture was cancelled.");
            }

            dialog.Password.MakeReadOnly();
            entry = new CredentialEntry(new HeldCredentialInfo
            {
                Identity = request.Identity,
                CapturedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(_config.Daemon.CredentialIdleMinutes),
                Emulated = false
            }, dialog.Password);
        }

        if (_credentials.TryRemove(request.Identity, out var existing))
        {
            existing.Dispose();
        }

        _credentials[request.Identity] = entry;
        AppendAudit(new
        {
            ts = DateTimeOffset.UtcNow,
            kind = "credential_captured",
            identity = request.Identity,
            emulated = entry.Info.Emulated
        });
        BroadcastEvent(new
        {
            @event = "credential_captured",
            identity = request.Identity,
            expiresAt = entry.Info.ExpiresAt,
            emulated = entry.Info.Emulated
        });

        return CloneCredentialInfo(entry.Info);
    }

    private Task<object> DropAsync(DropRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Identity))
        {
            throw new RpcException(RpcErrorCodes.InvalidParams, "drop requires --identity.");
        }

        if (_credentials.TryRemove(request.Identity, out var existing))
        {
            existing.Dispose();
        }

        AppendAudit(new
        {
            ts = DateTimeOffset.UtcNow,
            kind = "credential_dropped",
            identity = request.Identity
        });
        BroadcastEvent(new
        {
            @event = "credential_dropped",
            identity = request.Identity
        });

        return Task.FromResult<object>(new { dropped = true });
    }

    private async Task<SessionRecord> CreateSessionAsync(SessionCreateRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.Identity))
        {
            throw new RpcException(RpcErrorCodes.InvalidParams, "session.create requires --identity.");
        }

        var name = string.IsNullOrWhiteSpace(request.Name)
            ? $"session-{Interlocked.Increment(ref _sessionCounter)}"
            : request.Name!;

        if (_sessions.ContainsKey(name))
        {
            throw new RpcException(RpcErrorCodes.SessionNameTaken, $"Session '{name}' already exists.");
        }

        var session = new SessionRecord
        {
            Id = $"s_{Guid.NewGuid():N}"[..10],
            Name = name,
            Identity = request.Identity,
            NetOnly = request.NetOnly || IdentityHelpers.IsForeignDomain(request.Identity),
            Persistent = request.Persistent,
            Implicit = false,
            State = request.Persistent ? RunspaceState.Starting : RunspaceState.None,
            CreatedAt = DateTimeOffset.UtcNow,
            LastUsedAt = DateTimeOffset.UtcNow,
            BlockCount = 0
        };

        var context = new SessionContext(session);
        if (!_sessions.TryAdd(name, context))
        {
            throw new RpcException(RpcErrorCodes.SessionNameTaken, $"Session '{name}' already exists.");
        }

        try
        {
            if (request.Persistent)
            {
                var credential = GetCredential(request.Identity);
                var worker = CreateWorker(session, credential);
                await worker.StartAsync(cancellationToken);
                context.Worker = worker;
                session.State = RunspaceState.Alive;
                session.RunspacePid = worker.ProcessId;
            }
        }
        catch
        {
            _sessions.TryRemove(name, out _);
            await context.DisposeAsync();
            throw;
        }

        AppendAudit(new
        {
            ts = DateTimeOffset.UtcNow,
            kind = "session_created",
            name = session.Name,
            identity = session.Identity,
            netOnly = session.NetOnly,
            persistent = session.Persistent
        });
        BroadcastEvent(new
        {
            @event = "session_created",
            session = CloneSession(session)
        });

        return CloneSession(session);
    }

    private async Task<object> DestroySessionAsync(SessionDestroyRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new RpcException(RpcErrorCodes.InvalidParams, "session.destroy requires --name.");
        }

        if (!_sessions.TryRemove(request.Name, out var context))
        {
            throw new RpcException(RpcErrorCodes.SessionNotFound, $"Session '{request.Name}' was not found.");
        }

        if (context.Worker is not null)
        {
            await context.Worker.CancelCurrentAsync(CancelReason.SessionDestroyed);
            await context.Worker.DisposeAsync();
        }

        if (context.ActiveBlockId is not null)
        {
            _activeBlocks.TryRemove(context.ActiveBlockId, out _);
        }

        AppendAudit(new
        {
            ts = DateTimeOffset.UtcNow,
            kind = "session_destroyed",
            name = request.Name
        });
        BroadcastEvent(new
        {
            @event = "session_destroyed",
            name = request.Name
        });

        return new { destroyed = true };
    }

    private async Task<BlockRecord> RunAsync(RunRequest request, ChunkSender? chunkSender, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Script))
        {
            throw new RpcException(RpcErrorCodes.InvalidParams, "run requires either --script or --script-file.");
        }

        var context = await ResolveRunSessionAsync(request, cancellationToken);
        var session = context.Session;

        // Auto-capture: if the resolved identity is not the current user and no
        // credential is held yet, prompt for one synchronously instead of failing
        // with CredentialNotHeld. This matches the "no flags = current user, give
        // --identity = run as that user" UX without forcing callers to remember
        // a separate `idmux capture` step before every fresh run.
        await EnsureCredentialAsync(session.Identity, cancellationToken);

        var credential = GetCredential(session.Identity);

        var block = new BlockRecord
        {
            Id = $"b_{Guid.NewGuid():N}"[..10],
            SessionId = session.Id,
            SessionName = session.Name,
            Script = request.Script,
            Args = request.Args?.DeepClone(),
            IdentityUsed = session.Identity,
            StartedAt = DateTimeOffset.UtcNow,
            State = BlockState.Running
        };

        var blockEntry = AddBlock(context, block);
        BroadcastEvent(new
        {
            @event = "block_created",
            session = session.Name,
            block = blockEntry.ToRecord()
        });

        // Subscribe the calling pipe (if any) to live chunks for this block.
        IDisposable? liveSubscription = null;
        if (chunkSender is not null && !request.Detach)
        {
            liveSubscription = blockEntry.AddSubscriber(new TailSubscriber(
                onChunk: (stream, data) =>
                {
                    try
                    {
                        chunkSender(stream, data, cancellationToken).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (IOException)
                    {
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                },
                onCompleted: () => { }));
        }

        if (request.Detach)
        {
            // Fire-and-forget; CLI gets the block summary immediately.
            _ = Task.Run(async () =>
            {
                try
                {
                    await ExecuteBlockAsync(context, blockEntry, request, credential, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    _log.Error(exception, $"Detached run failed for block {block.Id}.");
                }
            });

            return blockEntry.ToRecord();
        }

        try
        {
            return await ExecuteBlockAsync(context, blockEntry, request, credential, cancellationToken);
        }
        finally
        {
            liveSubscription?.Dispose();
        }
    }

    private async Task<BlockRecord> ExecuteBlockAsync(SessionContext context, BlockEntry blockEntry, RunRequest request, CredentialEntry credential, CancellationToken cancellationToken)
    {
        var session = context.Session;
        var block = blockEntry.Record;

        await context.Gate.WaitAsync(cancellationToken);
        try
        {
            session.LastUsedAt = DateTimeOffset.UtcNow;

            PowerShellWorker? worker = null;
            var ownsWorker = false;
            CancellationTokenSource? timeoutCts = null;
            Task? timeoutTask = null;

            try
            {
                if (session.Persistent)
                {
                    worker = await EnsurePersistentWorkerAsync(context, credential, cancellationToken);
                }
                else
                {
                    worker = CreateWorker(session, credential);
                    ownsWorker = true;
                    await worker.StartAsync(cancellationToken);
                }

                context.ActiveBlockId = block.Id;
                session.RunspacePid = worker.ProcessId;
                if (session.Persistent)
                {
                    session.State = RunspaceState.Alive;
                }

                var active = new ActiveExecution(context, worker, ownsWorker);
                _activeBlocks[block.Id] = active;

                if (request.TimeoutSec is > 0)
                {
                    timeoutCts = new CancellationTokenSource();
                    timeoutTask = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(request.TimeoutSec.Value), timeoutCts.Token);
                            if (!timeoutCts.Token.IsCancellationRequested)
                            {
                                active.CancelReason = CancelReason.Timeout;
                                await worker.CancelCurrentAsync(CancelReason.Timeout);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                        }
                    }, cancellationToken);
                }

                var result = await worker.ExecuteAsync(
                    request.Script,
                    request.Args,
                    data => AppendChunk(blockEntry, "stdout", data),
                    data => AppendChunk(blockEntry, "stderr", data),
                    cancellationToken);

                if (result.CancelReason == CancelReason.User)
                {
                    CompleteBlock(blockEntry, BlockState.Cancelled, 1, result.Error);
                    throw new RpcException(RpcErrorCodes.CancelledByUser, "The block was cancelled by the user.", IdmuxJson.ToNode(new { blockId = block.Id }));
                }

                if (result.CancelReason == CancelReason.Timeout)
                {
                    CompleteBlock(blockEntry, BlockState.Failed, 1, result.Error ?? "Script timed out.");
                    throw new RpcException(RpcErrorCodes.ScriptTimeout, "The script exceeded --timeout-sec.", IdmuxJson.ToNode(new { blockId = block.Id }));
                }

                if (result.CancelReason is CancelReason.SessionDestroyed or CancelReason.Shutdown)
                {
                    CompleteBlock(blockEntry, BlockState.Cancelled, 1, result.Error);
                    throw new RpcException(RpcErrorCodes.CancelledByUser, result.Error ?? "The run was interrupted.", IdmuxJson.ToNode(new { blockId = block.Id }));
                }

                CompleteBlock(
                    blockEntry,
                    result.ExitCode == 0 ? BlockState.Completed : BlockState.Failed,
                    result.ExitCode,
                    result.Error);

                return blockEntry.ToRecord();
            }
            catch (RpcException)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (blockEntry.Record.CompletedAt is null)
                {
                    CompleteBlock(blockEntry, BlockState.Failed, 1, exception.Message);
                }

                throw;
            }
            finally
            {
                timeoutCts?.Cancel();
                if (timeoutTask is not null)
                {
                    try
                    {
                        await timeoutTask;
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                if (ownsWorker && worker is not null)
                {
                    await worker.DisposeAsync();
                }
            }
        }
        finally
        {
            context.ActiveBlockId = null;
            _activeBlocks.TryRemove(block.Id, out _);

            if (session.Persistent && context.Worker is not null && context.Worker.ProcessId is null)
            {
                await context.Worker.DisposeAsync();
                context.Worker = null;
                session.State = RunspaceState.Dead;
                session.RunspacePid = null;
            }

            context.Gate.Release();
        }
    }

    private async Task<BlockRecord> TailBlockAsync(BlockTailRequest request, ChunkSender? chunkSender, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Id))
        {
            throw new RpcException(RpcErrorCodes.InvalidParams, "block.tail requires --id.");
        }

        var blockEntry = GetBlockEntry(request.Id);

        // Snapshot the buffered output starting from the requested offsets.
        string stdoutPrefix;
        string stderrPrefix;
        var stdoutOffset = Math.Max(0, request.StdoutOffset);
        var stderrOffset = Math.Max(0, request.StderrOffset);

        lock (blockEntry.Gate)
        {
            stdoutPrefix = blockEntry.Stdout.Length > stdoutOffset
                ? blockEntry.Stdout.ToString(stdoutOffset, blockEntry.Stdout.Length - stdoutOffset)
                : string.Empty;
            stderrPrefix = blockEntry.Stderr.Length > stderrOffset
                ? blockEntry.Stderr.ToString(stderrOffset, blockEntry.Stderr.Length - stderrOffset)
                : string.Empty;
        }

        if (chunkSender is not null)
        {
            if (!string.IsNullOrEmpty(stdoutPrefix))
            {
                await chunkSender("stdout", stdoutPrefix, cancellationToken);
            }

            if (!string.IsNullOrEmpty(stderrPrefix))
            {
                await chunkSender("stderr", stderrPrefix, cancellationToken);
            }
        }

        var alreadyDone = blockEntry.Record.CompletedAt is not null;
        if (!request.Follow || alreadyDone)
        {
            return blockEntry.ToRecord();
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));

        using var subscription = blockEntry.AddSubscriber(new TailSubscriber(
            onChunk: (stream, data) =>
            {
                if (chunkSender is null || string.IsNullOrEmpty(data))
                {
                    return;
                }

                try
                {
                    chunkSender(stream, data, cancellationToken).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                }
                catch (IOException)
                {
                    completion.TrySetResult();
                }
                catch (ObjectDisposedException)
                {
                    completion.TrySetResult();
                }
            },
            onCompleted: () => completion.TrySetResult()));

        try
        {
            await completion.Task;
        }
        catch (OperationCanceledException)
        {
        }

        return blockEntry.ToRecord();
    }

    private List<BlockSummary> ListBlocks(BlockListRequest request)
    {
        IEnumerable<BlockEntry> query = _blocks.Values;
        if (!string.IsNullOrWhiteSpace(request.Session))
        {
            if (!_sessions.ContainsKey(request.Session))
            {
                throw new RpcException(RpcErrorCodes.SessionNotFound, $"Session '{request.Session}' was not found.");
            }

            query = query.Where(block => string.Equals(block.Record.SessionName, request.Session, StringComparison.OrdinalIgnoreCase));
        }

        if (request.Since is not null)
        {
            query = query.Where(block => block.Record.StartedAt >= request.Since.Value);
        }

        return query
            .OrderByDescending(block => block.Record.StartedAt)
            .Take(request.Limit.GetValueOrDefault(100))
            .Select(block => block.ToSummary())
            .ToList();
    }

    private JsonNode? HandleBlockGet(BlockGetRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Id))
        {
            throw new RpcException(RpcErrorCodes.InvalidParams, "block.get requires --id.");
        }

        var entry = GetBlockEntry(request.Id);
        string stdout;
        string stderr;
        lock (entry.Gate)
        {
            stdout = entry.Stdout.ToString();
            stderr = entry.Stderr.ToString();
        }

        return request.Fields?.ToLowerInvariant() switch
        {
            "stdout" => IdmuxJson.ToNode(new { id = request.Id, stdout }),
            "stderr" => IdmuxJson.ToNode(new { id = request.Id, stderr }),
            "result" => IdmuxJson.ToNode(new { id = request.Id, result = entry.Record.Result?.DeepClone() }),
            null or "" or "all" => IdmuxJson.ToNode(entry.ToRecord()),
            _ => throw new RpcException(RpcErrorCodes.InvalidParams, "block.get --field must be stdout, stderr, result, or all.")
        };
    }

    private async Task<object> CancelBlockAsync(BlockCancelRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.Id))
        {
            throw new RpcException(RpcErrorCodes.InvalidParams, "block.cancel requires --id.");
        }

        if (!_activeBlocks.TryGetValue(request.Id, out var active))
        {
            throw new RpcException(RpcErrorCodes.BlockNotFound, $"Block '{request.Id}' is not running.");
        }

        active.CancelReason = CancelReason.User;
        await active.Worker.CancelCurrentAsync(CancelReason.User);

        if (active.Context.Session.Persistent && active.Context.Worker is not null)
        {
            await active.Context.Worker.DisposeAsync();
            active.Context.Worker = null;
            active.Context.Session.State = RunspaceState.Dead;
            active.Context.Session.RunspacePid = null;
        }

        BroadcastEvent(new
        {
            @event = "block_cancel_requested",
            blockId = request.Id
        });
        return new { cancelled = true };
    }

    private object GetDaemonStatus()
    {
        return new
        {
            running = true,
            home = IdmuxPaths.Home,
            daemon = CreateDaemonInfo(),
            credentialCount = _credentials.Count,
            sessionCount = _sessions.Count
        };
    }

    private Task<object> PauseAsync()
    {
        _paused = true;
        AppendAudit(new { ts = DateTimeOffset.UtcNow, kind = "daemon_paused" });
        BroadcastEvent(new { @event = "daemon_paused" });
        return Task.FromResult<object>(new { paused = true });
    }

    private Task<object> ResumeAsync()
    {
        _paused = false;
        AppendAudit(new { ts = DateTimeOffset.UtcNow, kind = "daemon_resumed" });
        BroadcastEvent(new { @event = "daemon_resumed" });
        return Task.FromResult<object>(new { resumed = true });
    }

    private Task<object> StopAsync()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            RequestStop?.Invoke();
        });

        return Task.FromResult<object>(new { stopping = true });
    }

    private static T Parse<T>(JsonNode? node)
    {
        try
        {
            return node is null ? Activator.CreateInstance<T>() : IdmuxJson.Deserialize<T>(node);
        }
        catch (Exception exception)
        {
            throw new RpcException(RpcErrorCodes.InvalidParams, exception.Message);
        }
    }

    private SessionContext GetSessionContext(string name)
    {
        if (!_sessions.TryGetValue(name, out var context))
        {
            throw new RpcException(RpcErrorCodes.SessionNotFound, $"Session '{name}' was not found.");
        }

        return context;
    }

    private async Task<SessionContext> ResolveRunSessionAsync(RunRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(request.Session))
        {
            return GetSessionContext(request.Session);
        }

        // No --session: pick the identity. With --identity, use it verbatim.
        // Without --identity, default to the user the daemon is running as
        // ("run as me"). idmux core does not pick identities by hostname or any
        // other heuristic; identity-selection conventions live in the agent
        // skill that calls idmux, not in the CLI/daemon.
        var identity = string.IsNullOrWhiteSpace(request.Identity)
            ? IdentityHelpers.CurrentUserName
            : request.Identity!;

        var netOnly = request.NetOnly || IdentityHelpers.IsForeignDomain(identity);
        var baseName = IdentityHelpers.DefaultSessionName(identity, netOnly, _config);

        // Never reuse a session that belongs to a different identity. If the
        // base name is already held by another identity (for example two
        // identities share a configured display name), use the next free
        // numbered name instead.
        for (var attempt = 1; ; attempt++)
        {
            var name = attempt == 1 ? baseName : $"{baseName}-{attempt}";
            var context = _sessions.GetOrAdd(name, _ => new SessionContext(new SessionRecord
            {
                Id = $"s_{Guid.NewGuid():N}"[..10],
                Name = name,
                Identity = identity,
                NetOnly = netOnly,
                Persistent = false,
                Implicit = true,
                State = RunspaceState.None,
                CreatedAt = DateTimeOffset.UtcNow,
                LastUsedAt = DateTimeOffset.UtcNow
            }));

            if (context.Session.Implicit
                && context.Session.NetOnly == netOnly
                && IdentityHelpers.SameIdentity(context.Session.Identity, identity))
            {
                return context;
            }
        }
    }

    /// <summary>
    /// Ensure a credential is held for <paramref name="identity"/> before a run
    /// uses it. For the current user this is a no-op (the daemon's own logon
    /// token is inherited). For any other identity that has no credential
    /// cached, this triggers the same WPF dialog that <c>idmux capture</c>
    /// would, runs it synchronously, and caches the result. Existing
    /// non-expired credentials are kept as-is.
    /// </summary>
    private async Task EnsureCredentialAsync(string identity, CancellationToken cancellationToken)
    {
        if (IdentityHelpers.IsCurrentUser(identity))
        {
            return;
        }

        if (_credentials.ContainsKey(identity))
        {
            return;
        }

        await CaptureAsync(new CaptureRequest
        {
            Identity = identity,
            Caption = $"Auto-capture for run as {identity}"
        }, cancellationToken);
    }

    private BlockEntry AddBlock(SessionContext context, BlockRecord block)
    {
        var entry = new BlockEntry(block);
        _blocks[block.Id] = entry;

        context.Session.BlockCount += 1;

        var overflow = _blocks.Values
            .Where(value => string.Equals(value.Record.SessionName, context.Session.Name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(value => value.Record.StartedAt)
            .Skip(_config.Daemon.BlockBufferSize)
            .ToList();

        foreach (var stale in overflow)
        {
            _blocks.TryRemove(stale.Record.Id, out _);
        }

        return entry;
    }

    private async Task<PowerShellWorker> EnsurePersistentWorkerAsync(SessionContext context, CredentialEntry credential, CancellationToken cancellationToken)
    {
        if (context.Worker is { ProcessId: not null } worker)
        {
            return worker;
        }

        var newWorker = CreateWorker(context.Session, credential);
        await newWorker.StartAsync(cancellationToken);
        context.Worker = newWorker;
        context.Session.State = RunspaceState.Alive;
        context.Session.RunspacePid = newWorker.ProcessId;
        return newWorker;
    }

    private PowerShellWorker CreateWorker(SessionRecord session, CredentialEntry credential)
    {
        var emulate = credential.Info.Emulated || string.Equals(_config.Development.ExecutionMode, "emulate", StringComparison.OrdinalIgnoreCase);
        return PowerShellWorker.Create(session.Identity, credential.Password, emulate, _config.Development.ExecutionMode, _log, session.Persistent, session.NetOnly);
    }

    private CredentialEntry GetCredential(string identity)
    {
        // The daemon is already running as the current user. Asking it to spawn
        // a worker "as me" needs no captured password — passing a null/empty
        // SecureString makes ApplyCredentials skip CreateProcessWithLogonW so
        // the child pwsh inherits the daemon's logon token. We synthesise a
        // throwaway entry for this case so the rest of the run pipeline (and
        // /status, audit log, web UI) works uniformly.
        if (IdentityHelpers.IsCurrentUser(identity))
        {
            var info = new HeldCredentialInfo
            {
                Identity = IdentityHelpers.CurrentUserName,
                CapturedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.MaxValue,
                Emulated = false
            };

            var entry = new CredentialEntry(info, new SecureString());
            entry.Password.MakeReadOnly();
            return entry;
        }

        if (!_credentials.TryGetValue(identity, out var credential))
        {
            throw new RpcException(
                RpcErrorCodes.CredentialNotHeld,
                $"Credential for '{identity}' is not currently held.",
                IdmuxJson.ToNode(new { identity, hint = "call `capture` first" }));
        }

        credential.Info.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(_config.Daemon.CredentialIdleMinutes);
        return credential;
    }

    private BlockEntry GetBlockEntry(string id)
    {
        if (!_blocks.TryGetValue(id, out var block))
        {
            throw new RpcException(RpcErrorCodes.BlockNotFound, $"Block '{id}' was not found.");
        }

        return block;
    }

    private HeldCredentialInfo CreateHeldCredentialInfo(string identity, bool emulated)
    {
        return new HeldCredentialInfo
        {
            Identity = identity,
            CapturedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(_config.Daemon.CredentialIdleMinutes),
            Emulated = emulated
        };
    }

    private static HeldCredentialInfo CloneCredentialInfo(HeldCredentialInfo info)
    {
        return new HeldCredentialInfo
        {
            Identity = info.Identity,
            CapturedAt = info.CapturedAt,
            ExpiresAt = info.ExpiresAt,
            Emulated = info.Emulated
        };
    }

    private bool ShouldEmulateCapture()
    {
        return _config.Development.CaptureWithoutPrompt
            || string.Equals(_config.Development.ExecutionMode, "emulate", StringComparison.OrdinalIgnoreCase);
    }

    private void AppendChunk(BlockEntry block, string stream, string data)
    {
        if (string.IsNullOrEmpty(data))
        {
            return;
        }

        lock (block.Gate)
        {
            if (stream == "stdout")
            {
                block.Stdout.Append(data);
            }
            else
            {
                block.Stderr.Append(data);
            }
        }

        block.NotifyChunk(stream, data);

        BroadcastEvent(new
        {
            @event = "block_chunk",
            blockId = block.Record.Id,
            stream,
            data
        });
    }

    private void CompleteBlock(BlockEntry block, BlockState state, int exitCode, string? errorMessage)
    {
        block.Record.CompletedAt = DateTimeOffset.UtcNow;
        block.Record.State = state;
        block.Record.ExitCode = exitCode;
        block.Record.ErrorMessage = errorMessage;

        string? stdoutSnapshot = null;
        string? stderrSnapshot = null;
        if (_config.Daemon.AuditLogBodies)
        {
            lock (block.Gate)
            {
                stdoutSnapshot = block.Stdout.ToString();
                stderrSnapshot = block.Stderr.ToString();
            }
        }

        AppendAudit(new
        {
            ts = DateTimeOffset.UtcNow,
            kind = "block_completed",
            session = block.Record.SessionName,
            identity = block.Record.IdentityUsed,
            exitCode,
            state,
            durationMs = (block.Record.CompletedAt.Value - block.Record.StartedAt).TotalMilliseconds,
            scriptFirstLine = block.Record.Script.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(),
            stdout = stdoutSnapshot,
            stderr = stderrSnapshot
        });
        BroadcastEvent(new
        {
            @event = "block_completed",
            blockId = block.Record.Id,
            session = block.Record.SessionName,
            exitCode,
            state,
            durationMs = block.Record.CompletedAt is null ? 0 : (block.Record.CompletedAt.Value - block.Record.StartedAt).TotalMilliseconds
        });

        block.NotifyCompleted();
    }

    private SessionRecord CloneSession(SessionRecord session)
    {
        return new SessionRecord
        {
            Id = session.Id,
            Name = session.Name,
            Identity = session.Identity,
            NetOnly = session.NetOnly,
            Persistent = session.Persistent,
            Implicit = session.Implicit,
            State = session.State,
            RunspacePid = session.RunspacePid,
            CreatedAt = session.CreatedAt,
            LastUsedAt = session.LastUsedAt,
            BlockCount = session.BlockCount
        };
    }

    private DaemonStatusInfo CreateDaemonInfo()
    {
        return new DaemonStatusInfo
        {
            Version = typeof(DaemonRuntime).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            StartedAt = _startedAt,
            IdleShutdownAt = _lastRequestAt.AddMinutes(_config.Daemon.IdleTimeoutMinutes),
            Paused = _paused,
            PipeName = IdmuxPaths.CurrentPipeName,
            WebPort = _config.Daemon.WebPort
        };
    }

    private void Touch()
    {
        _lastRequestAt = DateTimeOffset.UtcNow;
    }

    private void SweepExpiredCredentials()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _credentials.ToArray())
        {
            if (pair.Value.Info.ExpiresAt <= now && _credentials.TryRemove(pair.Key, out var removed))
            {
                removed.Dispose();
                BroadcastEvent(new
                {
                    @event = "credential_dropped",
                    identity = pair.Key
                });
            }
        }
    }

    private void TryIdleShutdown()
    {
        if (_activeBlocks.IsEmpty && DateTimeOffset.UtcNow - _lastRequestAt >= TimeSpan.FromMinutes(_config.Daemon.IdleTimeoutMinutes))
        {
            _log.Info("Idle timeout reached; stopping daemon.");
            RequestStop?.Invoke();
        }
    }

    private void AppendAudit(object payload)
    {
        if (!_config.Daemon.AuditLogEnabled)
        {
            return;
        }

        var path = Path.Combine(IdmuxPaths.AuditDirectory, $"{DateTime.UtcNow:yyyy-MM-dd}.jsonl");
        File.AppendAllText(path, IdmuxJson.ToNode(payload)!.ToJsonString(IdmuxJson.Options) + Environment.NewLine, Encoding.UTF8);
    }

    private void BroadcastEvent(object payload)
    {
        var serialized = IdmuxJson.ToNode(payload)?.ToJsonString(IdmuxJson.Options);
        if (serialized is null)
        {
            return;
        }

        lock (_subscriberGate)
        {
            foreach (var writer in _subscribers.Values)
            {
                _ = writer.Writer.TryWrite(serialized);
            }
        }
    }

    public readonly record struct ChannelSubscription(Guid Id, ChannelReader<string> Reader);

    private sealed class CredentialEntry(HeldCredentialInfo info, SecureString password) : IDisposable
    {
        public HeldCredentialInfo Info = info;
        public SecureString Password { get; } = password;

        public void Dispose()
        {
            Password.Dispose();
        }
    }

    private sealed class SessionContext(SessionRecord session) : IAsyncDisposable
    {
        public SessionRecord Session { get; } = session;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public PowerShellWorker? Worker { get; set; }
        public string? ActiveBlockId { get; set; }

        public async ValueTask DisposeAsync()
        {
            if (Worker is not null)
            {
                await Worker.DisposeAsync();
            }

            Gate.Dispose();
        }
    }

    private sealed class ActiveExecution(SessionContext context, PowerShellWorker worker, bool ownsWorker)
    {
        public SessionContext Context { get; } = context;
        public PowerShellWorker Worker { get; } = worker;
        public bool OwnsWorker { get; } = ownsWorker;
        public CancelReason CancelReason { get; set; }
    }

    private sealed class BlockEntry(BlockRecord record)
    {
        private readonly object _subscriberGate = new();
        private readonly List<TailSubscriber> _subscribers = [];
        private bool _completed;

        public BlockRecord Record { get; } = record;
        public StringBuilder Stdout { get; } = new();
        public StringBuilder Stderr { get; } = new();
        public object Gate => _subscriberGate;

        public IDisposable AddSubscriber(TailSubscriber subscriber)
        {
            bool fireCompleted;
            lock (_subscriberGate)
            {
                _subscribers.Add(subscriber);
                fireCompleted = _completed;
            }

            if (fireCompleted)
            {
                try
                {
                    subscriber.OnCompleted?.Invoke();
                }
                catch
                {
                }
            }

            return new SubscriberHandle(this, subscriber);
        }

        public void NotifyChunk(string stream, string data)
        {
            TailSubscriber[] copy;
            lock (_subscriberGate)
            {
                copy = [.. _subscribers];
            }

            foreach (var subscriber in copy)
            {
                try
                {
                    subscriber.OnChunk?.Invoke(stream, data);
                }
                catch
                {
                }
            }
        }

        public void NotifyCompleted()
        {
            TailSubscriber[] copy;
            lock (_subscriberGate)
            {
                _completed = true;
                copy = [.. _subscribers];
            }

            foreach (var subscriber in copy)
            {
                try
                {
                    subscriber.OnCompleted?.Invoke();
                }
                catch
                {
                }
            }
        }

        private void Remove(TailSubscriber subscriber)
        {
            lock (_subscriberGate)
            {
                _subscribers.Remove(subscriber);
            }
        }

        private sealed class SubscriberHandle(BlockEntry owner, TailSubscriber subscriber) : IDisposable
        {
            public void Dispose() => owner.Remove(subscriber);
        }

        public BlockRecord ToRecord()
        {
            string stdout;
            string stderr;
            lock (_subscriberGate)
            {
                stdout = Stdout.ToString();
                stderr = Stderr.ToString();
            }

            return new BlockRecord
            {
                Id = Record.Id,
                SessionId = Record.SessionId,
                SessionName = Record.SessionName,
                Script = Record.Script,
                Args = Record.Args?.DeepClone(),
                IdentityUsed = Record.IdentityUsed,
                StartedAt = Record.StartedAt,
                CompletedAt = Record.CompletedAt,
                State = Record.State,
                ExitCode = Record.ExitCode,
                Stdout = stdout,
                Stderr = stderr,
                Result = Record.Result?.DeepClone(),
                ErrorMessage = Record.ErrorMessage
            };
        }

        public BlockSummary ToSummary()
        {
            int stdoutLen;
            int stderrLen;
            lock (_subscriberGate)
            {
                stdoutLen = Stdout.Length;
                stderrLen = Stderr.Length;
            }

            return new BlockSummary
            {
                Id = Record.Id,
                SessionId = Record.SessionId,
                SessionName = Record.SessionName,
                IdentityUsed = Record.IdentityUsed,
                StartedAt = Record.StartedAt,
                CompletedAt = Record.CompletedAt,
                State = Record.State,
                ExitCode = Record.ExitCode,
                ScriptPreview = Record.Script.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? Record.Script,
                StdoutLength = stdoutLen,
                StderrLength = stderrLen
            };
        }
    }

    internal sealed class TailSubscriber(Action<string, string>? onChunk, Action? onCompleted)
    {
        public Action<string, string>? OnChunk { get; } = onChunk;
        public Action? OnCompleted { get; } = onCompleted;
    }
}
