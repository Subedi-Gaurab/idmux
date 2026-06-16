using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Idmux.Cli;
using Idmux.Core;

return await CliProgram.RunAsync(args);

internal static class CliProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;
        }
        catch
        {
            // Best effort \u2014 redirected handles can throw.
        }

        // Top-level help routing.
        //   idmux                              -> root help
        //   idmux help                         -> root help
        //   idmux --help | -h | -? | /?         -> root help
        //   idmux help <cmd> [<sub>]           -> topic help
        if (args.Length == 0 || IsHelpToken(args[0]))
        {
            if (args.Length >= 2 && string.Equals(args[0], "help", StringComparison.OrdinalIgnoreCase))
            {
                var sub = args.Length >= 3 ? args[2] : null;
                if (HelpText.WriteCommand(args[1], sub))
                {
                    return 0;
                }

                Console.Error.WriteLine($"idmux: no help topic for '{args[1]}{(sub is null ? string.Empty : " " + sub)}'.");
                HelpText.WriteRoot();
                return 1;
            }

            HelpText.WriteRoot();
            return 0;
        }

        if (args[0] is "--version" or "version")
        {
            // Allow `idmux version --help` too.
            if (args.Length >= 2 && IsHelpToken(args[1]))
            {
                HelpText.WriteCommand("version", null);
                return 0;
            }

            Console.Out.WriteLine(typeof(CliProgram).Assembly.GetName().Version?.ToString() ?? "0.0.0");
            return 0;
        }

        var config = IdmuxConfigLoader.Load();
        var parsed = ParsedArguments.Parse(args.Skip(1));
        var json = parsed.Has("json");
        var launcher = new DaemonLauncher();
        var client = new PipeRpcClient(launcher);
        var command = args[0];

        // Per-command help routing.
        //   idmux <cmd> --help | -h            -> command help
        //   idmux <cmd> <sub> --help           -> subcommand help
        //   idmux session | blocks | daemon    (bare) -> command help
        if (HasHelpFlag(parsed) || args.Skip(1).Any(IsHelpToken))
        {
            string? subcommand = null;
            if (IsCommandWithSubcommands(command))
            {
                subcommand = parsed.Positionals.FirstOrDefault(p => !IsHelpToken(p));
            }

            if (HelpText.WriteCommand(command, subcommand))
            {
                return 0;
            }

            Console.Error.WriteLine($"idmux: no help topic for '{command}{(subcommand is null ? string.Empty : " " + subcommand)}'.");
            HelpText.WriteRoot();
            return 1;
        }

        if (IsCommandWithSubcommands(command) && parsed.Positionals.Count == 0)
        {
            // Bare `idmux session`, `idmux blocks`, or `idmux daemon` -> show
            // that command's help instead of failing with "requires a subcommand".
            HelpText.WriteCommand(command, null);
            return 0;
        }

        try
        {
            return command switch
            {
                "capture" => await InvokeSimpleAsync(client, RpcMethods.Capture, new CaptureRequest
                {
                    Identity = parsed.Require("identity"),
                    Caption = parsed.Get("caption")
                }, ensureRunning: true, json),

                "drop" => await InvokeSimpleAsync(client, RpcMethods.Drop, new DropRequest
                {
                    Identity = parsed.Require("identity")
                }, ensureRunning: true, json),

                "status" => await InvokeSimpleAsync(client, RpcMethods.Status, null, ensureRunning: true, json),

                "run" => await HandleRunAsync(client, parsed, json),
                "serve" => await HandleServeAsync(client, launcher, config, parsed, json, openBrowser: false),
                "session" => await HandleSessionAsync(client, args.Skip(1).ToArray(), json),
                "blocks" => await HandleBlocksAsync(client, args.Skip(1).ToArray(), json),
                "daemon" => await HandleDaemonAsync(client, launcher, args.Skip(1).ToArray(), json),
                "ui" => await HandleServeAsync(client, launcher, config, parsed, json, openBrowser: true),
                _ => Fail($"Unknown command '{command}'.")
            };
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine($"idmux: {exception.Message}");
            return 1;
        }
        catch (FileNotFoundException exception)
        {
            Console.Error.WriteLine($"idmux: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> HandleRunAsync(PipeRpcClient client, ParsedArguments parsed, bool json)
    {
        var script = parsed.Get("script");
        if (string.IsNullOrWhiteSpace(script))
        {
            var scriptFile = parsed.Get("script-file");
            if (string.IsNullOrWhiteSpace(scriptFile))
            {
                return Fail("run requires --script or --script-file.");
            }

            script = await File.ReadAllTextAsync(scriptFile);
        }

        JsonNode? scriptArgs = null;
        var argsJson = parsed.Get("args-json");
        if (!string.IsNullOrWhiteSpace(argsJson))
        {
            scriptArgs = JsonNode.Parse(argsJson);
        }

        var detach = parsed.Has("detach");
        var pty = parsed.Has("pty");
        var netOnly = parsed.Has("netonly");
        var identity = parsed.Get("identity");
        var session = parsed.Get("session");

        // Auto-capture: if the caller asked for a specific identity that's not
        // the current user and is not pinned to an existing --session, make
        // sure a credential is held before we issue `run` so the request
        // doesn't fail with CredentialNotHeld. Auto-capture also happens
        // server-side as a safety net (DaemonRuntime.EnsureCredentialAsync),
        // but doing it here lets us print a clear "waiting for password
        // dialog" hint to stderr instead of a silent stall.
        if (string.IsNullOrWhiteSpace(session)
            && !string.IsNullOrWhiteSpace(identity)
            && !IdentityHelpers.IsCurrentUser(identity))
        {
            await EnsureCredentialOrPromptAsync(client, identity!);
        }

        var request = new RunRequest
        {
            Session = session,
            Identity = identity,
            Script = script!,
            Args = scriptArgs,
            TimeoutSec = TryParseInt(parsed.Get("timeout-sec")),
            Detach = detach,
            Pty = pty,
            NetOnly = netOnly
        };

        // For non-detached runs, stream raw stdout/stderr bytes through the chunk callback.
        // For detached runs, the daemon returns immediately with the block summary.
        Action<string, string>? onChunk = (detach || json) ? null : (stream, data) =>
        {
            if (stream == "stderr")
            {
                Console.Error.Write(data);
            }
            else
            {
                Console.Out.Write(data);
            }
        };

        var response = await client.SendAsync(RpcMethods.Run, request, ensureRunning: true, onChunk, CancellationToken.None);
        if (response.Error is not null)
        {
            if (json)
            {
                OutputWriter.WriteJsonError(response.Error);
            }
            else
            {
                OutputWriter.WriteShortError(response.Error);
            }

            return 2;
        }

        if (json)
        {
            OutputWriter.WriteJson(response.Result);
            return GetRunExitCode(response.Result);
        }

        if (detach)
        {
            // Print just the block id so it's pipeable to other commands.
            var blockId = (response.Result as JsonObject)?["id"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(blockId))
            {
                Console.Out.WriteLine(blockId);
            }

            return 0;
        }

        // Streaming foreground run: bytes already went out via onChunk.
        // Honor the script's exit code.
        return GetRunExitCode(response.Result);
    }

    private static async Task<int> HandleSessionAsync(PipeRpcClient client, string[] args, bool json)
    {
        if (args.Length == 0)
        {
            return Fail("session requires a subcommand.");
        }

        var subcommand = args[0];
        var parsed = ParsedArguments.Parse(args.Skip(1));

        // --identity is optional on `session create`; when absent we default to
        // the current user (DOMAIN\Username), matching `idmux run` without
        // --identity. We only need to auto-capture a credential when the
        // caller explicitly asked for a different identity than the daemon's
        // own logon.
        string? createIdentity = null;
        if (subcommand == "create")
        {
            createIdentity = parsed.Get("identity");
            if (string.IsNullOrWhiteSpace(createIdentity))
            {
                createIdentity = IdentityHelpers.CurrentUserName;
            }

            if (!IdentityHelpers.IsCurrentUser(createIdentity))
            {
                await EnsureCredentialOrPromptAsync(client, createIdentity);
            }
        }

        return subcommand switch
        {
            // Sessions are always persistent by definition (the whole point of a
            // named session is that variables, modules, and connections survive
            // across runs). The legacy `--persistent` flag is gone.
            "create" => await InvokeSimpleAsync(client, RpcMethods.SessionCreate, new SessionCreateRequest
            {
                Name = parsed.Get("name"),
                Identity = createIdentity!,
                Persistent = true,
                NetOnly = parsed.Has("netonly")
            }, ensureRunning: true, json),

            "list" => await InvokeSimpleAsync(client, RpcMethods.SessionList, null, ensureRunning: true, json),

            "destroy" => await InvokeSimpleAsync(client, RpcMethods.SessionDestroy, new SessionDestroyRequest
            {
                Name = parsed.Require("name")
            }, ensureRunning: true, json),

            _ => Fail($"Unknown session subcommand '{subcommand}'.")
        };
    }

    private static async Task<int> HandleBlocksAsync(PipeRpcClient client, string[] args, bool json)
    {
        if (args.Length == 0)
        {
            return Fail("blocks requires a subcommand.");
        }

        var subcommand = args[0];
        var parsed = ParsedArguments.Parse(args.Skip(1));
        return subcommand switch
        {
            "list" => await InvokeSimpleAsync(client, RpcMethods.BlockList, new BlockListRequest
            {
                Session = parsed.Get("session"),
                Since = DateTimeOffset.TryParse(parsed.Get("since"), out var since) ? since : null,
                Limit = TryParseInt(parsed.Get("limit"))
            }, ensureRunning: true, json),

            "get" => await InvokeSimpleAsync(client, RpcMethods.BlockGet, new BlockGetRequest
            {
                Id = parsed.Require("id"),
                Fields = parsed.Get("field")
            }, ensureRunning: true, json),

            "cancel" => await InvokeSimpleAsync(client, RpcMethods.BlockCancel, new BlockCancelRequest
            {
                Id = parsed.Require("id")
            }, ensureRunning: true, json),

            "tail" => await HandleBlocksTailAsync(client, parsed, json),

            _ => Fail($"Unknown blocks subcommand '{subcommand}'.")
        };
    }

    private static async Task<int> HandleBlocksTailAsync(PipeRpcClient client, ParsedArguments parsed, bool json)
    {
        var request = new BlockTailRequest
        {
            Id = parsed.Require("id"),
            Follow = parsed.Has("follow"),
            StdoutOffset = TryParseInt(parsed.Get("stdout-offset")) ?? 0,
            StderrOffset = TryParseInt(parsed.Get("stderr-offset")) ?? 0
        };

        Action<string, string>? onChunk = json ? null : (stream, data) =>
        {
            if (stream == "stderr")
            {
                Console.Error.Write(data);
            }
            else
            {
                Console.Out.Write(data);
            }
        };

        var response = await client.SendAsync(RpcMethods.BlockTail, request, ensureRunning: true, onChunk, CancellationToken.None);
        if (response.Error is not null)
        {
            if (json)
            {
                OutputWriter.WriteJsonError(response.Error);
            }
            else
            {
                OutputWriter.WriteShortError(response.Error);
            }

            return 2;
        }

        if (json)
        {
            OutputWriter.WriteJson(response.Result);
        }

        return GetRunExitCode(response.Result);
    }

    private static async Task<int> HandleDaemonAsync(PipeRpcClient client, DaemonLauncher launcher, string[] args, bool json)
    {
        if (args.Length == 0)
        {
            return Fail("daemon requires a subcommand.");
        }

        var subcommand = args[0];
        var parsed = ParsedArguments.Parse(args.Skip(1));
        return subcommand switch
        {
            "start" => await HandleDaemonStartAsync(client, launcher, json),
            "status" => await HandleDaemonStatusAsync(client, json),
            "stop" => await HandleDaemonStopAsync(client, json),
            "pause" => await InvokeSimpleAsync(client, RpcMethods.DaemonPause, null, ensureRunning: false, json),
            "resume" => await InvokeSimpleAsync(client, RpcMethods.DaemonResume, null, ensureRunning: false, json),
            _ => Fail($"Unknown daemon subcommand '{subcommand}'.")
        };
    }

    private static async Task<int> HandleDaemonStartAsync(PipeRpcClient client, DaemonLauncher launcher, bool json)
    {
        var alreadyRunning = await launcher.IsRunningAsync(CancellationToken.None);
        var started = false;
        if (!alreadyRunning)
        {
            started = await launcher.EnsureRunningAsync(CancellationToken.None);
        }

        var response = await client.SendAsync(RpcMethods.DaemonStatus, null, ensureRunning: false, CancellationToken.None);
        if (response.Error is not null)
        {
            return EmitErrorAndExit(response.Error, json);
        }

        var status = response.Result as JsonObject ?? [];
        status["started"] = started;
        status["alreadyRunning"] = alreadyRunning;

        if (json)
        {
            OutputWriter.WriteJson(status);
        }
        else
        {
            var port = status["daemon"]?["webPort"]?.GetValue<int?>() ?? 0;
            Console.Out.WriteLine($"daemon running on pipe {IdmuxPaths.CurrentPipeName} (port {port})");
        }

        return 0;
    }

    private static async Task<int> HandleDaemonStatusAsync(PipeRpcClient client, bool json)
    {
        if (!await client.IsRunningAsync(CancellationToken.None))
        {
            if (json)
            {
                OutputWriter.WriteJson(IdmuxJson.ToNode(new
                {
                    running = false,
                    pipeName = IdmuxPaths.CurrentPipeName
                }));
            }
            else
            {
                Console.Out.WriteLine("daemon stopped");
            }

            return 0;
        }

        return await InvokeSimpleAsync(client, RpcMethods.DaemonStatus, null, ensureRunning: false, json);
    }

    private static async Task<int> HandleDaemonStopAsync(PipeRpcClient client, bool json)
    {
        if (!await client.IsRunningAsync(CancellationToken.None))
        {
            if (json)
            {
                OutputWriter.WriteJson(IdmuxJson.ToNode(new
                {
                    stopping = false,
                    alreadyStopped = true
                }));
            }
            else
            {
                Console.Out.WriteLine("daemon already stopped");
            }

            return 0;
        }

        return await InvokeSimpleAsync(client, RpcMethods.DaemonStop, null, ensureRunning: false, json);
    }

    private static async Task<int> HandleServeAsync(PipeRpcClient client, DaemonLauncher launcher, IdmuxConfig config, ParsedArguments parsed, bool json, bool openBrowser)
    {
        var requestedPort = TryParseInt(parsed.Get("port"));
        if (parsed.Has("port") && requestedPort is null)
        {
            return Fail("serve --port requires a valid integer.");
        }

        if (requestedPort is <= 0 or > 65535)
        {
            return Fail("serve --port must be between 1 and 65535.");
        }

        var daemonWasRunning = await launcher.IsRunningAsync(CancellationToken.None);
        if (requestedPort.HasValue)
        {
            if (config.Daemon.WebPort != requestedPort.Value)
            {
                config.Daemon.WebPort = requestedPort.Value;
                IdmuxConfigLoader.Save(config);
            }

            if (daemonWasRunning)
            {
                var statusResponse = await client.SendAsync(RpcMethods.DaemonStatus, null, ensureRunning: false, CancellationToken.None);
                if (statusResponse.Error is not null)
                {
                    return EmitErrorAndExit(statusResponse.Error, json);
                }

                var activePort = TryGetDaemonPort(statusResponse.Result);
                if (activePort != requestedPort.Value)
                {
                    var stopResponse = await client.SendAsync(RpcMethods.DaemonStop, null, ensureRunning: false, CancellationToken.None);
                    if (stopResponse.Error is not null)
                    {
                        return EmitErrorAndExit(stopResponse.Error, json);
                    }

                    await WaitForDaemonStateAsync(launcher, running: false, CancellationToken.None);
                    daemonWasRunning = false;
                }
            }
        }

        var started = false;
        if (!await launcher.IsRunningAsync(CancellationToken.None))
        {
            started = await launcher.EnsureRunningAsync(CancellationToken.None);
        }

        var response = await client.SendAsync(RpcMethods.DaemonStatus, null, ensureRunning: false, CancellationToken.None);
        if (response.Error is not null)
        {
            return EmitErrorAndExit(response.Error, json);
        }

        var activeDaemonPort = TryGetDaemonPort(response.Result) ?? config.Daemon.WebPort;
        var url = $"http://127.0.0.1:{activeDaemonPort}";
        var suppressed = string.Equals(Environment.GetEnvironmentVariable("IDMUX_UI_NO_OPEN"), "1", StringComparison.OrdinalIgnoreCase);
        var opened = false;
        if (openBrowser && !json && !suppressed)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            })?.Dispose();
            opened = true;
        }

        if (json)
        {
            OutputWriter.WriteJson(IdmuxJson.ToNode(new
            {
                url,
                opened = opened || suppressed,
                started,
                alreadyRunning = daemonWasRunning && !started,
                configFile = IdmuxPaths.ConfigFile,
                daemon = response.Result?["daemon"]?.DeepClone()
            }));
        }
        else
        {
            Console.Out.WriteLine(url);
        }

        return 0;
    }

    private static async Task<int> InvokeSimpleAsync(
        PipeRpcClient client,
        string method,
        object? parameters,
        bool ensureRunning,
        bool json)
    {
        var response = await client.SendAsync(method, parameters, ensureRunning, CancellationToken.None);
        if (response.Error is not null)
        {
            return EmitErrorAndExit(response.Error, json);
        }

        if (json)
        {
            OutputWriter.WriteJson(response.Result);
        }
        else
        {
            WritePlainResult(method, response.Result);
        }

        return 0;
    }

    private static int EmitErrorAndExit(JsonRpcError error, bool json)
    {
        if (json)
        {
            OutputWriter.WriteJsonError(error);
        }
        else
        {
            OutputWriter.WriteShortError(error);
        }

        return 2;
    }

    private static void WritePlainResult(string method, JsonNode? result)
    {
        switch (method)
        {
            case RpcMethods.Capture:
                if (result is JsonObject identityObj && identityObj["identity"]?.GetValue<string>() is { Length: > 0 } identity)
                {
                    Console.Out.WriteLine(identity);
                }

                return;

            case RpcMethods.Drop:
                if (result is JsonObject droppedObj && droppedObj["dropped"]?.GetValue<bool?>() == true)
                {
                    Console.Out.WriteLine("dropped");
                }

                return;

            case RpcMethods.SessionCreate:
                if (result is JsonObject sessionObj && sessionObj["name"]?.GetValue<string>() is { Length: > 0 } sessionName)
                {
                    Console.Out.WriteLine(sessionName);
                }

                return;

            case RpcMethods.SessionList:
                if (result is JsonArray sessions)
                {
                    foreach (var session in sessions.OfType<JsonObject>())
                    {
                        var name = session["name"]?.GetValue<string>();
                        var id = session["identity"]?.GetValue<string>();
                        var state = session["state"]?.GetValue<string>();
                        Console.Out.WriteLine($"{name}\t{state}\t{id}");
                    }
                }

                return;

            case RpcMethods.SessionDestroy:
            case RpcMethods.BlockCancel:
                Console.Out.WriteLine("ok");
                return;

            case RpcMethods.BlockList:
                if (result is JsonArray blocks)
                {
                    foreach (var block in blocks.OfType<JsonObject>())
                    {
                        var id = block["id"]?.GetValue<string>();
                        var state = block["state"]?.GetValue<string>();
                        var session = block["sessionName"]?.GetValue<string>();
                        var preview = block["scriptPreview"]?.GetValue<string>() ?? string.Empty;
                        Console.Out.WriteLine($"{id}\t{state}\t{session}\t{preview}");
                    }
                }

                return;

            case RpcMethods.BlockGet:
                if (result is JsonObject blockGet)
                {
                    var stdout = blockGet["stdout"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(stdout))
                    {
                        Console.Out.Write(stdout);
                    }

                    var stderr = blockGet["stderr"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(stderr))
                    {
                        Console.Error.Write(stderr);
                    }
                }

                return;

            case RpcMethods.Status:
                if (result is JsonObject statusObj)
                {
                    var daemon = statusObj["daemon"]?.AsObject();
                    var port = daemon?["webPort"]?.GetValue<int?>() ?? 0;
                    var paused = daemon?["paused"]?.GetValue<bool?>() == true ? " (paused)" : string.Empty;
                    Console.Out.WriteLine($"daemon {(port > 0 ? $"port {port}" : "running")}{paused}");
                    Console.Out.WriteLine($"credentials\t{statusObj["credentials"]?.AsArray().Count ?? 0}");
                    Console.Out.WriteLine($"sessions\t{statusObj["sessions"]?.AsArray().Count ?? 0}");
                    Console.Out.WriteLine($"recent-blocks\t{statusObj["recentBlocks"]?.AsArray().Count ?? 0}");
                }

                return;

            case RpcMethods.DaemonStatus:
                if (result is JsonObject statusInfo)
                {
                    var port = statusInfo["daemon"]?["webPort"]?.GetValue<int?>() ?? 0;
                    Console.Out.WriteLine($"running\tport {port}");
                }

                return;

            case RpcMethods.DaemonPause:
                Console.Out.WriteLine("paused");
                return;

            case RpcMethods.DaemonResume:
                Console.Out.WriteLine("resumed");
                return;

            case RpcMethods.DaemonStop:
                Console.Out.WriteLine("stopping");
                return;

            default:
                if (result is not null)
                {
                    OutputWriter.WriteJson(result);
                }

                return;
        }
    }

    private static int GetRunExitCode(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            return 0;
        }

        return obj["exitCode"]?.GetValue<int?>() ?? 0;
    }

    private static int? TryParseInt(string? value)
    {
        return int.TryParse(value, out var parsed) ? parsed : null;
    }

    private static int? TryGetDaemonPort(JsonNode? result)
    {
        return result?["daemon"]?["webPort"]?.GetValue<int?>();
    }

    private static async Task WaitForDaemonStateAsync(DaemonLauncher launcher, bool running, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await launcher.IsRunningAsync(cancellationToken) == running)
            {
                return;
            }

            await Task.Delay(200, cancellationToken);
        }

        throw new InvalidOperationException(running
            ? "Timed out waiting for the daemon to start."
            : "Timed out waiting for the daemon to stop.");
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"idmux: {message}");
        return 1;
    }

    /// <summary>
    /// If <paramref name="identity"/> is not currently held by the daemon, print a
    /// one-line heads-up to stderr and trigger the WPF capture dialog before
    /// returning. The next <c>run</c> RPC will then have a fresh credential to
    /// use. Failures (cancelled dialog, etc.) propagate to the caller via the
    /// returned exit code.
    /// </summary>
    private static async Task EnsureCredentialOrPromptAsync(PipeRpcClient client, string identity)
    {
        var statusResponse = await client.SendAsync(RpcMethods.Status, null, ensureRunning: true, null, CancellationToken.None);
        if (statusResponse.Error is not null)
        {
            // If status itself failed, let the run command surface the real error.
            return;
        }

        var creds = statusResponse.Result?["credentials"] as JsonArray;
        var alreadyHeld = creds is not null && creds.OfType<JsonObject>().Any(c =>
            string.Equals(c["identity"]?.GetValue<string>(), identity, StringComparison.OrdinalIgnoreCase));
        if (alreadyHeld)
        {
            return;
        }

        Console.Error.WriteLine($"idmux: capturing credential for {identity} (waiting for password dialog)...");
        var captureResponse = await client.SendAsync(
            RpcMethods.Capture,
            new CaptureRequest { Identity = identity, Caption = $"idmux run --identity {identity}" },
            ensureRunning: true,
            null,
            CancellationToken.None);

        if (captureResponse.Error is not null)
        {
            // Surface the capture error verbatim so the agent sees DialogCancelled
            // / DaemonNotRunning / etc. and the run command can fail-fast.
            OutputWriter.WriteShortError(captureResponse.Error);
            throw new InvalidOperationException(captureResponse.Error.Message);
        }
    }

    private static bool IsHelpToken(string s) =>
        s is "help" or "--help" or "-h" or "-?" or "/?";

    private static bool HasHelpFlag(ParsedArguments parsed) =>
        parsed.Has("help") || parsed.Has("h");

    private static bool IsCommandWithSubcommands(string command) =>
        command is "session" or "blocks" or "daemon";
}

/// <summary>
/// All CLI help text. Each command (and where applicable, each subcommand)
/// has its own topic so that AI agents can run <c>idmux &lt;cmd&gt; --help</c>
/// or <c>idmux help &lt;cmd&gt; &lt;sub&gt;</c> and get a self-contained,
/// accurate description of every option.
///
/// The root help summarises the identity model, exit codes, env vars, and
/// the global <c>--json</c> flag once; per-topic pages document only what
/// is specific to that command.
/// </summary>
internal static class HelpText
{
    public static void WriteRoot() => Write(Root);

    /// <summary>
    /// Print help for <paramref name="command"/> (and optional
    /// <paramref name="subcommand"/>). Returns false if no matching topic
    /// exists so the caller can decide how to surface the unknown name.
    /// </summary>
    public static bool WriteCommand(string command, string? subcommand)
    {
        var key = string.IsNullOrWhiteSpace(subcommand)
            ? command.ToLowerInvariant()
            : $"{command.ToLowerInvariant()} {subcommand!.ToLowerInvariant()}";

        if (!Topics.TryGetValue(key, out var text))
        {
            // Fall back to command-level help if the subcommand was unknown
            // but the command itself has a page.
            if (!string.IsNullOrWhiteSpace(subcommand)
                && Topics.TryGetValue(command.ToLowerInvariant(), out var fallback))
            {
                Write(fallback);
                return true;
            }

            return false;
        }

        Write(text);
        return true;
    }

    private static void Write(string text) => Console.Out.WriteLine(text);

    private static readonly Dictionary<string, string> Topics = new(StringComparer.OrdinalIgnoreCase)
    {
        ["run"] = Run,
        ["session"] = Session,
        ["session create"] = SessionCreate,
        ["session list"] = SessionList,
        ["session destroy"] = SessionDestroy,
        ["blocks"] = Blocks,
        ["blocks list"] = BlocksList,
        ["blocks get"] = BlocksGet,
        ["blocks cancel"] = BlocksCancel,
        ["blocks tail"] = BlocksTail,
        ["capture"] = Capture,
        ["drop"] = Drop,
        ["status"] = Status,
        ["daemon"] = Daemon,
        ["daemon start"] = DaemonStart,
        ["daemon status"] = DaemonStatus,
        ["daemon stop"] = DaemonStop,
        ["daemon pause"] = DaemonPause,
        ["daemon resume"] = DaemonResume,
        ["serve"] = Serve,
        ["ui"] = Ui,
        ["version"] = Version,
        ["help"] = Help
    };

    private const string Root = """
        idmux - one CLI for background and multi-identity PowerShell on Windows.

        USAGE
          idmux <command> [arguments]
          idmux help [<command> [<subcommand>]]
          idmux <command> --help

        COMMANDS
          run         Execute a PowerShell script (foreground or detached).
          session     Manage long-lived persistent sessions (create | list | destroy).
          blocks      Inspect run history and live-tail output (list | get | cancel | tail).
          capture     Pre-cache a credential by popping the WPF password dialog.
          drop        Forget a cached credential.
          status      Print daemon status, held credentials, sessions, recent blocks.
          daemon      Control the background daemon (start | stop | status | pause | resume).
          serve       Ensure the daemon is running and print its HTTP URL.
          ui          Like `serve` but also opens the web UI in the default browser.
          version     Print the idmux version.
          help        Show this help, or `idmux help <command>`.

        GLOBAL FLAGS
          --json      Emit a structured JSON envelope on stdout instead of plain
                      text. On error the JSON-RPC error object is written to
                      stderr. Works on every command.

        IDENTITY MODEL
          (no flags)              Run as the current user. No prompt, nothing
                                  cached.
          --identity <DOMAIN\user>
                                  Run as that identity. If no credential is held,
                                  idmux pops a synchronous WPF password dialog
                                  and waits. Successful captures are kept in
                                  memory for reuse.
          Cross-domain identity   Auto-detected (DOMAIN differs from the daemon's
                                  own DOMAIN). Idmux silently uses runas /netonly
                                  semantics. NO flag is required.
          --netonly               Force runas /netonly even for a same-domain
                                  account. Rare; only useful when the local box
                                  cannot validate the account.
          --session <NAME>        Reuse an existing named session. The session's
                                  identity wins; --identity and --netonly are
                                  ignored on `run`.

        EXIT CODES
          0    Success. For `run` (foreground), the script returned 0.
          1    CLI usage error - missing/invalid flag, file not found,
               unknown command, etc. Message is written to stderr.
          2    RPC error from the daemon (CredentialNotHeld, DialogCancelled,
               ScriptTimeout, BlockNotFound, DaemonPaused, ...). Use --json to
               see the numeric error code and structured `data`.
          N    For `run` (foreground only), the script's own non-zero exit code.

        ENVIRONMENT
          IDMUX_HOME          Override the config / audit / log directory.
                              Default: %LOCALAPPDATA%\idmux
          IDMUX_DAEMON_PATH   Explicit path to idmux-daemon.exe. Otherwise
                              resolved next to idmux.exe or via the matching
                              build output.
          IDMUX_UI_NO_OPEN    Set to "1" to suppress the browser launch from
                              `idmux ui`.

        The daemon auto-starts the first time any command needs it. See
        `idmux <command> --help` for full per-command details.
        """;

    private const string Run = """
        idmux run - execute a PowerShell script.

        USAGE
          idmux run --script '<PS>' [options]
          idmux run --script-file <PATH> [--args-json '<JSON>'] [options]

        REQUIRED (one of)
          --script <STRING>           Inline PowerShell to execute.
          --script-file <PATH>        Path to a .ps1 file. Read into memory and
                                      executed identically to --script.

        IDENTITY (optional, mutually exclusive)
          (none)                      Run as the current user. No prompt.
          --identity <DOMAIN\user>    Run as that identity. If no credential is
                                      held, a WPF password dialog appears and
                                      the run waits for the user to submit it.
                                      A cancelled dialog returns RPC error
                                      -32010 DialogCancelled.
          --session <NAME>            Reuse an existing named session. The
                                      session's identity is used; --identity
                                      and --netonly are ignored.

        OPTIONS
          --args-json '<JSON>'        Bind a JSON value as the script's
                                      arguments. JSON arrays splat into @args
                                      (positional parameters); other values
                                      become $args[0]. Examples:
                                        --args-json '[10, 32]'
                                        --args-json '{"Path":"C:\\data"}'
          --timeout-sec <N>           Kill the script after N seconds. Returns
                                      RPC error -32006 ScriptTimeout.
          --detach                    Fire-and-forget. The CLI prints just the
                                      block id (e.g. b_a1b2c3d4e5) and exits 0
                                      immediately. Tail later with
                                      `idmux blocks tail --id <ID> --follow`.
          --pty                       Allocate a pseudo-console for the child
                                      process. Use for tools that need a real
                                      TTY (interactive installers, kubectl,
                                      some npm scripts).
          --netonly                   Force runas /netonly logon for a
                                      same-domain account. Cross-domain
                                      identities already auto-detect this.
          --json                      Emit a JSON BlockRecord envelope instead
                                      of streaming output. Suppresses live
                                      stdout/stderr streaming.

        OUTPUT
          Foreground (default)        pwsh's stdout/stderr is streamed verbatim
                                      to the CLI's stdout/stderr while the
                                      script runs. Exit code = script exit code.
          --detach                    Prints just the block id (one line) on
                                      stdout, then exits 0 immediately. Output
                                      is captured server-side.
          --json                      Prints a single JSON object: id,
                                      sessionId, sessionName, identityUsed,
                                      script, args, exitCode, state, stdout,
                                      stderr, startedAt, completedAt,
                                      errorMessage, result.

        EXAMPLES
          # Run as me
          idmux run --script 'Get-Process pwsh | Select-Object Id, CPU'

          # Run as a different account (auto-prompts on first use)
          idmux run --identity DOMAIN\svc `
                    --script 'Restart-Service Spooler -Force'

          # Cross-domain (auto-netonly, no flag needed)
          idmux run --identity OTHERDOMAIN\svc `
                    --script 'Get-ChildItem \\fileserver\share'

          # Long-running, detached, then tail later
          $id = idmux run --identity DOMAIN\admin --detach `
                          --script 'msiexec.exe /i big.msi /qn'
          idmux blocks tail --id $id --follow

          # Pass typed JSON args to a parameter script
          idmux run --script-file .\Deploy.ps1 `
                    --args-json '{"Env":"prod","DryRun":false}'

          # Time out after 30 seconds
          idmux run --script 'Start-Sleep 60; "done"' --timeout-sec 30
        """;

    private const string Session = """
        idmux session - manage long-lived persistent named sessions.

        A named session keeps a single pwsh worker process alive between runs.
        Variables, imported modules, and live connections (DB, AD, REST, ...)
        survive across `idmux run --session <NAME>` calls.

        Default sessions (created implicitly by `idmux run` without --session)
        do NOT persist - each run gets a fresh worker. Only NAMED sessions
        persist.

        SUBCOMMANDS
          create     Create a new persistent named session.
          list       List all known sessions (alive + crashed + implicit).
          destroy    Stop a session's pwsh process and forget it.

        USAGE
          idmux session create --name <NAME> [--identity <USER>] [--netonly] [--json]
          idmux session list [--json]
          idmux session destroy --name <NAME> [--json]

        See `idmux session <subcommand> --help` for full per-subcommand details.
        """;

    private const string SessionCreate = """
        idmux session create - create a persistent named session.

        USAGE
          idmux session create --name <NAME> [--identity <USER>] [--netonly] [--json]

        REQUIRED
          --name <NAME>             Unique session name. Used everywhere with
                                    --session <NAME>. Reusing an existing name
                                    returns RPC error -32003 SessionNameTaken.

        OPTIONS
          --identity <DOMAIN\user>  Identity for the session's pwsh worker.
                                    Defaults to the current user. Foreign-domain
                                    identities auto-use runas /netonly. If no
                                    credential is held, a WPF password dialog
                                    appears synchronously.
          --netonly                 Force runas /netonly even for a same-domain
                                    account. Rare.
          --json                    Emit the SessionRecord (id, name, identity,
                                    netOnly, persistent, state, runspacePid,
                                    createdAt, lastUsedAt, blockCount) as JSON.

        EXAMPLES
          # Default: persistent session as current user
          idmux session create --name dev

          # Same-domain service-account session (auto-prompts for password on first use)
          idmux session create --name svc --identity DOMAIN\svc

          # Cross-domain service-account session - netonly auto-detected, no flag needed
          idmux session create --name xsvc --identity OTHERDOMAIN\svc

          # Use the session repeatedly
          idmux run --session svc --script '$counter = 0; Import-Module ActiveDirectory'
          idmux run --session svc --script '$counter++; "run #$counter"'
          idmux run --session svc --script 'Get-ADUser -Identity $env:USERNAME | Select-Object Name, Enabled'
        """;

    private const string SessionList = """
        idmux session list - list all known sessions.

        USAGE
          idmux session list [--json]

        OPTIONS
          --json     Emit a JSON array of SessionRecord. Without --json, prints
                     one tab-separated row per session:
                       <name>\t<state>\t<identity>

        STATES
          starting   Worker process is being created.
          alive      Worker is running and accepting commands.
          dead       Worker exited cleanly.
          crashed    Worker exited unexpectedly.

        NOTES
          Implicit (default) sessions created by `idmux run` without --session
          appear here as well. Their names look like
          `default-<userToken>` or `default-<userToken>-netonly`.
        """;

    private const string SessionDestroy = """
        idmux session destroy - stop a session's pwsh process and forget it.

        USAGE
          idmux session destroy --name <NAME> [--json]

        REQUIRED
          --name <NAME>     Session to destroy. Returns RPC error -32002
                            SessionNotFound if no such session exists.

        OPTIONS
          --json            Emit JSON {"ok":true,"name":"<NAME>"}.

        NOTES
          Any block currently running in this session is cancelled and
          transitions to state `cancelled`. The session cannot be used after
          destruction; recreate it with `idmux session create`.
        """;

    private const string Blocks = """
        idmux blocks - inspect run history and live-tail output.

        A "block" is a single execution unit produced by `idmux run`. Blocks
        are kept in memory in a per-session bounded ring buffer; you can list
        them, fetch their captured stdout/stderr, cancel running ones, and
        tail (live-stream) them.

        SUBCOMMANDS
          list      List recent blocks across all sessions or one session.
          get       Fetch a block's full output and metadata.
          cancel    Cancel a currently running block.
          tail      Stream a block's stdout/stderr (with optional follow).

        USAGE
          idmux blocks list   [--session <NAME>] [--since <ISO8601>] [--limit <N>] [--json]
          idmux blocks get    --id <ID> [--field stdout|stderr|result|all] [--json]
          idmux blocks cancel --id <ID> [--json]
          idmux blocks tail   --id <ID> [--follow] [--stdout-offset <N>] [--stderr-offset <N>] [--json]

        See `idmux blocks <subcommand> --help` for full per-subcommand details.
        """;

    private const string BlocksList = """
        idmux blocks list - list recent blocks.

        USAGE
          idmux blocks list [--session <NAME>] [--since <ISO8601>] [--limit <N>] [--json]

        OPTIONS
          --session <NAME>      Restrict to a single session (named or implicit).
                                Default: all sessions.
          --since <ISO8601>     Only return blocks started at/after this
                                timestamp. Examples:
                                  --since 2026-06-16T14:00:00Z
                                  --since 2026-06-16T09:00:00-05:00
          --limit <N>           Cap the number of returned blocks
                                (most-recent first). Default: daemon's per
                                session ring-buffer size.
          --json                Emit a JSON array of BlockSummary objects.
                                Without --json, prints one tab-separated row:
                                  <id>\t<state>\t<sessionName>\t<scriptPreview>

        NOTES
          Summaries do NOT include full stdout/stderr. Use
          `idmux blocks get --id <ID>` to fetch the captured output.
        """;

    private const string BlocksGet = """
        idmux blocks get - fetch a block's output and metadata.

        USAGE
          idmux blocks get --id <ID> [--field stdout|stderr|result|all] [--json]

        REQUIRED
          --id <ID>             Block id (e.g. b_a1b2c3d4e5). Returns RPC
                                error -32032 BlockNotFound if no such block.

        OPTIONS
          --field <NAME>        Project a single field. Choices:
                                  stdout    just the captured stdout
                                  stderr    just the captured stderr
                                  result    just the script's return value
                                  all       full BlockRecord (default)
          --json                Emit the field/record as JSON.

        PLAIN OUTPUT
          --field stdout (or no --field)  -> stdout written verbatim to stdout
          --field stderr                  -> stderr written verbatim to stderr
          --field all (default)           -> stdout + stderr written verbatim

        EXAMPLES
          # Just the stdout from a previous run
          idmux blocks get --id b_a1b2c3d4e5 --field stdout

          # Full record as JSON for programmatic parsing
          idmux blocks get --id b_a1b2c3d4e5 --json
        """;

    private const string BlocksCancel = """
        idmux blocks cancel - cancel a currently running block.

        USAGE
          idmux blocks cancel --id <ID> [--json]

        REQUIRED
          --id <ID>             Block id to cancel. Returns RPC error -32032
                                BlockNotFound if the block is not running
                                (already completed or never existed).

        OPTIONS
          --json                Emit JSON {"ok":true,"id":"<ID>"}.

        NOTES
          Cancellation tears down the active execution. The block transitions
          to state `cancelled` with exitCode 1. Any caller currently streaming
          the block (`blocks tail` or foreground `run`) sees RPC error
          -32007 CancelledByUser.
        """;

    private const string BlocksTail = """
        idmux blocks tail - stream a block's stdout/stderr.

        USAGE
          idmux blocks tail --id <ID> [--follow] [--stdout-offset <N>] [--stderr-offset <N>] [--json]

        REQUIRED
          --id <ID>             Block id to tail. Returns RPC error -32032
                                BlockNotFound if no such block exists.

        OPTIONS
          --follow              Keep the connection open and stream new output
                                live as it arrives. Returns when the block
                                completes. Without --follow, prints the
                                currently-buffered output and exits.
          --stdout-offset <N>   Start streaming stdout at byte offset N.
                                Default 0 (from the beginning).
          --stderr-offset <N>   Start streaming stderr at byte offset N.
                                Default 0.
          --json                Emit a JSON envelope per chunk; otherwise
                                streams verbatim bytes to stdout/stderr.

        EXIT CODE
          Mirrors the script's exit code if the block has completed; otherwise
          0 while still running.

        EXAMPLES
          # Tail a freshly detached block
          $id = idmux run --identity DOMAIN\admin --detach `
                          --script 'Start-Sleep 30; "done"'
          idmux blocks tail --id $id --follow

          # Resume tailing where you left off after a disconnect
          idmux blocks tail --id $id --follow --stdout-offset 1024
        """;

    private const string Capture = """
        idmux capture - pre-cache a credential for an identity.

        Pops the WPF password dialog and waits for the user to submit. The
        captured credential is held in memory by the daemon for reuse.

        USAGE
          idmux capture --identity <DOMAIN\user> [--caption <STRING>] [--json]

        REQUIRED
          --identity <USER>      Identity to capture (DOMAIN\user).

        OPTIONS
          --caption <STRING>     Custom title for the password dialog.
                                 Default: "Capture credential for <identity>".
          --json                 Emit JSON {"identity":"<DOMAIN\\user>"} on
                                 success.

        ERRORS
          -32010 DialogCancelled   The user clicked Cancel.

        NOTES
          You usually do NOT need this. `idmux run --identity ...` and
          `idmux session create --identity ...` auto-capture on first use.
          Pre-capture is useful for verifying a password ahead of a script
          or batch-caching several identities at once.
        """;

    private const string Drop = """
        idmux drop - forget a cached credential.

        USAGE
          idmux drop --identity <DOMAIN\user> [--json]

        REQUIRED
          --identity <USER>      Identity whose credential to forget.
                                 Idempotent - dropping an unknown identity
                                 still returns success.

        OPTIONS
          --json                 Emit JSON {"identity":"<DOMAIN\\user>",
                                 "dropped":true|false}.

        NOTES
          Dropping does NOT cancel running sessions or blocks; it only
          prevents future runs from finding a cached credential. The next
          `run` or `session create` for that identity will re-trigger the
          password dialog.
        """;

    private const string Status = """
        idmux status - print daemon status, credentials, sessions, recent blocks.

        USAGE
          idmux status [--json]

        OPTIONS
          --json     Emit a StatusSnapshot JSON object containing:
                       daemon       { version, startedAt, idleShutdownAt,
                                      paused, pipeName, webPort }
                       credentials  array of HeldCredentialInfo
                                    { identity, capturedAt, expiresAt,
                                      emulated }
                       sessions     array of SessionRecord
                       recentBlocks array of BlockSummary

        PLAIN OUTPUT
          daemon port <N>[ (paused)]
          credentials   <count>
          sessions      <count>
          recent-blocks <count>
        """;

    private const string Daemon = """
        idmux daemon - control the background daemon process.

        The daemon is a per-Windows-logon-session process that hosts pwsh
        runspaces, holds credentials in memory, and serves the named-pipe
        RPC and HTTP UI. It auto-starts the first time any command needs it,
        and idle-shuts-down after a configurable inactivity window.

        SUBCOMMANDS
          start      Start the daemon if it is not running. Idempotent.
          status     Print whether the daemon is running (and on what port).
          stop       Tell the daemon to shut down. Idempotent.
          pause      Pause new runs (existing runs continue) until `resume`.
          resume     Resume a paused daemon.

        USAGE
          idmux daemon <subcommand> [--json]

        PIPE NAME
          idmux-<sessionId>          (one daemon per Windows logon session)

        See `idmux daemon <subcommand> --help` for full per-subcommand details.
        """;

    private const string DaemonStart = """
        idmux daemon start - start the background daemon (idempotent).

        USAGE
          idmux daemon start [--json]

        OPTIONS
          --json     Emit a status object including:
                       started         true if THIS call launched the daemon
                       alreadyRunning  true if the daemon was already up
                       daemon          the standard daemon block
                       sessions        current session list
                       credentials     current credential list
                       recentBlocks    current block list

        PLAIN OUTPUT
          daemon running on pipe idmux-<sessionId> (port <N>)
        """;

    private const string DaemonStatus = """
        idmux daemon status - show whether the daemon is running.

        USAGE
          idmux daemon status [--json]

        OPTIONS
          --json     If running, emits the standard StatusSnapshot daemon
                     block. If stopped, emits
                     {"running":false,"pipeName":"<NAME>"}.

        PLAIN OUTPUT
          running\tport <N>      (when up)
          daemon stopped         (when down)
        """;

    private const string DaemonStop = """
        idmux daemon stop - gracefully shut down the daemon.

        USAGE
          idmux daemon stop [--json]

        OPTIONS
          --json     Emit {"stopping":true} on success or
                     {"stopping":false,"alreadyStopped":true} when the daemon
                     was not running.

        PLAIN OUTPUT
          stopping                 (was running)
          daemon already stopped   (was not running)

        NOTES
          Running blocks are cancelled (CancelledByUser) before the daemon
          process exits.
        """;

    private const string DaemonPause = """
        idmux daemon pause - reject new runs until `resume`.

        USAGE
          idmux daemon pause [--json]

        PLAIN OUTPUT
          paused

        NOTES
          While paused, `idmux run` and `idmux session create` return RPC
          error -32031 DaemonPaused. Existing runs keep going. `daemon
          status` and read-only commands still work. Use
          `idmux daemon resume` to re-enable runs.
        """;

    private const string DaemonResume = """
        idmux daemon resume - resume a paused daemon.

        USAGE
          idmux daemon resume [--json]

        PLAIN OUTPUT
          resumed
        """;

    private const string Serve = """
        idmux serve - start the daemon and print its HTTP URL.

        USAGE
          idmux serve [--port <N>] [--json]

        OPTIONS
          --port <N>     Port for the HTTP / web-UI listener (1-65535).
                         Must be an integer. If different from the active
                         port, the daemon is restarted with the new port.
                         The new port is saved to the config file and
                         becomes the default for next time.
          --json         Emit a JSON envelope:
                           { url, opened, started, alreadyRunning,
                             configFile, daemon }

        PLAIN OUTPUT
          http://127.0.0.1:<port>

        NOTES
          Does NOT open a browser. Use `idmux ui` for that.
        """;

    private const string Ui = """
        idmux ui - start the daemon and open the web UI in the default browser.

        USAGE
          idmux ui [--port <N>] [--json]

        OPTIONS
          --port <N>     Port for the HTTP / web-UI listener (1-65535). Same
                         semantics as `idmux serve --port` - persisted to
                         config and reused next time.
          --json         Emit a JSON envelope (browser is NOT opened in this
                         mode).

        ENVIRONMENT
          IDMUX_UI_NO_OPEN=1   Suppress the browser launch. Useful in
                               scripted environments where you only want
                               to ensure the URL is reachable.

        PLAIN OUTPUT
          http://127.0.0.1:<port>          (browser opens to this URL)

        EXAMPLES
          idmux ui                          # default port from config
          idmux ui --port 5050              # change port permanently
        """;

    private const string Version = """
        idmux version - print the idmux version string.

        USAGE
          idmux version
          idmux --version

        OUTPUT
          Plain semver string on stdout (e.g. 1.0.0.0). Exit code 0.
        """;

    private const string Help = """
        idmux help - show help for idmux or a specific command.

        USAGE
          idmux help                              # root help (same as `idmux`)
          idmux help <command>                    # command-specific help
          idmux help <command> <subcommand>       # subcommand help

        EQUIVALENT
          idmux <command> --help
          idmux <command> -h
          idmux <command> <subcommand> --help

        TOPICS
          run | session [create|list|destroy] | blocks [list|get|cancel|tail]
          | capture | drop | status | daemon [start|status|stop|pause|resume]
          | serve | ui | version | help
        """;
}
