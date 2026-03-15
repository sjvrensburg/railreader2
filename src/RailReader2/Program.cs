using Avalonia;
using RailReader.Core.Services;

namespace RailReader2;

internal sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
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
