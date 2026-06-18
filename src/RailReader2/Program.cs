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

        // Linux/X11 dispatcher selection. Avalonia's native X11 dispatcher (X11PlatformThreading)
        // does not implement IDispatcherImplWithExplicitBackgroundProcessing, so when a low-priority
        // dispatcher job stays perpetually queued it arms a "now + 1ms" OS timer that collapses the
        // epoll timeout to ~0 and pins one core at 100% (recvmsg/EAGAIN storm). We originally
        // switched to the GLib (GMainLoop) dispatcher to dodge that.
        //
        // But the real trigger turned out to be indeterminate ProgressBars left attached to the
        // visual tree (their infinite animation kept Avalonia's animation clock — and thus a
        // low-priority job — armed forever); that's fixed in the views. The native dispatcher no
        // longer spins AND paces animation (zoom / rail horizontal-scroll) noticeably more smoothly
        // than the GLib loop, so it's the default again. GLib stays as an opt-in fallback
        // (RR_X11_GLIB=1|true|on|yes), probe-guarded so a stray opt-in on a glib-less system can't
        // throw DllNotFoundException at startup (Avalonia hard-DllImports glib with no fallback).
        if (OperatingSystem.IsLinux() && ShouldUseGLibMainLoop())
            builder = builder.With(new X11PlatformOptions { UseGLibMainLoop = true });

        return builder
            .WithInterFont()
            .LogToTrace();
    }

    // GLib main loop is opt-in (see BuildAvaloniaApp); the native epoll dispatcher is the default.
    private static bool ShouldUseGLibMainLoop()
    {
        // Only when explicitly requested via RR_X11_GLIB=1|true|on|yes, and only if libglib is
        // actually loadable (Avalonia hard-DllImports it with no fallback).
        if (ParseBool(Environment.GetEnvironmentVariable("RR_X11_GLIB")) != true)
            return false;
        return NativeLibrary.TryLoad("libglib-2.0.so.0", out _);
    }

    private static bool? ParseBool(string? value) => value switch
    {
        "1" or "true" or "TRUE" or "yes" or "on" => true,
        "0" or "false" or "FALSE" or "no" or "off" => false,
        _ => null,
    };
}
