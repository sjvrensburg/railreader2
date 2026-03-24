namespace RailReader.Core;

/// <summary>
/// Minimal logging abstraction. Implementations control output destination
/// and level filtering. Follows the IThreadMarshaller pattern — interface
/// in Core, implementations provided by the host (UI, tests, CLI).
/// </summary>
public interface ILogger
{
    void Debug(string message);
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);

    /// <summary>Path to the log file, or null if this logger doesn't write to a file.</summary>
    string? LogFilePath => null;
}

/// <summary>
/// Discards all log output. Default for tests and headless contexts.
/// </summary>
public sealed class NullLogger : ILogger
{
    public static readonly NullLogger Instance = new();
    public void Debug(string message) { }
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? ex = null) { }
}
