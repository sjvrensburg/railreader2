using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using RailReader.Core;
using RailReader.Core.Services;
using RailReader2.ControlBus;
using RailReader2.ViewModels;
using RailReader2.Views;

namespace RailReader2;

public partial class App : Application
{
    // Held for lifetime + disposal when the optional --control-bus server is running.
    private ViewModelControl? _control;
    private DBusControlServer? _controlServer;

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
                window.Closing += (_, _) =>
                {
                    _controlServer?.Dispose();
                    _control?.Dispose();
                    vm.Dispose();
                };
                desktop.MainWindow = window;

                // First non-flag argument that names an existing file is the PDF to open.
                var docPath = args?.FirstOrDefault(a => !a.StartsWith("--") && File.Exists(a));
                if (docPath is not null)
                    window.Opened += (_, _) => vm.FireAndForget(vm.OpenDocument(docPath), nameof(vm.OpenDocument));

                // Optional external control surface: --control-bus or --control-bus=<bus-name>.
                if (TryGetControlBusName(args, out var busName))
                    window.Opened += (_, _) => StartControlServer(vm, busName);

                window.Show();
            }, DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Parse <c>--control-bus</c> / <c>--control-bus=&lt;name&gt;</c> from the command line.
    /// Returns true when the flag is present; <paramref name="busName"/> is the explicit name or null
    /// (meaning the default <see cref="DBusControlServer.DefaultBusName"/>).</summary>
    private static bool TryGetControlBusName(string[]? args, out string? busName)
    {
        busName = null;
        if (args is null) return false;
        foreach (var arg in args)
        {
            if (arg == "--control-bus") return true;
            if (arg.StartsWith("--control-bus=", StringComparison.Ordinal))
            {
                var name = arg["--control-bus=".Length..];
                busName = string.IsNullOrWhiteSpace(name) ? null : name;
                return true;
            }
        }
        return false;
    }

    private void StartControlServer(MainWindowViewModel vm, string? busName)
    {
        var logger = RailReaderLogging.Logger;
        try
        {
            _control = new ViewModelControl(vm);
            _controlServer = new DBusControlServer(_control, busName, logger);
            vm.FireAndForget(_controlServer.StartAsync(), nameof(DBusControlServer));
        }
        catch (Exception ex)
        {
            logger.Error("[control-bus] Failed to start control server", ex);
        }
    }
}
