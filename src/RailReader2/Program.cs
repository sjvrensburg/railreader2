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
        AppConfig.Logger = logger;
        AnnotationService.Logger = logger;
        CleanupService.Logger = logger;
        PdfTextService.Logger = logger;
        PdfOutlineExtractor.Logger = logger;
        LayoutAnalyzer.Logger = logger;
        SkiaPdfService.Logger = logger;

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
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
