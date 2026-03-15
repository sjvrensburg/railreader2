using RailReader.Cli.Output;

namespace RailReader.Cli.Commands;

/// <summary>
/// Static context providing the shared CliSession and IOutputFormatter
/// to command handlers. Set by Program.cs before command invocation.
/// </summary>
public static class SessionBinder
{
    public static CliSession Session
    {
        get => _session ?? throw new InvalidOperationException("Session not initialized");
        set => _session = value;
    }

    public static IOutputFormatter Formatter
    {
        get => _formatter ?? throw new InvalidOperationException("Formatter not initialized");
        set => _formatter = value;
    }

    private static CliSession? _session;
    private static IOutputFormatter? _formatter;
}
