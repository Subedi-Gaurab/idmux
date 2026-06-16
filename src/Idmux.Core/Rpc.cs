using System.Text.Json.Nodes;

namespace Idmux.Core;

public sealed class JsonRpcRequest
{
    public string Jsonrpc { get; set; } = "2.0";
    public string? Id { get; set; }
    public string Method { get; set; } = string.Empty;
    public JsonNode? Params { get; set; }
}

public sealed class JsonRpcResponse
{
    public string Jsonrpc { get; set; } = "2.0";
    public string? Id { get; set; }
    public JsonNode? Result { get; set; }
    public JsonRpcError? Error { get; set; }
}

public sealed class JsonRpcError
{
    public int Code { get; set; }
    public string Message { get; set; } = string.Empty;
    public JsonNode? Data { get; set; }
}

public sealed class RpcException(int code, string message, JsonNode? errorData = null) : Exception(message)
{
    public int Code { get; } = code;
    public JsonNode? ErrorData { get; } = errorData;
}

public static class RpcMethods
{
    public const string Ping = "ping";
    public const string Status = "status";
    public const string Capture = "capture";
    public const string Drop = "drop";
    public const string SessionCreate = "session.create";
    public const string SessionList = "session.list";
    public const string SessionDestroy = "session.destroy";
    public const string Run = "run";
    public const string BlockList = "block.list";
    public const string BlockGet = "block.get";
    public const string BlockCancel = "block.cancel";
    public const string BlockTail = "block.tail";
    public const string DaemonStatus = "daemon.status";
    public const string DaemonPause = "daemon.pause";
    public const string DaemonResume = "daemon.resume";
    public const string DaemonStop = "daemon.stop";

    // Server -> client notifications (mid-request, no id):
    public const string Chunk = "chunk";
}

public static class RpcErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    public const int CredentialNotHeld = -32001;
    public const int SessionNotFound = -32002;
    public const int SessionNameTaken = -32003;
    public const int RunspaceDead = -32004;
    public const int ScriptTimeout = -32006;
    public const int CancelledByUser = -32007;
    public const int DialogCancelled = -32010;
    public const int PwshNotFound = -32020;
    public const int InternalDaemonError = -32030;
    public const int DaemonPaused = -32031;
    public const int BlockNotFound = -32032;
    public const int DaemonNotRunning = -32033;
}

public sealed class CaptureRequest
{
    public string Identity { get; set; } = string.Empty;
    public string? Caption { get; set; }
}

public sealed class DropRequest
{
    public string Identity { get; set; } = string.Empty;
}

public sealed class SessionCreateRequest
{
    public string? Name { get; set; }
    public string Identity { get; set; } = string.Empty;
    public bool Persistent { get; set; }
    public bool NetOnly { get; set; }
}

public sealed class SessionDestroyRequest
{
    public string Name { get; set; } = string.Empty;
}

public sealed class RunRequest
{
    public string? Identity { get; set; }
    public string? Session { get; set; }
    public string Script { get; set; } = string.Empty;
    public JsonNode? Args { get; set; }
    public int? TimeoutSec { get; set; }
    public bool Stream { get; set; }
    public bool Detach { get; set; }
    public bool Pty { get; set; }
    public bool NetOnly { get; set; }
}

public sealed class BlockListRequest
{
    public string? Session { get; set; }
    public DateTimeOffset? Since { get; set; }
    public int? Limit { get; set; }
}

public sealed class BlockGetRequest
{
    public string Id { get; set; } = string.Empty;
    public string? Fields { get; set; }
}

public sealed class BlockCancelRequest
{
    public string Id { get; set; } = string.Empty;
}

public sealed class BlockTailRequest
{
    public string Id { get; set; } = string.Empty;
    public bool Follow { get; set; }
    public int StdoutOffset { get; set; }
    public int StderrOffset { get; set; }
}

public sealed class ChunkParams
{
    public string Stream { get; set; } = "stdout";
    public string Data { get; set; } = string.Empty;
}
