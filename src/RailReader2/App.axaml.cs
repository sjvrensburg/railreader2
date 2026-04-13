using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using RailReader.Core.Services;
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
            var splash = new SplashWindow();
            desktop.MainWindow = splash;
            splash.Show();

            // Defer heavy initialization so the event loop can pump and
            // the splash window actually renders before we block the UI
            // thread with config loading, cleanup, and ONNX model init.
            var args = desktop.Args;
            Dispatcher.UIThread.Post(async () =>
            {
                // Yield once to let the splash paint
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                var config = AppConfig.Load();
                Application.Current!.RequestedThemeVariant =
                    config.DarkMode ? ThemeVariant.Dark : ThemeVariant.Light;
                CleanupService.RunCleanup();

                var vm = new MainWindowViewModel(config);
                var window = new MainWindow { DataContext = vm };
                vm.SetWindow(window);

                window.Opened += (_, _) => splash.Close();
                window.Closing += (_, _) => vm.Dispose();
                desktop.MainWindow = window;

                if (args is { Length: > 0 } && File.Exists(args[0]))
                    window.Opened += (_, _) => vm.FireAndForget(vm.OpenDocument(args[0]), nameof(vm.OpenDocument));

                window.Show();
            }, DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
