using System.Runtime.InteropServices;
using Avalonia;
using RailReader.Core.Services;

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
        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
