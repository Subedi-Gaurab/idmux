using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using Idmux.Core;

namespace Idmux.Daemon;

internal enum CancelReason
{
    None,
    User,
    Timeout,
    Shutdown,
    SessionDestroyed
}

internal sealed class WorkerExecutionResult
{
    public int ExitCode { get; init; }
    public string? Error { get; init; }
    public CancelReason CancelReason { get; init; }
}

internal abstract class PowerShellWorker : IAsyncDisposable
{
    private const int LogonNetCredentialsOnly = 0x00000002;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint HandleFlagInherit = 0x00000001;

    public abstract int? ProcessId { get; }
    public abstract Task StartAsync(CancellationToken cancellationToken);
    public abstract Task<WorkerExecutionResult> ExecuteAsync(string script, JsonNode? args, Action<string> onStdout, Action<string> onStderr, CancellationToken cancellationToken);
    public abstract Task CancelCurrentAsync(CancelReason reason);
    public abstract ValueTask DisposeAsync();

    protected sealed class SpawnedProcess(Process process, StreamWriter standardInput, StreamReader standardOutput, StreamReader standardError)
    {
        public Process Process { get; } = process;
        public StreamWriter StandardInput { get; } = standardInput;
        public StreamReader StandardOutput { get; } = standardOutput;
        public StreamReader StandardError { get; } = standardError;
    }

    public static PowerShellWorker Create(string identity, SecureString? password, bool emulated, string executionMode, DaemonLog log, bool persistent, bool netOnly)
    {
        if (persistent)
        {
            return new PersistentPowerShellWorker(identity, password, emulated, executionMode, log, netOnly);
        }

        return new OneShotPowerShellWorker(identity, password, emulated, executionMode, log, netOnly);
    }

    protected static string ResolvePwshPath()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var standardPath = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
        return File.Exists(standardPath) ? standardPath : "pwsh.exe";
    }

    protected static void ApplyCredentials(ProcessStartInfo startInfo, string identity, SecureString? password, bool emulated, string executionMode)
    {
        if (emulated || !string.Equals(executionMode, "credential", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (password is null || password.Length == 0)
        {
            return;
        }

        var (domain, userName) = IdentityHelpers.SplitIdentity(identity);
        startInfo.UserName = userName;
        startInfo.Domain = string.IsNullOrWhiteSpace(domain) ? Environment.MachineName : domain;
        startInfo.Password = password;
        startInfo.LoadUserProfile = true;
    }

    protected static SpawnedProcess StartProcess(ProcessStartInfo startInfo, string identity, SecureString? password, bool emulated, string executionMode, bool netOnly)
    {
        if (!ShouldUseNetOnly(identity, password, emulated, executionMode, netOnly))
        {
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start pwsh.exe.");
            }

            return new SpawnedProcess(process, process.StandardInput, process.StandardOutput, process.StandardError);
        }

        return StartProcessNetOnly(startInfo, identity, password!);
    }

    private static bool ShouldUseNetOnly(string identity, SecureString? password, bool emulated, string executionMode, bool netOnly)
    {
        // Auto-infer netonly when the identity belongs to a foreign domain that
        // can't log on locally to this box. The explicit --netonly flag still
        // works for edge cases like forcing netonly on a same-domain account.
        var effectiveNetOnly = netOnly || IdentityHelpers.IsForeignDomain(identity);

        return effectiveNetOnly
            && !emulated
            && string.Equals(executionMode, "credential", StringComparison.OrdinalIgnoreCase)
            && password is not null
            && password.Length > 0
            && !IdentityHelpers.IsCurrentUser(identity);
    }

    private static SpawnedProcess StartProcessNetOnly(ProcessStartInfo startInfo, string identity, SecureString password)
    {
        var (domain, userName) = IdentityHelpers.SplitIdentity(identity);
        var resolvedDomain = string.IsNullOrWhiteSpace(domain) ? Environment.MachineName : domain;

        IntPtr passwordPtr = IntPtr.Zero;
        SafeFileHandle? stdinRead = null;
        SafeFileHandle? stdinWrite = null;
        SafeFileHandle? stdoutRead = null;
        SafeFileHandle? stdoutWrite = null;
        SafeFileHandle? stderrRead = null;
        SafeFileHandle? stderrWrite = null;

        try
        {
            passwordPtr = Marshal.SecureStringToGlobalAllocUnicode(password);

            CreatePipePair(out stdinRead, out stdinWrite, inheritRead: true);
            CreatePipePair(out stdoutRead, out stdoutWrite, inheritRead: false);
            CreatePipePair(out stderrRead, out stderrWrite, inheritRead: false);

            var startupInfo = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                dwFlags = StartfUseStdHandles,
                hStdInput = stdinRead.DangerousGetHandle(),
                hStdOutput = stdoutWrite.DangerousGetHandle(),
                hStdError = stderrWrite.DangerousGetHandle()
            };

            var commandLine = BuildCommandLine(startInfo.FileName, startInfo.ArgumentList);

            // Match runas /netonly exactly: pass NULL for environment, NULL for working dir,
            // and only CREATE_NO_WINDOW. Anything else (custom env block, working dir under a
            // protected install directory, CREATE_UNICODE_ENVIRONMENT) makes SSPI fail to apply
            // the alternate network credentials and outbound auth falls back to the caller's
            // identity, producing "untrusted domain" errors against cross-domain network resources.
            if (!CreateProcessWithLogonW(
                    userName,
                    resolvedDomain,
                    passwordPtr,
                    LogonNetCredentialsOnly,
                    null,
                    commandLine,
                    CreateNoWindow,
                    IntPtr.Zero,
                    null,
                    ref startupInfo,
                    out var processInfo))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            stdinRead.Dispose();
            stdinRead = null;
            stdoutWrite.Dispose();
            stdoutWrite = null;
            stderrWrite.Dispose();
            stderrWrite = null;

            var process = Process.GetProcessById(processInfo.dwProcessId);
            process.EnableRaisingEvents = true;

            var stdinStream = new FileStream(stdinWrite!, FileAccess.Write, 4096, isAsync: false);
            var stdoutStream = new FileStream(stdoutRead!, FileAccess.Read, 4096, isAsync: false);
            var stderrStream = new FileStream(stderrRead!, FileAccess.Read, 4096, isAsync: false);
            var stdinWriter = CreateInputWriter(stdinStream);
            var stdoutReader = CreateOutputReader(stdoutStream);
            var stderrReader = CreateOutputReader(stderrStream);

            CloseHandle(processInfo.hThread);
            CloseHandle(processInfo.hProcess);

            stdinWrite = null;
            stdoutRead = null;
            stderrRead = null;

            return new SpawnedProcess(process, stdinWriter, stdoutReader, stderrReader);
        }
        finally
        {
            if (passwordPtr != IntPtr.Zero)
            {
                Marshal.ZeroFreeGlobalAllocUnicode(passwordPtr);
            }

            stdinRead?.Dispose();
            stdinWrite?.Dispose();
            stdoutRead?.Dispose();
            stdoutWrite?.Dispose();
            stderrRead?.Dispose();
            stderrWrite?.Dispose();
        }
    }

    private static StreamWriter CreateInputWriter(FileStream stream)
    {
        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
    }

    private static StreamReader CreateOutputReader(FileStream stream)
    {
        return new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
    }

    private static string BuildCommandLine(string fileName, IReadOnlyList<string> args)
    {
        var builder = new StringBuilder();
        builder.Append(QuoteWindowsArgument(fileName));
        foreach (var arg in args)
        {
            builder.Append(' ');
            builder.Append(QuoteWindowsArgument(arg));
        }

        return builder.ToString();
    }

    private static string QuoteWindowsArgument(string argument)
    {
        if (argument.Length == 0)
        {
            return "\"\"";
        }

        var needsQuotes = argument.Any(ch => char.IsWhiteSpace(ch) || ch == '"');
        if (!needsQuotes)
        {
            return argument;
        }

        var result = new StringBuilder();
        result.Append('"');
        var backslashes = 0;
        foreach (var ch in argument)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }

            if (ch == '"')
            {
                result.Append('\\', backslashes * 2 + 1);
                result.Append('"');
                backslashes = 0;
                continue;
            }

            if (backslashes > 0)
            {
                result.Append('\\', backslashes);
                backslashes = 0;
            }

            result.Append(ch);
        }

        if (backslashes > 0)
        {
            result.Append('\\', backslashes * 2);
        }

        result.Append('"');
        return result.ToString();
    }

    private static IntPtr BuildEnvironmentBlock(IDictionary<string, string?> environment)
    {
        if (environment.Count == 0)
        {
            return IntPtr.Zero;
        }

        var builder = new StringBuilder();
        foreach (var pair in environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(pair.Key);
            builder.Append('=');
            builder.Append(pair.Value ?? string.Empty);
            builder.Append('\0');
        }
        builder.Append('\0');

        return Marshal.StringToHGlobalUni(builder.ToString());
    }

    private static void CreatePipePair(out SafeFileHandle readHandle, out SafeFileHandle writeHandle, bool inheritRead)
    {
        var securityAttributes = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            bInheritHandle = true,
            lpSecurityDescriptor = IntPtr.Zero
        };

        if (!CreatePipe(out readHandle, out writeHandle, ref securityAttributes, 0))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        var nonInherited = inheritRead ? writeHandle : readHandle;
        if (!SetHandleInformation(nonInherited, HandleFlagInherit, 0))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithLogonW(
        string lpUsername,
        string lpDomain,
        IntPtr lpPassword,
        int dwLogonFlags,
        string? lpApplicationName,
        string lpCommandLine,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(SafeFileHandle hObject, uint dwMask, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    protected static async Task PumpStreamAsync(StreamReader reader, Action<string>? onData, CancellationToken cancellationToken)
    {
        var buffer = new char[8192];
        while (!cancellationToken.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (read == 0)
            {
                break;
            }

            try
            {
                onData?.Invoke(new string(buffer, 0, read));
            }
            catch
            {
                // Never let a chunk callback take the pump down.
            }
        }
    }

    /// <summary>
    /// Shared emulator routine used by both worker flavours. Echoes a deterministic
    /// line so JSON-based tests can assert. Honors a tiny "sleep:N" directive so
    /// tests that need a cancellable window can spin one up without forking pwsh.
    /// </summary>
    protected static WorkerExecutionResult RunEmulatedScript(string script, string identity, Action<string> onStdout, object cancelGate, Func<CancelReason> readCancelReason, Action<CancelReason>? writeCancelReason = null)
    {
        var firstLine = script.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
        var sleepSeconds = TryParseSleepDirective(firstLine);
        if (sleepSeconds > 0)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(sleepSeconds);
            while (DateTimeOffset.UtcNow < deadline)
            {
                CancelReason currentReason;
                lock (cancelGate)
                {
                    currentReason = readCancelReason();
                }

                if (currentReason != CancelReason.None)
                {
                    return new WorkerExecutionResult
                    {
                        ExitCode = 1,
                        CancelReason = currentReason
                    };
                }

                Thread.Sleep(50);
            }
        }

        onStdout($"emulated:{identity}:{firstLine}{Environment.NewLine}");
        return new WorkerExecutionResult { ExitCode = 0 };
    }

    private static int TryParseSleepDirective(string firstLine)
    {
        if (!firstLine.StartsWith("sleep:", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var rest = firstLine[6..].Trim();
        return int.TryParse(rest, out var seconds) && seconds > 0 ? seconds : 0;
    }
}

/// <summary>
/// One-shot worker: spawns a fresh pwsh.exe per ExecuteAsync, runs the script via
/// <c>pwsh.exe -NoProfile -Command "&amp; { script } @args"</c>, and pumps the
/// process's raw stdout/stderr bytes through onStdout/onStderr.
/// No JSON wrapping. No Out-String. Bytes are what pwsh wrote.
/// </summary>
internal sealed class OneShotPowerShellWorker(string identity, SecureString? password, bool emulated, string executionMode, DaemonLog log, bool netOnly) : PowerShellWorker
{
    private readonly string _identity = identity;
    private readonly SecureString? _password = password;
    private readonly bool _emulated = emulated;
    private readonly string _executionMode = executionMode;
    private readonly DaemonLog _log = log;
    private readonly bool _netOnly = netOnly;
    private readonly object _gate = new();
    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private StreamReader? _stderr;
    private CancelReason _cancelReason;

    public override int? ProcessId
    {
        get
        {
            lock (_gate)
            {
                return _process is { HasExited: false } process ? process.Id : null;
            }
        }
    }

    public override Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override async Task<WorkerExecutionResult> ExecuteAsync(string script, JsonNode? args, Action<string> onStdout, Action<string> onStderr, CancellationToken cancellationToken)
    {
        if (_emulated)
        {
            return await Task.Run(() => RunEmulated(script, onStdout), cancellationToken);
        }

        var commandLine = BuildCommandLine(script, args);
        var startInfo = new ProcessStartInfo(ResolvePwshPath())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-OutputFormat");
        startInfo.ArgumentList.Add("Text");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(commandLine);

        ApplyCredentials(startInfo, _identity, _password, _emulated, _executionMode);

        SpawnedProcess spawned;
        try
        {
            spawned = StartProcess(startInfo, _identity, _password, _emulated, _executionMode, _netOnly);
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.Message.Contains("cannot find", StringComparison.OrdinalIgnoreCase))
        {
            throw new RpcException(RpcErrorCodes.PwshNotFound, "pwsh.exe was not found.");
        }

        var process = spawned.Process;
        lock (_gate)
        {
            _process = process;
            _stdin = spawned.StandardInput;
            _stdout = spawned.StandardOutput;
            _stderr = spawned.StandardError;
            _cancelReason = CancelReason.None;
        }

        // Close stdin immediately \u2014 the script has no further input source.
        try
        {
            spawned.StandardInput.Close();
        }
        catch
        {
        }

        var stdoutTask = PumpStreamAsync(spawned.StandardOutput, onStdout, cancellationToken);
        var stderrTask = PumpStreamAsync(spawned.StandardError, onStderr, cancellationToken);

        await Task.WhenAll(stdoutTask, stderrTask);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }

        CancelReason cancelReason;
        int exitCode;
        lock (_gate)
        {
            cancelReason = _cancelReason;
        }

        try
        {
            exitCode = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            exitCode = cancelReason != CancelReason.None ? 1 : 0;
        }

        return new WorkerExecutionResult
        {
            ExitCode = exitCode,
            CancelReason = cancelReason
        };
    }

    public override Task CancelCurrentAsync(CancelReason reason)
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            if (process is null)
            {
                return Task.CompletedTask;
            }

            _cancelReason = reason;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception)
        {
            _log.Error(exception, $"Failed to cancel one-shot worker for {_identity}.");
        }

        return Task.CompletedTask;
    }

    public override ValueTask DisposeAsync()
    {
        Process? process;
        StreamWriter? stdin;
        StreamReader? stdout;
        StreamReader? stderr;
        lock (_gate)
        {
            process = _process;
            _process = null;
            stdin = _stdin;
            stdout = _stdout;
            stderr = _stderr;
            _stdin = null;
            _stdout = null;
            _stderr = null;
        }

        if (process is { HasExited: false })
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }

        stdin?.Dispose();
        stdout?.Dispose();
        stderr?.Dispose();
        process?.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string BuildCommandLine(string script, JsonNode? args)
    {
        // The user's script is passed verbatim. Any args are wrapped into a parameter array.
        // We invoke the script as a fresh scriptblock so $script:LASTEXITCODE propagates.
        var prelude = new StringBuilder();
        prelude.Append("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; ");
        prelude.Append("$OutputEncoding = [System.Text.Encoding]::UTF8; ");

        if (args is JsonArray array)
        {
            prelude.Append("$idmuxArgs = @(");
            for (var i = 0; i < array.Count; i++)
            {
                if (i > 0) prelude.Append(", ");
                prelude.Append(JsonNodeToPwshLiteral(array[i]));
            }

            prelude.Append("); ");
            prelude.Append("& {").Append(script).Append("} @idmuxArgs");
        }
        else if (args is not null)
        {
            prelude.Append("$idmuxArgs = ").Append(JsonNodeToPwshLiteral(args)).Append("; ");
            prelude.Append("& {").Append(script).Append("} $idmuxArgs");
        }
        else
        {
            prelude.Append("& {").Append(script).Append("}");
        }

        // Honor a non-zero LASTEXITCODE as the process exit.
        prelude.Append("; if ($LASTEXITCODE) { exit [int]$LASTEXITCODE }");

        return prelude.ToString();
    }

    private static string JsonNodeToPwshLiteral(JsonNode? node)
    {
        if (node is null)
        {
            return "$null";
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var boolValue))
            {
                return boolValue ? "$true" : "$false";
            }

            if (value.TryGetValue<long>(out var longValue))
            {
                return longValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (value.TryGetValue<double>(out var doubleValue))
            {
                return doubleValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (value.TryGetValue<string>(out var stringValue))
            {
                return $"'{stringValue.Replace("'", "''")}'";
            }
        }

        // Fallback: pass JSON text and let the script decide what to do.
        var json = node.ToJsonString();
        return $"(ConvertFrom-Json -InputObject '{json.Replace("'", "''")}' -Depth 20)";
    }

    private WorkerExecutionResult RunEmulated(string script, Action<string> onStdout)
    {
        return PowerShellWorker.RunEmulatedScript(script, _identity, onStdout, _gate, () => _cancelReason, reason => _cancelReason = reason);
    }
}

/// <summary>
/// Persistent worker: keeps a single pwsh.exe alive across many ExecuteAsync calls
/// so variables and modules survive. Commands are dispatched on the bootstrap's
/// stdin; the user's script writes to real stdout/stderr (raw). A sentinel line
/// after each command lets the daemon detect completion without parsing user output.
/// </summary>
internal sealed class PersistentPowerShellWorker : PowerShellWorker
{
    private readonly string _identity;
    private readonly SecureString? _password;
    private readonly bool _emulated;
    private readonly string _executionMode;
    private readonly DaemonLog _log;
    private readonly object _gate = new();
    private readonly string _workerId;
    private readonly string _stdoutSentinelBase;
    private readonly string _stderrSentinelBase;
    private readonly string _helloSentinel;

    private Process? _process;
    private StreamWriter? _stdin;
    private Task? _stdoutPump;
    private Task? _stderrPump;
    private string? _bootstrapPath;
    private TaskCompletionSource<bool>? _helloCompletion;
    private bool _receivedHello;

    // Per-active-command state \u2014 protected by _gate.
    private TaskCompletionSource<WorkerExecutionResult>? _currentCompletion;
    private Action<string>? _currentOnStdout;
    private Action<string>? _currentOnStderr;
    private CancelReason _currentCancelReason;
    private string? _currentCmdId;
    private int _currentExitCode;
    private bool _currentStdoutDone;
    private bool _currentStderrDone;

    private readonly bool _netOnly;

    public PersistentPowerShellWorker(string identity, SecureString? password, bool emulated, string executionMode, DaemonLog log, bool netOnly)
    {
        _identity = identity;
        _password = password;
        _emulated = emulated;
        _executionMode = executionMode;
        _log = log;
        _netOnly = netOnly;
        _workerId = Guid.NewGuid().ToString("N");
        _stdoutSentinelBase = $"__IDMUX_{_workerId}_OUT__";
        _stderrSentinelBase = $"__IDMUX_{_workerId}_ERR__";
        _helloSentinel = $"__IDMUX_{_workerId}_HELLO__";
    }

    public override int? ProcessId
    {
        get
        {
            lock (_gate)
            {
                return _process is { HasExited: false } process ? process.Id : null;
            }
        }
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_process is { HasExited: false })
            {
                return;
            }
        }

        if (_emulated)
        {
            // Emulated persistent worker doesn't fork a real pwsh.
            _receivedHello = true;
            return;
        }

        _bootstrapPath = WriteBootstrap();

        var startInfo = new ProcessStartInfo(ResolvePwshPath())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-OutputFormat");
        startInfo.ArgumentList.Add("Text");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(_bootstrapPath);
        startInfo.ArgumentList.Add(_stdoutSentinelBase);
        startInfo.ArgumentList.Add(_stderrSentinelBase);
        startInfo.ArgumentList.Add(_helloSentinel);

        ApplyCredentials(startInfo, _identity, _password, _emulated, _executionMode);

        SpawnedProcess spawned;
        try
        {
            spawned = StartProcess(startInfo, _identity, _password, _emulated, _executionMode, _netOnly);
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.Message.Contains("cannot find", StringComparison.OrdinalIgnoreCase))
        {
            throw new RpcException(RpcErrorCodes.PwshNotFound, "pwsh.exe was not found.");
        }

        var process = spawned.Process;
        process.Exited += OnProcessExited;
        var helloCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _process = process;
            _stdin = spawned.StandardInput;
            _helloCompletion = helloCompletion;
            _receivedHello = false;
        }

        _stdoutPump = Task.Run(() => PumpReaderAsync(spawned.StandardOutput, isStderr: false, CancellationToken.None));
        _stderrPump = Task.Run(() => PumpReaderAsync(spawned.StandardError, isStderr: true, CancellationToken.None));

        using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        helloTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        await helloCompletion.Task.WaitAsync(helloTimeout.Token);
    }

    public override async Task<WorkerExecutionResult> ExecuteAsync(string script, JsonNode? args, Action<string> onStdout, Action<string> onStderr, CancellationToken cancellationToken)
    {
        await StartAsync(cancellationToken);

        if (_emulated)
        {
            return await Task.Run(() => RunEmulatedScript(script, _identity, onStdout, _gate, () => _currentCancelReason), cancellationToken);
        }

        var cmdId = $"cmd_{Guid.NewGuid():N}"[..10];
        var completion = new TaskCompletionSource<WorkerExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            if (_currentCompletion is not null)
            {
                throw new InvalidOperationException("Persistent worker already has an active command.");
            }

            _currentCompletion = completion;
            _currentOnStdout = onStdout;
            _currentOnStderr = onStderr;
            _currentCancelReason = CancelReason.None;
            _currentCmdId = cmdId;
            _currentStdoutDone = false;
            _currentStderrDone = false;
            _currentExitCode = 0;
        }

        var payload = new JsonObject
        {
            ["kind"] = "execute",
            ["cmdId"] = cmdId,
            ["script"] = script
        };

        if (args is not null)
        {
            payload["argsJson"] = args.ToJsonString(IdmuxJson.Options);
        }

        StreamWriter? stdin;
        lock (_gate)
        {
            stdin = _stdin;
        }

        if (stdin is null)
        {
            throw new InvalidOperationException("Persistent worker stdin is not available.");
        }

        await stdin.WriteLineAsync(payload.ToJsonString(IdmuxJson.Options));
        await stdin.FlushAsync();

        return await completion.Task.WaitAsync(cancellationToken);
    }

    public override Task CancelCurrentAsync(CancelReason reason)
    {
        Process? process;
        TaskCompletionSource<WorkerExecutionResult>? completion;
        lock (_gate)
        {
            process = _process;
            completion = _currentCompletion;
            _currentCancelReason = reason;
        }

        if (process is null || completion is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception)
        {
            _log.Error(exception, $"Failed to cancel persistent worker for {_identity}.");
        }

        return Task.CompletedTask;
    }

    public override async ValueTask DisposeAsync()
    {
        StreamWriter? stdin;
        Process? process;
        string? bootstrapPath;
        Task? stdoutPump;
        Task? stderrPump;

        lock (_gate)
        {
            stdin = _stdin;
            process = _process;
            bootstrapPath = _bootstrapPath;
            stdoutPump = _stdoutPump;
            stderrPump = _stderrPump;
            _stdin = null;
            _process = null;
            _bootstrapPath = null;
            _stdoutPump = null;
            _stderrPump = null;
        }

        try
        {
            if (stdin is not null)
            {
                await stdin.WriteLineAsync("""{"kind":"shutdown"}""");
                await stdin.FlushAsync();
            }
        }
        catch
        {
        }

        if (process is { HasExited: false })
        {
            try
            {
                if (!process.WaitForExit(500))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        }

        if (stdoutPump is not null)
        {
            try { await stdoutPump; } catch { }
        }

        if (stderrPump is not null)
        {
            try { await stderrPump; } catch { }
        }

        stdin?.Dispose();
        process?.Dispose();

        if (!string.IsNullOrEmpty(bootstrapPath))
        {
            try { File.Delete(bootstrapPath); } catch { }
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        TaskCompletionSource<bool>? helloCompletion;
        TaskCompletionSource<WorkerExecutionResult>? currentCompletion;
        CancelReason cancelReason;

        lock (_gate)
        {
            helloCompletion = _helloCompletion;
            currentCompletion = _currentCompletion;
            cancelReason = _currentCancelReason;
            _currentCompletion = null;
        }

        if (helloCompletion is not null && !_receivedHello)
        {
            helloCompletion.TrySetException(new IOException($"Persistent worker for {_identity} exited before bootstrap completed."));
        }

        currentCompletion?.TrySetResult(new WorkerExecutionResult
        {
            ExitCode = 1,
            CancelReason = cancelReason,
            Error = cancelReason switch
            {
                CancelReason.User => "Cancelled by user.",
                CancelReason.Timeout => "Script timed out.",
                CancelReason.SessionDestroyed => "Session was destroyed.",
                CancelReason.Shutdown => "Daemon is shutting down.",
                _ => "Persistent worker exited unexpectedly."
            }
        });
    }

    private async Task PumpReaderAsync(StreamReader reader, bool isStderr, CancellationToken cancellationToken)
    {
        var residual = new StringBuilder();
        var buffer = new char[8192];
        while (!cancellationToken.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            }
            catch (Exception)
            {
                break;
            }

            if (read == 0)
            {
                FlushResidual(residual, isStderr);
                break;
            }

            residual.Append(buffer, 0, read);
            ProcessResidual(residual, isStderr);
        }
    }

    private void ProcessResidual(StringBuilder residual, bool isStderr)
    {
        var sentinelBase = isStderr ? _stderrSentinelBase : _stdoutSentinelBase;

        while (true)
        {
            var text = residual.ToString();

            // Look for sentinel anywhere in the buffer.
            var idx = text.IndexOf(sentinelBase, StringComparison.Ordinal);
            if (idx < 0)
            {
                // No sentinel. Emit all but a trailing window in case sentinel spans the next read.
                EmitSafePrefix(residual, sentinelBase.Length, isStderr);
                return;
            }

            // Emit everything before the sentinel as raw bytes.
            if (idx > 0)
            {
                var chunk = text.Substring(0, idx);
                InvokeChunkCallback(chunk, isStderr);
            }

            // Find the end of the sentinel line ("\n" terminator).
            var lineEnd = text.IndexOf('\n', idx);
            if (lineEnd < 0)
            {
                // Sentinel started but the terminating newline hasn't arrived yet. Keep waiting.
                residual.Remove(0, idx);
                return;
            }

            var sentinelLine = text.Substring(idx + sentinelBase.Length, lineEnd - idx - sentinelBase.Length).TrimEnd('\r');

            // Hello sentinel?
            if (sentinelLine.StartsWith("HELLO|", StringComparison.Ordinal))
            {
                _receivedHello = true;
                TaskCompletionSource<bool>? helloCompletion;
                lock (_gate)
                {
                    helloCompletion = _helloCompletion;
                }
                helloCompletion?.TrySetResult(true);
            }
            else
            {
                // Format: "{cmdId}|{exitCode}" or "{cmdId}" for stderr (no exit).
                var pipe = sentinelLine.IndexOf('|');
                var exitCode = 0;
                if (!isStderr && pipe >= 0)
                {
                    if (int.TryParse(sentinelLine.AsSpan(pipe + 1), out var parsed))
                    {
                        exitCode = parsed;
                    }
                }

                SignalCommandEnd(isStderr, exitCode);
            }

            residual.Remove(0, lineEnd + 1);
        }
    }

    private void EmitSafePrefix(StringBuilder residual, int sentinelLength, bool isStderr)
    {
        var keep = Math.Min(residual.Length, sentinelLength);
        var emit = residual.Length - keep;
        if (emit <= 0)
        {
            return;
        }

        var chunk = residual.ToString(0, emit);
        InvokeChunkCallback(chunk, isStderr);
        residual.Remove(0, emit);
    }

    private void FlushResidual(StringBuilder residual, bool isStderr)
    {
        if (residual.Length == 0)
        {
            return;
        }

        InvokeChunkCallback(residual.ToString(), isStderr);
        residual.Clear();
    }

    private void InvokeChunkCallback(string chunk, bool isStderr)
    {
        if (string.IsNullOrEmpty(chunk))
        {
            return;
        }

        Action<string>? callback;
        lock (_gate)
        {
            callback = isStderr ? _currentOnStderr : _currentOnStdout;
        }

        try
        {
            callback?.Invoke(chunk);
        }
        catch
        {
        }
    }

    private void SignalCommandEnd(bool isStderr, int exitCode)
    {
        TaskCompletionSource<WorkerExecutionResult>? completion = null;
        WorkerExecutionResult? result = null;

        lock (_gate)
        {
            if (isStderr)
            {
                _currentStderrDone = true;
            }
            else
            {
                _currentStdoutDone = true;
                _currentExitCode = exitCode;
            }

            if (_currentStdoutDone && _currentStderrDone && _currentCompletion is not null)
            {
                completion = _currentCompletion;
                result = new WorkerExecutionResult
                {
                    ExitCode = _currentExitCode,
                    CancelReason = _currentCancelReason
                };
                _currentCompletion = null;
                _currentOnStdout = null;
                _currentOnStderr = null;
                _currentCmdId = null;
            }
        }

        completion?.TrySetResult(result!);
    }

    private string WriteBootstrap()
    {
        Directory.CreateDirectory(IdmuxPaths.SharedTempDirectory);
        var path = Path.Combine(IdmuxPaths.SharedTempDirectory, $"idmux-persistent-{_workerId}.ps1");
        File.WriteAllText(path, BootstrapScript);
        return path;
    }

    private const string BootstrapScript = """
param(
    [Parameter(Mandatory)] [string]$StdoutSentinel,
    [Parameter(Mandatory)] [string]$StderrSentinel,
    [Parameter(Mandatory)] [string]$HelloSentinel
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Continue'

# We dot-source the user's scriptblock below so `$variable` assignments persist
# across `idmux run` calls (sessions are persistent by definition). Dot-sourcing
# means the bootstrap and the user script share one scope, so we have to
# harden protocol-critical state against accidental clobbering by user code:
#   * Sentinel bases are pinned as Constants. Any user assignment to them
#     becomes a terminating error caught by the per-dispatch try/catch below;
#     the daemon still sees a valid sentinel and the user just gets exit 1.
#   * The original parameter variables are removed so user code cannot read
#     or write them by their plain names.
#   * Internal loop variables use the reserved `__idmux_` prefix.
Set-Variable -Name __idmux_OutSent -Value $StdoutSentinel -Option Constant -Scope Script
Set-Variable -Name __idmux_ErrSent -Value $StderrSentinel -Option Constant -Scope Script
Remove-Variable -Name StdoutSentinel, StderrSentinel, HelloSentinel -ErrorAction SilentlyContinue

# Tell the daemon we're alive. Single matched pair so both stream readers unblock.
[Console]::Out.WriteLine($__idmux_OutSent + 'HELLO|0')
[Console]::Out.Flush()
[Console]::Error.WriteLine($__idmux_ErrSent + 'HELLO|0')
[Console]::Error.Flush()

while ($true) {
    $__idmux_line = [Console]::In.ReadLine()
    if ($null -eq $__idmux_line) { break }

    try {
        $__idmux_cmd = $__idmux_line | ConvertFrom-Json -Depth 20
    } catch {
        continue
    }

    if ($__idmux_cmd.kind -eq 'shutdown') { break }
    if ($__idmux_cmd.kind -ne 'execute') { continue }

    $__idmux_cmdId = [string]$__idmux_cmd.cmdId
    $__idmux_exitCode = 0

    $__idmux_argList = @()
    if ($__idmux_cmd.argsJson) {
        try {
            $__idmux_parsed = $__idmux_cmd.argsJson | ConvertFrom-Json -Depth 20
            if ($__idmux_parsed -is [Array]) { $__idmux_argList = @($__idmux_parsed) }
            elseif ($null -ne $__idmux_parsed) { $__idmux_argList = @($__idmux_parsed) }
        } catch {
            $__idmux_argList = @()
        }
    }

    $global:LASTEXITCODE = 0

    try {
        $__idmux_sb = [scriptblock]::Create([string]$__idmux_cmd.script)
        # Dot-source so user `$x = 42` lands in the bootstrap's script scope and
        # is visible to the next dispatch. `& $sb` would push assignments into a
        # child scope and lose them on return, breaking session persistence.
        . $__idmux_sb @__idmux_argList
        if ($global:LASTEXITCODE) { $__idmux_exitCode = [int]$global:LASTEXITCODE }
    } catch {
        $__idmux_msg = ($_ | Out-String)
        [Console]::Error.Write($__idmux_msg)
        $__idmux_exitCode = 1
    }

    [Console]::Out.Flush()
    [Console]::Error.Flush()
    [Console]::Out.WriteLine($__idmux_OutSent + $__idmux_cmdId + '|' + $__idmux_exitCode)
    [Console]::Out.Flush()
    [Console]::Error.WriteLine($__idmux_ErrSent + $__idmux_cmdId)
    [Console]::Error.Flush()
}
""";
}
