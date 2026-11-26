using System.IO;
using Nocturnal.Core;

namespace Nocturnal.App.Services;

public sealed class AppLogger : IAppLogger
{
    private const long MaxLogBytes = 512 * 1024;
    private readonly object _gate = new();
    private readonly string _logPath;

    public AppLogger(string? baseDir = null)
    {
        var dir = baseDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Nocturnal");
        Directory.CreateDirectory(dir);
        _logPath = Path.Combine(dir, "nocturnal.log");
    }

    public void Info(string message) => Write("INFO", message, null);
    public void Warn(string message) => Write("WARN", message, null);
    public void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (_gate)
            {
                if (File.Exists(_logPath) && new FileInfo(_logPath).Length >= MaxLogBytes)
                {
                    var backup = _logPath + ".1";
                    File.Move(_logPath, backup, overwrite: true);
                }

                var line = ex is null
                    ? $"{DateTimeOffset.UtcNow:O} [{level}] {message}{Environment.NewLine}"
                    : $"{DateTimeOffset.UtcNow:O} [{level}] {message} :: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}";
                File.AppendAllText(_logPath, line);
            }
        }
        catch
        {
            // Logging failures must never crash power management or UI threads.
        }
    }
}
