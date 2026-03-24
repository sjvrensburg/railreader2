namespace RailReader.Core;

/// <summary>
/// Writes log output to stderr. Debug messages are suppressed in Release builds.
/// </summary>
public sealed class ConsoleLogger : ILogger
{
    public void Debug(string message)
    {
#if DEBUG
        Console.Error.WriteLine(message);
#endif
    }

    public void Info(string message) => Console.Error.WriteLine(message);

    public void Warn(string message) => Console.Error.WriteLine($"WARN: {message}");

    public void Error(string message, Exception? ex = null)
    {
        Console.Error.WriteLine(message);
        for (var e = ex; e is not null; e = e.InnerException)
            Console.Error.WriteLine($"  {e.GetType().Name}: {e.Message}");
    }
}
