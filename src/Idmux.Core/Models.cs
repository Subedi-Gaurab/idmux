using System.Text.Json.Nodes;

namespace Idmux.Core;

public enum RunspaceState
{
    None,
    Starting,
    Alive,
    Dead,
    Crashed
}

public enum BlockState
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}

public sealed class SessionRecord
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Identity { get; set; } = string.Empty;
    public bool NetOnly { get; set; }
    public bool Persistent { get; set; }
    public bool Implicit { get; set; }
    public RunspaceState State { get; set; }
    public int? RunspacePid { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastUsedAt { get; set; }
    public int BlockCount { get; set; }
}

public sealed class BlockRecord
{
    public string Id { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string SessionName { get; set; } = string.Empty;
    public string Script { get; set; } = string.Empty;
    public JsonNode? Args { get; set; }
    public string IdentityUsed { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public BlockState State { get; set; }
    public int? ExitCode { get; set; }
    public string Stdout { get; set; } = string.Empty;
    public string Stderr { get; set; } = string.Empty;
    public JsonNode? Result { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class BlockSummary
{
    public string Id { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string SessionName { get; set; } = string.Empty;
    public string IdentityUsed { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public BlockState State { get; set; }
    public int? ExitCode { get; set; }
    public string ScriptPreview { get; set; } = string.Empty;
    public int StdoutLength { get; set; }
    public int StderrLength { get; set; }
}

public sealed class HeldCredentialInfo
{
    public string Identity { get; set; } = string.Empty;
    public DateTimeOffset CapturedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool Emulated { get; set; }
}

public sealed class DaemonStatusInfo
{
    public string Version { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? IdleShutdownAt { get; set; }
    public bool Paused { get; set; }
    public string PipeName { get; set; } = string.Empty;
    public int WebPort { get; set; }
}

public sealed class StatusSnapshot
{
    public DaemonStatusInfo Daemon { get; set; } = new();
    public List<HeldCredentialInfo> Credentials { get; set; } = [];
    public List<SessionRecord> Sessions { get; set; } = [];
    public List<BlockSummary> RecentBlocks { get; set; } = [];
}

public sealed class SessionWithBlocks
{
    public SessionRecord Session { get; set; } = new();
    public List<BlockSummary> Blocks { get; set; } = [];
}
