using RailReader.Core.Services;

namespace RailReader.Core;

/// <summary>
/// Writes log output to stderr and to a session log file in the config directory.
/// Debug messages are suppressed in Release builds (stderr only — still written to the file).
/// The log file path is <see cref="LogFilePath"/>.
/// </summary>
public sealed class ConsoleLogger : ILogger, IDisposable
{
    private readonly StreamWriter? _fileWriter;

    /// <summary>Path to the current session log file, or null if file logging failed.</summary>
    public string? LogFilePath { get; }

    /// <summary>Path to the previous session's log file, or null if unavailable.</summary>
    public string? PreviousLogFilePath { get; }

    public ConsoleLogger()
    {
        try
        {
            var logDir = AppConfig.ConfigDir;
            Directory.CreateDirectory(logDir);
            LogFilePath = Path.Combine(logDir, "session.log");
            var prevPath = Path.Combine(logDir, "session-prev.log");

            // Preserve previous session log (may contain crash info)
            try
            {
                if (File.Exists(LogFilePath))
                {
                    File.Copy(LogFilePath, prevPath, overwrite: true);
                    PreviousLogFilePath = prevPath;
                }
            }
            catch { }

            _fileWriter = new StreamWriter(LogFilePath, append: false) { AutoFlush = true };
            _fileWriter.WriteLine($"--- Session started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---");
        }
        catch
        {
            // File logging is best-effort — continue without it
            _fileWriter = null;
            LogFilePath = null;
        }
    }

    public void Debug(string message)
    {
#if DEBUG
        Console.Error.WriteLine(message);
#endif
        WriteToFile("DBG", message);
    }

    public void Info(string message)
    {
        Console.Error.WriteLine(message);
        WriteToFile("INF", message);
    }

    public void Warn(string message)
    {
        Console.Error.WriteLine($"WARN: {message}");
        WriteToFile("WRN", message);
    }

    public void Error(string message, Exception? ex = null)
    {
        Console.Error.WriteLine(message);
        WriteToFile("ERR", message);
        for (var e = ex; e is not null; e = e.InnerException)
        {
            var line = $"  {e.GetType().Name}: {e.Message}";
            Console.Error.WriteLine(line);
            WriteToFile("ERR", line);
        }
    }

    private void WriteToFile(string level, string message)
    {
        try
        {
            _fileWriter?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}");
        }
        catch
        {
            // Best-effort — don't let logging failures crash the app
        }
    }

    public void Dispose()
    {
        try
        {
            _fileWriter?.WriteLine($"--- Session ended {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---");
            _fileWriter?.Dispose();
        }
        catch { }
    }
}
