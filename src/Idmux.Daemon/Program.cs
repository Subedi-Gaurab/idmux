using System.IO.Pipes;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Idmux.Core;
using Idmux.Daemon;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

IdmuxPaths.EnsureHomeExists();

using var mutex = new Mutex(true, IdmuxPaths.CurrentMutexName, out var createdNew);
if (!createdNew)
{
    Console.Error.WriteLine("idmux-daemon is already running.");
    return 0;
}

var config = IdmuxConfigLoader.Load();
var log = new DaemonLog(IdmuxPaths.LogFile);
var runtime = new DaemonRuntime(config, log);
var rpcServer = new PipeRpcServer(runtime, log);

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls($"http://127.0.0.1:{config.Daemon.WebPort}");

var app = builder.Build();
var webRoot = Path.Combine(AppContext.BaseDirectory, "Web", "wwwroot");
app.UseWebSockets();
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(webRoot),
    RequestPath = "/static"
});

app.MapGet("/", () => Results.File(Path.Combine(webRoot, "index.html"), "text/html"));
app.MapGet("/api/status", () => Results.Json(runtime.GetStatusSnapshot(), IdmuxJson.Options));
app.MapGet("/api/sessions", () => Results.Json(runtime.ListSessions(), IdmuxJson.Options));
app.MapGet("/api/sessions/{name}", (string name) => Results.Json(runtime.GetSessionWithBlocks(name), IdmuxJson.Options));
app.MapGet("/api/sessions/{name}/blocks", (string name, string? since, int? limit) =>
{
    var parsedSince = DateTimeOffset.TryParse(since, out var value) ? value : (DateTimeOffset?)null;
    return Results.Json(runtime.ListBlocksForSession(name, parsedSince, limit), IdmuxJson.Options);
});
app.MapGet("/api/blocks/{id}", (string id) => Results.Json(runtime.GetBlockRecord(id), IdmuxJson.Options));
app.MapGet("/api/blocks/{id}/stdout", (string id) => Results.Text(runtime.GetBlockRecord(id).Stdout, "text/plain"));
app.MapGet("/api/blocks/{id}/stderr", (string id) => Results.Text(runtime.GetBlockRecord(id).Stderr, "text/plain"));
app.MapPost("/api/credentials/{identity}/drop", async (string identity) => Results.Json(await runtime.DropCredentialFromWebAsync(identity), IdmuxJson.Options));
app.MapPost("/api/sessions/{name}/destroy", async (string name) => Results.Json(await runtime.DestroySessionFromWebAsync(name), IdmuxJson.Options));
app.MapPost("/api/blocks/{id}/cancel", async (string id, HttpContext context) => Results.Json(await runtime.CancelBlockFromWebAsync(id, context.RequestAborted), IdmuxJson.Options));
app.MapPost("/api/daemon/pause", async () => Results.Json(await runtime.DispatchAsync(new JsonRpcRequest { Method = RpcMethods.DaemonPause }, null, CancellationToken.None), IdmuxJson.Options));
app.MapPost("/api/daemon/resume", async () => Results.Json(await runtime.DispatchAsync(new JsonRpcRequest { Method = RpcMethods.DaemonResume }, null, CancellationToken.None), IdmuxJson.Options));
app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var subscription = runtime.Subscribe();

    try
    {
        await foreach (var message in subscription.Reader.ReadAllAsync(context.RequestAborted))
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, context.RequestAborted);
        }
    }
    catch
    {
        // Ignore disconnects.
    }
    finally
    {
        runtime.Unsubscribe(subscription.Id);
    }
});

runtime.RequestStop = () => app.Lifetime.StopApplication();
app.Lifetime.ApplicationStopping.Register(() =>
{
    log.Info("Daemon stopping.");
});

var shutdown = new CancellationTokenSource();
app.Lifetime.ApplicationStopping.Register(() => shutdown.Cancel());

var rpcTask = rpcServer.RunAsync(shutdown.Token);
var maintenanceTask = runtime.MaintenanceLoopAsync(shutdown.Token);

await app.StartAsync();
log.Info($"Daemon started on pipe {IdmuxPaths.CurrentPipeName} and web port {config.Daemon.WebPort}.");

try
{
    await app.WaitForShutdownAsync();
}
finally
{
    shutdown.Cancel();

    await Task.WhenAll(
        IgnoreCancellationAsync(rpcTask),
        IgnoreCancellationAsync(maintenanceTask));

    await runtime.DisposeAsync();
}

return 0;

static async Task IgnoreCancellationAsync(Task task)
{
    try
    {
        await task;
    }
    catch (OperationCanceledException)
    {
    }
}

internal sealed class PipeRpcServer(DaemonRuntime runtime, DaemonLog log)
{
    private readonly DaemonRuntime _runtime = runtime;
    private readonly DaemonLog _log = log;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    IdmuxPaths.CurrentPipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(cancellationToken);
                _ = Task.Run(() => HandleClientAsync(pipe, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception exception)
            {
                pipe?.Dispose();
                _log.Error(exception, "Named pipe accept loop failed.");
                await Task.Delay(250, cancellationToken);
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using var _ = pipe;
        var pipeWriteLock = new SemaphoreSlim(1, 1);
        JsonRpcResponse response;

        async Task SendChunkAsync(string stream, string data, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(data))
            {
                return;
            }

            var notification = new JsonRpcRequest
            {
                Method = RpcMethods.Chunk,
                Params = IdmuxJson.ToNode(new ChunkParams { Stream = stream, Data = data })
            };

            await pipeWriteLock.WaitAsync(ct);
            try
            {
                await PipeFraming.WriteMessageAsync(pipe, JsonSerializer.Serialize(notification, IdmuxJson.Options), ct);
            }
            finally
            {
                pipeWriteLock.Release();
            }
        }

        try
        {
            var payload = await PipeFraming.ReadMessageAsync(pipe, cancellationToken);
            if (string.IsNullOrWhiteSpace(payload))
            {
                return;
            }

            JsonRpcRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<JsonRpcRequest>(payload, IdmuxJson.Options);
            }
            catch (Exception exception)
            {
                response = new JsonRpcResponse
                {
                    Error = new JsonRpcError
                    {
                        Code = RpcErrorCodes.ParseError,
                        Message = exception.Message
                    }
                };

                await WriteFinalAsync(pipe, pipeWriteLock, response, cancellationToken);
                return;
            }

            if (request is null || string.IsNullOrWhiteSpace(request.Method))
            {
                response = new JsonRpcResponse
                {
                    Id = request?.Id,
                    Error = new JsonRpcError
                    {
                        Code = RpcErrorCodes.InvalidRequest,
                        Message = "Invalid JSON-RPC request."
                    }
                };
            }
            else
            {
                try
                {
                    var result = await _runtime.DispatchAsync(request, SendChunkAsync, cancellationToken);
                    response = new JsonRpcResponse
                    {
                        Id = request.Id,
                        Result = result
                    };
                }
                catch (RpcException exception)
                {
                    response = new JsonRpcResponse
                    {
                        Id = request.Id,
                        Error = new JsonRpcError
                        {
                            Code = exception.Code,
                            Message = exception.Message,
                            Data = exception.ErrorData
                        }
                    };
                }
                catch (Exception exception)
                {
                    _log.Error(exception, $"Unhandled RPC exception for method '{request.Method}'.");
                    response = new JsonRpcResponse
                    {
                        Id = request.Id,
                        Error = new JsonRpcError
                        {
                            Code = RpcErrorCodes.InternalDaemonError,
                            Message = exception.GetType().Name
                        }
                    };
                }
            }
        }
        catch (Exception exception)
        {
            _log.Error(exception, "Pipe client handler failed.");
            response = new JsonRpcResponse
            {
                Error = new JsonRpcError
                {
                    Code = RpcErrorCodes.InternalError,
                    Message = exception.Message
                }
            };
        }

        await WriteFinalAsync(pipe, pipeWriteLock, response, cancellationToken);
    }

    private static async Task WriteFinalAsync(NamedPipeServerStream pipe, SemaphoreSlim pipeWriteLock, JsonRpcResponse response, CancellationToken cancellationToken)
    {
        await pipeWriteLock.WaitAsync(cancellationToken);
        try
        {
            await PipeFraming.WriteMessageAsync(pipe, JsonSerializer.Serialize(response, IdmuxJson.Options), cancellationToken);
        }
        catch (IOException)
        {
            // Client disconnected before reading the final response \u2014 not a daemon-side problem.
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            pipeWriteLock.Release();
        }
    }
}
