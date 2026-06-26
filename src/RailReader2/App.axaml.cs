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
        // The accessibility/automation backends surface this as the application's name on the bus
        // (AT-SPI on Linux); without it the app registers as the generic "Avalonia Application", which an
        // agent/screen reader can't identify. The window frame is named separately (via the window title).
        Name = "RailReader2";
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
                bool configDirty = false;
                if (seedRenderQuality)
                {
                    config.RenderQuality = DefaultRenderQuality;
                    configDirty = true;
                }
                if (configDirty) config.Save();
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
                {
                    // Optional known-state startup flags (for agents / scripted launches):
                    //   --page <n> (1-based)   --zoom <percent, e.g. 300>   --rail
                    var (startPage, startZoom, startRail) = ParseStartupFlags(args!);
                    window.Opened += (_, _) => vm.FireAndForget(OpenStartupDocument(), nameof(vm.OpenDocument));

                    async System.Threading.Tasks.Task OpenStartupDocument()
                    {
                        await vm.OpenDocument(docPath);
                        if (startPage is not null || startZoom is not null || startRail)
                            vm.ApplyStartupView(startPage, startZoom, startRail);
                    }
                }

                window.Show();
            }, DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Parse the optional known-state startup flags: <c>--page &lt;n&gt;</c> (1-based),
    /// <c>--zoom &lt;percent&gt;</c> (e.g. 300 for 300%), and <c>--rail</c>. Unknown/malformed values are
    /// ignored; the PDF path is a separate positional argument.</summary>
    private static (int? Page, double? Zoom, bool Rail) ParseStartupFlags(string[] args)
    {
        int? page = null;
        double? zoom = null;
        bool rail = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--page" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p):
                    page = p; i++; break;
                case "--zoom" when i + 1 < args.Length && double.TryParse(
                        args[i + 1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var z):
                    zoom = z; i++; break;
                case "--rail":
                    rail = true; break;
            }
        }
        return (page, zoom, rail);
    }
}
