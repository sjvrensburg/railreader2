using Avalonia;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using RailReader2;
using RailReader2.RenderHarness.Headless;
using RailReader2.ViewModels;
using RailReader2.Views;

// Headless documentation-screenshot harness. Boots the REAL RailReader2 UI
// (menu bar, tab strip, accordion side panel, status bar, and the
// composition-thread PDF page + overlay layers) under Avalonia.Headless with
// real Skia drawing, drives a known UI state per shot, and captures the frame.
//
//   dotnet run --project src/Tools/RenderHarness.Headless -- [config.json] [--only <name>]

static string ResolveRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RailReader2.slnx")))
        dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}

string? GetOption(string name)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == $"--{name}") return args[i + 1];
    return null;
}

var repoRoot = ResolveRepoRoot();
var only = GetOption("only");
var configPath = args.FirstOrDefault(a => a.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
    ?? Path.Combine(AppContext.BaseDirectory, "screenshots.json");

if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"Error: config not found: {configPath}");
    return 1;
}

var config = HeadlessConfig.Load(configPath);
var outDir = Path.IsPathRooted(config.OutputDir) ? config.OutputDir : Path.Combine(repoRoot, config.OutputDir);
Directory.CreateDirectory(outDir);

PdfiumResolver.Initialize();
RailReaderLogging.Logger = new ConsoleLogger();

// Boot the real App (Fluent theme, Inter fonts, app styles) on a headless
// platform with real Skia drawing. No desktop lifetime → App skips the splash
// and async init; we own the window.
AppBuilder.Configure<App>()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .UseSkia()
    .SetupWithoutStarting();

Application.Current!.RequestedThemeVariant =
    config.Theme.Equals("light", StringComparison.OrdinalIgnoreCase) ? ThemeVariant.Light : ThemeVariant.Dark;

void Pump(int ticks)
{
    for (int i = 0; i < ticks; i++)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(8);
    }
}

// Pump until a condition holds or the timeout (in pump-ticks) elapses.
bool PumpUntil(Func<bool> done, int maxTicks)
{
    for (int i = 0; i < maxTicks; i++)
    {
        if (done()) return true;
        Pump(1);
    }
    return done();
}

var appConfig = AppConfig.Load();
var vm = new MainWindowViewModel(appConfig);
var window = new MainWindow { DataContext = vm };
vm.SetWindow(window);
window.Width = config.Width;
window.Height = config.Height;
window.Show();
Pump(20);

string? loadedPdf = null;
int ok = 0, fail = 0;

foreach (var shot in config.Shots)
{
    if (only != null && !shot.Name.Equals(only, StringComparison.OrdinalIgnoreCase))
        continue;

    try
    {
        var pdfPath = Path.IsPathRooted(shot.Pdf) ? shot.Pdf : Path.Combine(repoRoot, shot.Pdf);
        if (!File.Exists(pdfPath))
            throw new FileNotFoundException($"PDF not found: {pdfPath}");

        // Load the document if it isn't already open.
        if (!string.Equals(loadedPdf, pdfPath, StringComparison.Ordinal))
        {
            var open = vm.OpenDocument(pdfPath);
            PumpUntil(() => open.IsCompleted, 1500);
            if (open.IsFaulted) throw open.Exception!;
            loadedPdf = pdfPath;
            Pump(20);
        }

        var tab = vm.ActiveTab ?? throw new InvalidOperationException("No active tab after open.");
        int pageIdx = Math.Clamp(shot.Page - 1, 0, tab.PageCount - 1);
        vm.GoToPage(pageIdx);
        Pump(10);

        // Wait for layout analysis when the requested state needs it.
        if (shot.RequireAnalysis || shot.Sidebar == Pane.Figures || shot.Zoom >= (float)tab.Rail.ZoomThreshold)
        {
            vm.StartBackgroundAnalysis();
            bool got = PumpUntil(() => tab.AnalysisCache.ContainsKey(tab.State.CurrentPage), 800);
            if (!got)
                Console.Error.WriteLine($"  Warning: '{shot.Name}' — analysis not ready for page {shot.Page}.");
        }

        // Side panel.
        if (shot.Sidebar == Pane.None)
            vm.ShowOutline = false;
        else
            vm.ShowPane(shot.Sidebar switch
            {
                Pane.Outline => SidePane.Outline,
                Pane.Bookmarks => SidePane.Bookmarks,
                Pane.Figures => SidePane.Index,
                Pane.Search => SidePane.Search,
                _ => SidePane.Outline,
            });
        Pump(8);

        // Annotation tool.
        vm.SetAnnotationTool(shot.Tool switch
        {
            Tool.Highlight => AnnotationTool.Highlight,
            Tool.Pen => AnnotationTool.Pen,
            Tool.Rectangle => AnnotationTool.Rectangle,
            Tool.TextNote => AnnotationTool.TextNote,
            Tool.Eraser => AnnotationTool.Eraser,
            _ => AnnotationTool.None,
        });
        Pump(4);

        // Zoom: drive toward the target via the same keyboard path the app uses,
        // so rail mode engages through the real controller transition. 0 = fit page.
        if (shot.Zoom >= 0.05f)
        {
            float target = shot.Zoom;
            for (int i = 0; i < 240 && Math.Abs(tab.Camera.Zoom - target) > 0.04 * target; i++)
            {
                vm.HandleZoomKey(tab.Camera.Zoom < target);
                Pump(2);
            }
        }
        else
        {
            vm.FitPage();
        }

        // Let everything settle (zoom animation, rail snap, layer paint).
        Pump(60);

        var frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("CaptureRenderedFrame returned null.");
        var outPath = Path.Combine(outDir, shot.Name + ".png");
        using (var fs = File.Create(outPath))
            frame.Save(fs);

        ok++;
        Console.Error.WriteLine($"  {shot.Name} -> {outPath}  (zoom {tab.Camera.Zoom * 100:F0}%)");
    }
    catch (Exception ex)
    {
        fail++;
        Console.Error.WriteLine($"  Error on '{shot.Name}': {ex.Message}");
    }
}

Console.Error.WriteLine($"Done: {ok} screenshot(s) written to {outDir}" + (fail > 0 ? $" ({fail} failed)" : ""));
return fail > 0 ? 1 : 0;
