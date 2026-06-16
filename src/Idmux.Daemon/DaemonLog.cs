using System.IO;
using System.Text;

namespace Idmux.Daemon;

internal sealed class DaemonLog(string path)
{
    private readonly object _gate = new();
    private readonly string _path = path;

    public void Info(string message) => Write("INFO", message);
    public void Error(string message) => Write("ERROR", message);
    public void Error(Exception exception, string message) => Write("ERROR", $"{message} :: {exception.GetType().Name}: {exception.Message}");

    private void Write(string level, string message)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var line = $"{DateTimeOffset.UtcNow:O} [{level}] {message}{Environment.NewLine}";

        lock (_gate)
        {
            File.AppendAllText(_path, line, Encoding.UTF8);
        }
    }
}
