using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Idmux.Core;

public static class IdmuxJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public static JsonNode? ToNode<T>(T value)
    {
        return JsonSerializer.SerializeToNode(value, Options);
    }

    public static T Deserialize<T>(JsonNode? node)
    {
        return node.Deserialize<T>(Options)
            ?? throw new InvalidOperationException($"Unable to deserialize {typeof(T).Name}.");
    }
}

public static class IdmuxPaths
{
    public static string Home => Environment.GetEnvironmentVariable("IDMUX_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "idmux");

    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        "idmux");

    public static string ConfigFile => Path.Combine(Home, "config.json");
    public static string AuditDirectory => Path.Combine(Home, "audit");
    public static string LogFile => Path.Combine(Home, "daemon.log");
    public static string SharedTempDirectory => @"C:\temp";
    public static string StartupRunKeyName => "idmux-daemon";

    public static string CurrentPipeName => $"idmux-{CurrentSessionId}";
    public static string CurrentMutexName => $@"Local\idmux-daemon-{CurrentSessionId}";

    public static int CurrentSessionId
    {
        get
        {
            try
            {
                return Process.GetCurrentProcess().SessionId;
            }
            catch
            {
                return 0;
            }
        }
    }

    public static void EnsureHomeExists()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(AuditDirectory);
        Directory.CreateDirectory(SharedTempDirectory);
    }
}

public static class PipeFraming
{
    public static async Task WriteMessageAsync(Stream stream, string message, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(message);
        var prefix = ArrayPool<byte>.Shared.Rent(4);
        try
        {
            BinaryPrimitives.WriteInt32BigEndian(prefix.AsSpan(0, 4), payload.Length);
            await stream.WriteAsync(prefix.AsMemory(0, 4), cancellationToken);
            await stream.WriteAsync(payload, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(prefix);
        }
    }

    public static async Task<string?> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = ArrayPool<byte>.Shared.Rent(4);
        try
        {
            var bytesRead = await ReadExactAsync(stream, prefix.AsMemory(0, 4), cancellationToken);
            if (bytesRead == 0)
            {
                return null;
            }

            var length = BinaryPrimitives.ReadInt32BigEndian(prefix.AsSpan(0, 4));
            if (length < 0)
            {
                throw new InvalidDataException("Negative message length.");
            }

            var payload = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                await ReadFullyAsync(stream, payload.AsMemory(0, length), cancellationToken);
                return Encoding.UTF8.GetString(payload, 0, length);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(payload);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(prefix);
        }
    }

    private static async Task<int> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[totalRead..], cancellationToken);
            if (read == 0)
            {
                return totalRead;
            }

            totalRead += read;
        }

        return totalRead;
    }

    private static async Task ReadFullyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[totalRead..], cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected end of stream.");
            }

            totalRead += read;
        }
    }
}

public static class IdentityHelpers
{
    /// <summary>
    /// "DOMAIN\user" of the process running this code (the daemon, in production).
    /// Used to recognise when callers ask to run "as themselves" so we can skip
    /// the credential cache and just inherit the daemon's logon token.
    /// </summary>
    public static string CurrentUserName { get; } = $"{Environment.UserDomainName}\\{Environment.UserName}";

    /// <summary>
    /// True if <paramref name="identity"/> refers to the user the daemon is running
    /// as. Accepts both "DOMAIN\user" and bare "user" forms, case-insensitive.
    /// </summary>
    public static bool IsCurrentUser(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
        {
            return false;
        }

        if (string.Equals(identity, CurrentUserName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var (domain, userName) = SplitIdentity(identity);
        if (domain is null)
        {
            return string.Equals(userName, Environment.UserName, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(domain, Environment.UserDomainName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(userName, Environment.UserName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True if <paramref name="identity"/> belongs to a domain other than the
    /// one the current process (the daemon) is joined to.  Foreign-domain
    /// identities cannot perform a normal interactive logon on this box, so
    /// they must use LOGON_NETCREDENTIALS_ONLY (runas /netonly) semantics.
    /// Returns false for bare usernames (no domain part) since those are
    /// assumed to be local/same-domain.
    /// </summary>
    public static bool IsForeignDomain(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
        {
            return false;
        }

        var (domain, _) = SplitIdentity(identity);
        if (string.IsNullOrWhiteSpace(domain))
        {
            return false;
        }

        return !string.Equals(domain, Environment.UserDomainName, StringComparison.OrdinalIgnoreCase);
    }

    public static (string? Domain, string UserName) SplitIdentity(string identity)
    {
        var parts = identity.Split('\\', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2
            ? (parts[0], parts[1])
            : (null, identity);
    }

    public static string Slugify(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (builder.Length == 0 || builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "session" : slug;
    }

    public static string DisplayToken(string identity, IdmuxConfig config)
    {
        if (config.Identities.TryGetValue(identity, out var display) && !string.IsNullOrWhiteSpace(display.Display))
        {
            return Slugify(display.Display);
        }

        var (_, userName) = SplitIdentity(identity);
        return Slugify(userName);
    }
}
