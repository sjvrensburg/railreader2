using System.Runtime.InteropServices;
using RailReader.Core;

namespace RailReader2;

/// <summary>
/// Hooks the POSIX signals exposed by .NET's PosixSignalRegistration (SIGQUIT, SIGHUP,
/// SIGINT). SIGABRT/SIGSEGV are NOT exposed — the runtime handles them internally to
/// generate crash dumps. We log the non-crash signals to distinguish clean termination
/// from native crashes (absence of any signal log + absence of ProcessExit = native abort).
/// </summary>
internal static class NativeSignalTrap
{
    public static void Install(ILogger logger)
    {
        try
        {
            PosixSignalRegistration.Create(PosixSignal.SIGQUIT, ctx => Report(logger, "SIGQUIT", ctx));
            PosixSignalRegistration.Create(PosixSignal.SIGHUP,  ctx => Report(logger, "SIGHUP",  ctx));
            PosixSignalRegistration.Create(PosixSignal.SIGINT,  ctx => Report(logger, "SIGINT",  ctx));
        }
        catch (Exception ex)
        {
            logger.Warn($"NativeSignalTrap install failed: {ex.Message}");
        }
    }

    private static void Report(ILogger logger, string name, PosixSignalContext ctx)
    {
        try
        {
            logger.Error($"[FATAL] Native signal {name} received (thread {Environment.CurrentManagedThreadId})");
        }
        catch { }
    }
}
