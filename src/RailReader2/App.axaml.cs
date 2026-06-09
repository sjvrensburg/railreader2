using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader2.ViewModels;
using RailReader2.Views;

namespace RailReader2;

public partial class App : Application
{
    /// <summary>The desktop ships <see cref="RenderQuality.High"/> as its baseline, overriding Core's
    /// <see cref="RenderQuality.Quality"/> default. Kept in sync with the Reset-to-Defaults path in
    /// <see cref="Views.SettingsWindow"/>.</summary>
    internal const RenderQuality DefaultRenderQuality = RenderQuality.High;

    /// <summary>True only on a genuine first run — when no config file exists yet. On that first run
    /// the desktop seeds <see cref="DefaultRenderQuality"/> so the user gets High rather than Core's
    /// Quality. Any existing config is left untouched (pre-0.24.0 configs simply fall through to Core's
    /// Quality), so an explicit selection is never clobbered. Keyed on file existence rather than the
    /// serialized field, so it can't silently re-seed if Core ever renames the persisted property.</summary>
    private static bool IsFirstRun()
    {
        try { return !System.IO.File.Exists(AppConfig.ConfigPath); }
        catch { return false; } // unreadable path → treat as existing, never re-seed over a possible config
    }

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

                // Decide BEFORE Load (which may persist a fresh default file), so we only seed
                // the desktop render-quality default on a true first run (no config yet).
                bool seedRenderQuality = IsFirstRun();
                var config = AppConfig.Load();
                if (seedRenderQuality)
                {
                    config.RenderQuality = DefaultRenderQuality;
                    config.Save();
                }
                Application.Current!.RequestedThemeVariant =
                    config.DarkMode ? ThemeVariant.Dark : ThemeVariant.Light;
                CleanupService.RunCleanup();

                var vm = new MainWindowViewModel(config);
                var window = new MainWindow { DataContext = vm };
                vm.SetWindow(window);

                window.Opened += (_, _) => splash.Close();
                window.Closing += (_, _) => vm.Dispose();
                desktop.MainWindow = window;

                // First non-flag argument that names an existing file is the PDF to open.
                var docPath = args?.FirstOrDefault(a => !a.StartsWith("--") && File.Exists(a));
                if (docPath is not null)
                    window.Opened += (_, _) => vm.FireAndForget(vm.OpenDocument(docPath), nameof(vm.OpenDocument));

                window.Show();
            }, DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
