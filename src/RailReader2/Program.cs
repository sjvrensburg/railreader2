using System.Runtime.InteropServices;
using Avalonia;
using RailReader.Core;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;

namespace RailReader2;

internal sealed class Program
{
    // .NET 10 no longer provides a default SIGTERM handler — the OS default
    // (immediate kill) applies. On GNOME/X11, session management can send
    // SIGTERM during normal window lifecycle events. Ignore it so the app
    // shuts down through Avalonia's own lifecycle instead.
    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr signal(int signum, IntPtr handler);

    [STAThread]
    public static void Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
            signal(15 /* SIGTERM */, new IntPtr(1) /* SIG_IGN */);

        PdfiumResolver.Initialize();

        var logger = new ConsoleLogger();
        RailReaderLogging.Logger = logger;

        // Log unhandled exceptions so crash info survives in session.log
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            logger.Error($"[FATAL] Unhandled exception (terminating={e.IsTerminating})",
                e.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.Error("[WARN] Unobserved task exception", e.Exception);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            logger.Info("[EXIT] ProcessExit fired (clean shutdown path)");
        };
        FirstChanceCrashTracer.Install(logger);
        if (!OperatingSystem.IsWindows())
            NativeSignalTrap.Install(logger);

        try
        {
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            logger.Error("[FATAL] Top-level exception", ex);
            throw;
        }
        finally
        {
            logger.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect();

        // X11 CPU busy-loop mitigation (Linux/X11 only). Avalonia's native X11 dispatcher
        // (X11PlatformThreading) does not implement IDispatcherImplWithExplicitBackgroundProcessing,
        // so the base Dispatcher falls back to arming a "now + 1ms" OS timer whenever it has a
        // low-priority job it can't drain immediately. That collapses the X11 RunLoop's epoll
        // timeout to ~0, so it never sleeps: recvmsg() on the X server socket spins at ~30k/s
        // returning EAGAIN and pins one core at 100%. It's intermittent (a stuck pending-input /
        // never-draining-queue race), so it survives in long sessions once entered.
        //
        // The GLib (GMainLoop) dispatcher DOES implement explicit background processing (it uses a
        // GLib idle source instead of the +1ms timer), so it structurally cannot enter that spin.
        // We therefore prefer it on Linux, with two safeguards:
        //   * Guarded: only enabled when libglib-2.0.so.0 is actually loadable. Avalonia hard-
        //     DllImports glib with no fallback, so enabling it on a glib-less system would throw
        //     DllNotFoundException at startup. The probe keeps us on the epoll dispatcher there.
        //   * Opt-out: RR_X11_GLIB=0|false|off|no forces the native epoll dispatcher.
        //     RR_X11_GLIB=1|true|on|yes forces GLib even past the probe (debugging only).
        if (OperatingSystem.IsLinux() && ShouldUseGLibMainLoop())
            builder = builder.With(new X11PlatformOptions { UseGLibMainLoop = true });

        return builder
            .WithInterFont()
            .LogToTrace();
    }

    private static bool ShouldUseGLibMainLoop()
    {
        switch (ParseBool(Environment.GetEnvironmentVariable("RR_X11_GLIB")))
        {
            case false: return false;   // explicit opt-out -> native epoll dispatcher
            case true: return true;     // explicit opt-in  -> GLib even if the probe would fail
            default:
                // Default: prefer GLib, but only if it's actually loadable so we never crash on a
                // system without glib (Avalonia has no internal fallback to epoll).
                return NativeLibrary.TryLoad("libglib-2.0.so.0", out _);
        }
    }

    private static bool? ParseBool(string? value) => value switch
    {
        "1" or "true" or "TRUE" or "yes" or "on" => true,
        "0" or "false" or "FALSE" or "no" or "off" => false,
        _ => null,
    };
}
