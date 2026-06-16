using System.Text.Json;

namespace Idmux.Core;

public sealed class IdmuxConfig
{
    // Display-only metadata for known identities (friendly token, color in web UI).
    // Identity *routing* (which user runs a given script) is intentionally not part
    // of idmux core: callers pass --identity explicitly, or omit it to run as the
    // current user. Identity-selection conventions live in the agent skill, not here.
    public Dictionary<string, IdentityDisplay> Identities { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DaemonConfig Daemon { get; set; } = new();
    public DevelopmentConfig Development { get; set; } = new();

    public static IdmuxConfig CreateDefault()
    {
        return new IdmuxConfig
        {
            Identities = new Dictionary<string, IdentityDisplay>(StringComparer.OrdinalIgnoreCase),
            Daemon = new DaemonConfig
            {
                IdleTimeoutMinutes = 240,
                CredentialIdleMinutes = 480,
                AuditLogEnabled = true,
                AuditLogBodies = false,
                WebPort = 7878,
                BlockBufferSize = 1000
            },
            Development = new DevelopmentConfig
            {
                ExecutionMode = "credential",
                CaptureWithoutPrompt = false
            }
        };
    }
}

public sealed class IdentityDisplay
{
    public string Display { get; set; } = string.Empty;
    public string? Color { get; set; }
}

public sealed class DaemonConfig
{
    public int IdleTimeoutMinutes { get; set; } = 240;
    public int CredentialIdleMinutes { get; set; } = 480;
    public bool AuditLogEnabled { get; set; } = true;
    public bool AuditLogBodies { get; set; }
    public int WebPort { get; set; } = 7878;
    public int BlockBufferSize { get; set; } = 1000;
}

public sealed class DevelopmentConfig
{
    public string ExecutionMode { get; set; } = "credential";
    public bool CaptureWithoutPrompt { get; set; }
}

public static class IdmuxConfigLoader
{
    public static IdmuxConfig Load()
    {
        IdmuxPaths.EnsureHomeExists();

        var config = IdmuxConfig.CreateDefault();
        if (File.Exists(IdmuxPaths.ConfigFile))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<IdmuxConfig>(File.ReadAllText(IdmuxPaths.ConfigFile), IdmuxJson.Options);
                if (loaded is not null)
                {
                    foreach (var pair in loaded.Identities)
                    {
                        config.Identities[pair.Key] = pair.Value;
                    }

                    config.Daemon = NormalizeDaemonConfig(loaded.Daemon, config.Daemon);
                    config.Development = NormalizeDevelopmentConfig(loaded.Development, config.Development);
                }
            }
            catch
            {
                // Invalid user config should not prevent the daemon or CLI from starting.
            }
        }

        var envExecutionMode = Environment.GetEnvironmentVariable("IDMUX_EXECUTION_MODE");
        if (!string.IsNullOrWhiteSpace(envExecutionMode))
        {
            config.Development.ExecutionMode = envExecutionMode.Trim();
        }

        var captureMode = Environment.GetEnvironmentVariable("IDMUX_CAPTURE_WITHOUT_PROMPT");
        if (bool.TryParse(captureMode, out var captureWithoutPrompt))
        {
            config.Development.CaptureWithoutPrompt = captureWithoutPrompt;
        }

        var envWebPort = Environment.GetEnvironmentVariable("IDMUX_WEB_PORT");
        if (int.TryParse(envWebPort, out var webPort) && webPort > 0)
        {
            config.Daemon.WebPort = webPort;
        }

        return config;
    }

    public static void Save(IdmuxConfig config)
    {
        IdmuxPaths.EnsureHomeExists();

        var options = new JsonSerializerOptions(IdmuxJson.Options)
        {
            WriteIndented = true
        };

        var json = JsonSerializer.Serialize(config, options);
        File.WriteAllText(IdmuxPaths.ConfigFile, json + Environment.NewLine);
    }

    private static DaemonConfig NormalizeDaemonConfig(DaemonConfig loaded, DaemonConfig fallback)
    {
        return new DaemonConfig
        {
            IdleTimeoutMinutes = loaded.IdleTimeoutMinutes > 0 ? loaded.IdleTimeoutMinutes : fallback.IdleTimeoutMinutes,
            CredentialIdleMinutes = loaded.CredentialIdleMinutes > 0 ? loaded.CredentialIdleMinutes : fallback.CredentialIdleMinutes,
            AuditLogEnabled = loaded.AuditLogEnabled,
            AuditLogBodies = loaded.AuditLogBodies,
            WebPort = loaded.WebPort > 0 ? loaded.WebPort : fallback.WebPort,
            BlockBufferSize = loaded.BlockBufferSize > 0 ? loaded.BlockBufferSize : fallback.BlockBufferSize
        };
    }

    private static DevelopmentConfig NormalizeDevelopmentConfig(DevelopmentConfig loaded, DevelopmentConfig fallback)
    {
        return new DevelopmentConfig
        {
            ExecutionMode = string.IsNullOrWhiteSpace(loaded.ExecutionMode) ? fallback.ExecutionMode : loaded.ExecutionMode,
            CaptureWithoutPrompt = loaded.CaptureWithoutPrompt
        };
    }
}
