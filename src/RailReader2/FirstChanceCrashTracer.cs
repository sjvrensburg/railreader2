using System.Runtime.ExceptionServices;
using RailReader.Core;

namespace RailReader2;

/// <summary>
/// Traces first-chance exceptions that look catastrophic (AccessViolation, SEH,
/// ExecutionEngineException, OutOfMemory). These often indicate native misuse
/// that would crash the process shortly after. Ordinary exceptions are NOT
/// traced here — they'd be too noisy.
/// </summary>
internal static class FirstChanceCrashTracer
{
    public static void Install(ILogger logger)
    {
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            var ex = e.Exception;
            if (ex is AccessViolationException
                || ex is OutOfMemoryException
                || ex is StackOverflowException)
            {
                try
                {
                    logger.Error($"[FATAL] First-chance {ex.GetType().Name} on thread {Environment.CurrentManagedThreadId}", ex);
                }
                catch { }
            }
        };
    }
}
