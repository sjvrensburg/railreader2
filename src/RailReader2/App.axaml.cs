using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RailReader2.Services;
using RailReader2.ViewModels;
using RailReader2.Views;

namespace RailReader2;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Show splash screen while heavy initialization runs
            var splash = new SplashWindow();
            desktop.MainWindow = splash;
            splash.Show();

            var config = AppConfig.Load();
            CleanupService.RunCleanup();

            var vm = new MainWindowViewModel(config);
            var window = new MainWindow { DataContext = vm };
            vm.SetWindow(window);

            // Swap main window in once it's ready
            window.Opened += (_, _) =>
            {
                splash.Close();
            };
            desktop.MainWindow = window;

            // Open PDF from command line argument (deferred until window is shown)
            if (desktop.Args is { Length: > 0 } && File.Exists(desktop.Args[0]))
            {
                var pdfPath = desktop.Args[0];
                window.Opened += (_, _) => vm.OpenDocument(pdfPath);
            }

            window.Show();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
