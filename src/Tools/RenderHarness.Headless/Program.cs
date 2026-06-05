using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using RailReader2;
using RailReader2.RenderHarness.Headless;
using RailReader2.ViewModels;
using RailReader2.Views;

// Headless documentation-screenshot harness. Boots the REAL RailReader2 UI under
// Avalonia.Headless with real Skia drawing, drives a known UI state per shot
// (page, zoom, theme, side pane, tool, colour effect, debug overlay, rail toggles,
// search, annotations), pumps until it settles, and captures the frame.
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

AppBuilder.Configure<App>()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .UseSkia()
    .SetupWithoutStarting();

void Pump(int ticks)
{
    for (int i = 0; i < ticks; i++)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(8);
    }
}

bool PumpUntil(Func<bool> done, int maxTicks)
{
    for (int i = 0; i < maxTicks; i++)
    {
        if (done()) return true;
        Pump(1);
    }
    return done();
}

void SetTheme(string theme) =>
    Application.Current!.RequestedThemeVariant =
        theme.Equals("light", StringComparison.OrdinalIgnoreCase) ? ThemeVariant.Light : ThemeVariant.Dark;

var appConfig = AppConfig.Load();
// A more pronounced line-focus dim reads better in documentation screenshots.
appConfig.LineFocusBlurIntensity = 0.8;

// The app scales its whole chrome by the window FontSize (ApplyFontScale sets
// Window.FontSize = 14 * UiFontScale). Driving that live lets us change UI scale
// per shot WITHOUT rebuilding the window — so the analysis cache and loaded ONNX
// model survive, which a rebuild would reset (and risk a PDFium teardown crash).
const double BaseFontSize = 14.0;
string? loadedPdf = null;

appConfig.UiFontScale = config.UiScale > 0 ? config.UiScale : 1.0f;
var vm = new MainWindowViewModel(appConfig);
var window = new MainWindow { DataContext = vm };
vm.SetWindow(window);
window.Width = config.Width;
window.Height = config.Height;
window.FontSize = BaseFontSize * appConfig.UiFontScale;
SetTheme(config.Theme);
window.Show();
Pump(20);

void SetUiScale(float scale)
{
    appConfig.UiFontScale = scale > 0 ? scale : (config.UiScale > 0 ? config.UiScale : 1.0f);
    window.FontSize = BaseFontSize * appConfig.UiFontScale;
}

// Position the rail's active line: vertically a fraction down from the viewport top,
// and horizontally left-aligned to the active block's column (so line starts are
// visible, rather than the page being centred and cut off both sides at high zoom).
// The camera maps page→screen as p*zoom + Offset.
void PositionRailLine(TabViewModel t, float frac)
{
    var vp = window.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "Viewport");
    double vh = vp?.Bounds.Height ?? (window.Height - 120);
    double vw = vp?.Bounds.Width ?? window.Width;
    double zoom = t.Camera.Zoom;
    double lineY = t.Rail.CurrentLineInfo.Y;
    double blockX = t.Rail.CurrentNavigableBlock?.BBox.X ?? 0;
    t.Camera.OffsetX = vw * 0.06 - blockX * zoom;   // column left ~6% from viewport left
    t.Camera.OffsetY = vh * frac - lineY * zoom;
    vm.RequestCameraUpdate();
    Pump(10);
    double got = (t.Rail.CurrentLineInfo.Y * t.Camera.Zoom + t.Camera.OffsetY) / vh * 100;
    Console.Error.WriteLine($"    [rail] active line at {got:F0}% of viewport (target {frac * 100:F0}%)");
}

// Centre the camera on the first search match on the current page (so its on-page
// highlight is actually in view, with surrounding context).
void CenterSearchMatch(TabViewModel t, float fracY)
{
    PumpUntil(() => { vm.RefreshCurrentPageSearchMatches(); return vm.CurrentPageSearchMatches is { Count: > 0 }; }, 80);
    vm.RefreshCurrentPageSearchMatches();
    var m = vm.CurrentPageSearchMatches?.FirstOrDefault(x => x.Rects is { Count: > 0 });
    if (m?.Rects is not { Count: > 0 } rects) return;
    var r = rects[0];
    var vp = window.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "Viewport");
    double vh = vp?.Bounds.Height ?? (window.Height - 120);
    double vw = vp?.Bounds.Width ?? window.Width;
    double zoom = t.Camera.Zoom;
    t.Camera.OffsetX = vw * 0.5 - r.MidX * zoom;
    t.Camera.OffsetY = vh * fracY - r.MidY * zoom;
    vm.RequestCameraUpdate();
    Pump(10);
}

int ok = 0, fail = 0;

foreach (var shot in config.Shots)
{
    if (only != null && !shot.Name.Equals(only, StringComparison.OrdinalIgnoreCase))
        continue;

    int? annotatedPage = null;
    (float Cx, float Cy)? annotCenter = null;
    TabViewModel? tab = null;
    try
    {
        SetUiScale(shot.UiScale);
        SetTheme(shot.Theme ?? config.Theme);
        window.Width = shot.Width > 0 ? shot.Width : config.Width;
        window.Height = shot.Height > 0 ? shot.Height : config.Height;
        Pump(10);

        var pdfPath = Path.IsPathRooted(shot.Pdf) ? shot.Pdf : Path.Combine(repoRoot, shot.Pdf);
        if (!File.Exists(pdfPath))
            throw new FileNotFoundException($"PDF not found: {pdfPath}");

        if (!string.Equals(loadedPdf, pdfPath, StringComparison.Ordinal))
        {
            var open = vm.OpenDocument(pdfPath);
            PumpUntil(() => open.IsCompleted, 1500);
            if (open.IsFaulted) throw open.Exception!;
            loadedPdf = pdfPath;
            Pump(20);
        }

        tab = vm.ActiveTab ?? throw new InvalidOperationException("No active tab after open.");
        int pageIdx = Math.Clamp(shot.Page - 1, 0, tab.PageCount - 1);
        vm.GoToPage(pageIdx);
        Pump(10);

        bool needsAnalysis = shot.RequireAnalysis || shot.DebugOverlay || shot.Annotate
            || shot.Sidebar == Pane.Figures || shot.Zoom >= (float)tab.Rail.ZoomThreshold;
        if (needsAnalysis)
        {
            vm.StartBackgroundAnalysis();
            if (!PumpUntil(() => tab.AnalysisCache.ContainsKey(tab.State.CurrentPage), 3500))
                Console.Error.WriteLine($"  Warning: '{shot.Name}' — analysis not ready for page {shot.Page}.");
        }

        // --- State mutations that change layer *content* (rebuilt by the GoToPage below) ---

        // Colour effect: the page layer reads Controller.ActiveColourEffect, which
        // SetColourEffect drives (State.ColourEffect is a different field).
        vm.SetColourEffect((ColourEffect)(int)shot.ColourEffect);

        tab.DebugOverlay = shot.DebugOverlay;
        vm.IsAnnotationMode = shot.AnnotationMode;

        if (shot.Annotate)
        {
            tab.AnalysisCache.TryGetValue(pageIdx, out var analysis);
            // Only inject when the page has no real user annotations, and remember to
            // clear them after capture so nothing is persisted to the user's store.
            if (!tab.State.Annotations.Pages.TryGetValue(pageIdx, out var existing) || existing.Count == 0)
            {
                annotCenter = AnnotationDemo.Inject(tab.State.Annotations, pageIdx, analysis, tab.State.PageWidth, tab.State.PageHeight);
                annotatedPage = pageIdx;
            }
        }

        // Rebuild every layer with the new page/overlay/annotation content.
        vm.GoToPage(pageIdx);
        Pump(8);

        // --- Side panel / search ---
        // For a search shot, just open the pane here; the query is run AFTER zoom so the
        // search's auto-navigation to the match isn't undone by ApplyZoom's re-centering.
        if (shot.Search is { Length: > 0 })
        {
            vm.ShowPane(SidePane.Search);
        }
        else if (shot.Sidebar == Pane.None)
        {
            vm.ShowOutline = false;
        }
        else
        {
            vm.ShowPane(shot.Sidebar switch
            {
                Pane.Outline => SidePane.Outline,
                Pane.Bookmarks => SidePane.Bookmarks,
                Pane.Figures => SidePane.Index,
                Pane.Search => SidePane.Search,
                _ => SidePane.Outline,
            });
        }
        Pump(6);

        // --- Annotation tool ---
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

        // --- Zoom ---
        // Set the exact factor via ApplyZoom (the keyboard zoom is multiplicative and
        // can't land on an arbitrary target like 5.75x). Rail mode then engages through
        // the tick's UpdateRailZoom when the factor is above threshold.
        var vpCtl = window.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "Viewport");
        double vpW = vpCtl?.Bounds.Width ?? window.Width;
        double vpH = vpCtl?.Bounds.Height ?? window.Height;
        if (shot.Zoom >= 0.05f)
        {
            tab.State.ApplyZoom(shot.Zoom, vpW, vpH);
            // Re-raster at the zoom-appropriate DPI — ApplyZoom alone leaves the page
            // upscaled from the previous (low) DPI tier, which looks blurry at high zoom.
            tab.State.UpdateRenderDpiIfNeeded();
            vm.RequestCameraUpdate();
            PumpUntil(() => tab.State.DpiRenderReady, 200);
        }
        else
        {
            vm.FitPage();
        }
        Pump(30);

        // --- Rail line toggles (deterministic: toggle only to reach the desired state) ---
        if (tab.LineHighlightEnabled != shot.LineHighlight) vm.ToggleLineHighlight();
        if (tab.LineFocusBlur != shot.LineFocusBlur) vm.ToggleLineFocusBlur();
        Pump(20);

        // Run the search now (after zoom) so its auto-navigation centres the first match
        // in the viewport — the query also becomes visible in the search box.
        if (shot.Search is { Length: > 0 } query)
        {
            var box = window.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(t => t.Name == "SearchInput");
            if (box is not null) box.Text = query;
            else vm.ExecuteSearch(query, false, false);
            Pump(40);
            vm.GoToMatch(0);   // navigate to the first match (builds the current-page match cache)
            Pump(16);
            CenterSearchMatch(tab, shot.RailLineFraction > 0 ? shot.RailLineFraction : 0.40f);
        }
        // Rail: reset to the first navigable line (so the advance is absolute and identical
        // across shots — the rail line otherwise carries over from the previous shot's tab),
        // then advance a fixed number of lines onto the target body line, and position it.
        else if (shot.RailLineFraction > 0 && tab.Rail.Active)
        {
            // Walk up to the first navigable line of THIS page. Rail navigation crosses page
            // boundaries, so go up until we'd leave the page, then step back onto its top line.
            for (int i = 0; i < 80; i++)
            {
                vm.HandleArrowUp(); Pump(3);
                if (tab.State.CurrentPage < pageIdx) { vm.HandleArrowDown(); Pump(4); break; }
            }
            for (int i = 0; i < shot.RailAdvanceLines; i++) { vm.HandleArrowDown(); Pump(6); }
            PositionRailLine(tab, shot.RailLineFraction);
        }
        // Annotations: frame the camera on the injected annotations so they're all in view
        // (ApplyZoom centres the page, which leaves the title underline/scribble off-screen).
        else if (annotCenter is { } ac)
        {
            var vp = window.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "Viewport");
            double vh = vp?.Bounds.Height ?? window.Height;
            double vw = vp?.Bounds.Width ?? window.Width;
            double z = tab.Camera.Zoom;
            tab.Camera.OffsetX = vw * 0.5 - ac.Cx * z;
            tab.Camera.OffsetY = vh * 0.45 - ac.Cy * z;
            vm.RequestCameraUpdate();
            Pump(10);
        }

        Pump(12);

        var frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("CaptureRenderedFrame returned null.");
        var outPath = Path.Combine(outDir, shot.Name + ".png");
        using (var fs = File.Create(outPath))
            frame.Save(fs);

        ok++;
        Console.Error.WriteLine(
            $"  {shot.Name} -> {outPath}  ({(shot.Theme ?? config.Theme)}, zoom {tab.Camera.Zoom * 100:F0}%)");

        // Drain any in-flight async DPI raster before the next shot — a background
        // raster still touching PDFium while the next shot navigates/re-rasters causes
        // intermittent native crashes (PDFium is not safe for concurrent access).
        PumpUntil(() => tab.State.DpiRenderReady, 150);
        Pump(10);
    }
    catch (Exception ex)
    {
        fail++;
        Console.Error.WriteLine($"  Error on '{shot.Name}': {ex.Message}");
    }
    finally
    {
        // Never let injected demo annotations leak into the user's store or later shots.
        if (annotatedPage is { } ap && tab is not null && tab.State.Annotations.Pages.TryGetValue(ap, out var l))
            l.Clear();
    }
}

Console.Error.WriteLine($"Done: {ok} screenshot(s) written to {outDir}" + (fail > 0 ? $" ({fail} failed)" : ""));
return fail > 0 ? 1 : 0;
