using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Idmux.Core;

namespace Idmux.Cli;

internal sealed class ParsedArguments
{
    public List<string> Positionals { get; } = [];
    public Dictionary<string, string?> Options { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Has(string name) => Options.ContainsKey(name);
    public string? Get(string name) => Options.TryGetValue(name, out var value) ? value : null;

    public string Require(string name)
    {
        var value = Get(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Missing required option --{name}.");
        }

        return value;
    }

    public static ParsedArguments Parse(IEnumerable<string> args)
    {
        var parsed = new ParsedArguments();
        string? pendingKey = null;

        foreach (var current in args)
        {
            if (current.StartsWith("--", StringComparison.Ordinal))
            {
                if (pendingKey is not null)
                {
                    // Previous flag had no value before this next flag arrived \u2014 treat it as boolean.
                    parsed.Options[pendingKey] = "true";
                }

                pendingKey = current[2..];
            }
            else if (pendingKey is not null)
            {
                parsed.Options[pendingKey] = current;
                pendingKey = null;
            }
            else
            {
                parsed.Positionals.Add(current);
            }
        }

        if (pendingKey is not null)
        {
            parsed.Options[pendingKey] = "true";
        }

        return parsed;
    }
}

internal sealed class DaemonLauncher
{
    public async Task<bool> IsRunningAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", IdmuxPaths.CurrentPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(250));
            await pipe.ConnectAsync(timeout.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> EnsureRunningAsync(CancellationToken cancellationToken)
    {
        if (await IsRunningAsync(cancellationToken))
        {
            return false;
        }

        var daemonPath = ResolveDaemonPath();
        var startInfo = new ProcessStartInfo(daemonPath)
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory
        };

        Process.Start(startInfo)?.Dispose();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await IsRunningAsync(cancellationToken))
            {
                return true;
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new InvalidOperationException($"Timed out waiting for the daemon to start from '{daemonPath}'.");
    }

    public string ResolveDaemonPath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("IDMUX_DAEMON_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        var sameDirectory = Path.Combine(AppContext.BaseDirectory, "idmux-daemon.exe");
        if (File.Exists(sameDirectory))
        {
            return sameDirectory;
        }

        var baseDirectory = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var frameworkName = baseDirectory.Name;
        var configurationName = baseDirectory.Parent?.Name;
        var srcDirectory = baseDirectory.Parent?.Parent?.Parent?.Parent;
        if (configurationName is not null && srcDirectory is not null)
        {
            var siblingPath = Path.Combine(srcDirectory.FullName, "Idmux.Daemon", "bin", configurationName, frameworkName, "idmux-daemon.exe");
            if (File.Exists(siblingPath))
            {
                return siblingPath;
            }
        }

        throw new FileNotFoundException("Unable to locate idmux-daemon.exe.");
    }
}

internal sealed class PipeRpcClient(DaemonLauncher launcher)
{
    private readonly DaemonLauncher _launcher = launcher;

    public Task<bool> IsRunningAsync(CancellationToken cancellationToken) => _launcher.IsRunningAsync(cancellationToken);

    public Task<JsonRpcResponse> SendAsync(string method, object? parameters, bool ensureRunning, CancellationToken cancellationToken)
    {
        return SendAsync(method, parameters, ensureRunning, onChunk: null, cancellationToken);
    }

    public async Task<JsonRpcResponse> SendAsync(
        string method,
        object? parameters,
        bool ensureRunning,
        Action<string, string>? onChunk,
        CancellationToken cancellationToken)
    {
        if (ensureRunning)
        {
            await _launcher.EnsureRunningAsync(cancellationToken);
        }
        else if (!await _launcher.IsRunningAsync(cancellationToken))
        {
            throw new InvalidOperationException("The daemon is not running.");
        }

        await using var pipe = new NamedPipeClientStream(".", IdmuxPaths.CurrentPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        await pipe.ConnectAsync(connectTimeout.Token);

        var requestId = Guid.NewGuid().ToString("N");
        var request = new JsonRpcRequest
        {
            Id = requestId,
            Method = method,
            Params = parameters is null ? null : IdmuxJson.ToNode(parameters)
        };

        await PipeFraming.WriteMessageAsync(pipe, JsonSerializer.Serialize(request, IdmuxJson.Options), cancellationToken);

        while (true)
        {
            var payload = await PipeFraming.ReadMessageAsync(pipe, cancellationToken);
            if (string.IsNullOrEmpty(payload))
            {
                throw new InvalidOperationException("The daemon returned an empty response.");
            }

            var node = JsonNode.Parse(payload) as JsonObject
                ?? throw new InvalidOperationException("The daemon returned an invalid response.");

            // Notification (no id): mid-request chunk or other server-initiated event.
            if (!node.ContainsKey("id") || node["id"] is null)
            {
                var methodName = node["method"]?.GetValue<string>();
                if (methodName == RpcMethods.Chunk && onChunk is not null)
                {
                    var paramsNode = node["params"]?.AsObject();
                    var stream = paramsNode?["stream"]?.GetValue<string>() ?? "stdout";
                    var data = paramsNode?["data"]?.GetValue<string>() ?? string.Empty;
                    onChunk(stream, data);
                }

                continue;
            }

            return JsonSerializer.Deserialize<JsonRpcResponse>(payload, IdmuxJson.Options)
                ?? throw new InvalidOperationException("The daemon returned an invalid response.");
        }
    }
}

internal static class OutputWriter
{
    public static void WriteJson(JsonNode? node)
    {
        var options = new JsonSerializerOptions(IdmuxJson.Options) { WriteIndented = true };
        Console.Out.WriteLine(node?.ToJsonString(options) ?? "null");
    }

    public static void WriteJsonError(JsonRpcError error)
    {
        var options = new JsonSerializerOptions(IdmuxJson.Options) { WriteIndented = true };
        var node = IdmuxJson.ToNode(new { error })!;
        Console.Error.WriteLine(node.ToJsonString(options));
    }

    public static void WriteRaw(string? text, bool standardError)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var writer = standardError ? Console.Error : Console.Out;
        writer.Write(text);
    }

    public static void WriteShortError(JsonRpcError error)
    {
        var line = new StringBuilder();
        line.Append("idmux: ");
        line.Append(error.Message);
        if (error.Data is JsonObject data)
        {
            var hint = data["hint"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(hint))
            {
                line.Append(" (");
                line.Append(hint);
                line.Append(')');
            }
        }

        Console.Error.WriteLine(line.ToString());
    }
}
