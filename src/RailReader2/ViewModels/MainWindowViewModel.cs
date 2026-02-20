using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RailReader2.Models;
using RailReader2.Services;

namespace RailReader2.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private const double ZoomStep = 1.25;
    private const double ZoomScrollSensitivity = 0.003;
    private const double PanStep = 50.0;

    private AppConfig _config;
    private AnalysisWorker _worker = null!; // initialized in InitializeWorker, called from ctor
    private ColourEffectShaders _colourEffects = new();
    private Window? _window;
    private DispatcherTimer? _pollTimer;
    private Stopwatch _frameTimer = Stopwatch.StartNew();
    private Action? _invalidateCanvas; // legacy fallback
    private InvalidationCallbacks? _invalidation;
    private bool _animationRequested;

    [ObservableProperty] private int _activeTabIndex;
    [ObservableProperty] private bool _showOutline;
    [ObservableProperty] private bool _showMinimap;
    [ObservableProperty] private bool _showSettings;
    [ObservableProperty] private bool _showAbout;
    [ObservableProperty] private bool _showShortcuts;
    [ObservableProperty] private string? _cleanupMessage;

    public ObservableCollection<TabViewModel> Tabs { get; } = [];
    public AppConfig Config => _config;
    public ColourEffectShaders ColourEffects => _colourEffects;

    public TabViewModel? ActiveTab =>
        ActiveTabIndex >= 0 && ActiveTabIndex < Tabs.Count ? Tabs[ActiveTabIndex] : null;

    public MainWindowViewModel(AppConfig config)
    {
        _config = config;
        _colourEffects.Effect = config.ColourEffect;
        _colourEffects.Intensity = (float)config.ColourEffectIntensity;

        InitializeWorker();
        SetupPollTimer();
    }

    public void SetWindow(Window window)
    {
        _window = window;
        ApplyFontScale();
    }
    public void SetInvalidateCanvas(Action invalidate) => _invalidateCanvas = invalidate;
    public void SetInvalidation(InvalidationCallbacks callbacks) => _invalidation = callbacks;

    private void InitializeWorker()
    {
        var modelPath = FindModelPath();
        if (modelPath is null)
        {
            const string filename = "PP-DocLayoutV3.onnx";
            var searched = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "models", filename),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "railreader2", "models", filename),
                Path.GetFullPath(Path.Combine("models", filename)),
            };
            var msg = $"ONNX model not found ({filename}). Run ./scripts/download-model.sh\nSearched:\n"
                      + string.Join("\n", searched.Select(p => $"  - {p}"));
            throw new FileNotFoundException(msg);
        }

        Console.Error.WriteLine($"[ONNX] Starting worker with model: {modelPath}");
        _worker = new AnalysisWorker(modelPath);
        Console.Error.WriteLine("[ONNX] Worker started (ONNX session loading in background)");
    }

    private void SetupPollTimer()
    {
        // Low-frequency timer just for polling analysis results when not animating
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _pollTimer.Tick += (_, _) =>
        {
            // Animation frame handles polling when active — avoid mid-frame updates
            if (_animationRequested) return;

            bool gotResults = PollAnalysisResults();
            var tab = ActiveTab;
            if (tab is not null && !_animationRequested)
                tab.SubmitPendingLookahead(_worker);
            if (gotResults)
                InvalidateOverlay();
            bool workerBusy = !_worker.IsIdle;
            if (!workerBusy) _pollTimer?.Stop();
        };
    }

    /// <summary>
    /// Animation callback driven by the compositor's vsync via RequestAnimationFrame.
    /// Fires exactly once per display refresh — no timer jitter.
    /// </summary>
    private void OnAnimationFrame(TimeSpan _)
    {
        _animationRequested = false;

        double dt = _frameTimer.Elapsed.TotalSeconds;
        dt = Math.Min(dt, 0.05); // cap at 50ms to avoid large jumps

        var tab = ActiveTab;
        if (tab is null) return;

        var (ww, wh) = GetWindowSize();
        double cx = tab.Camera.OffsetX, cy = tab.Camera.OffsetY;
        bool animating = tab.Rail.Tick(ref cx, ref cy, dt, tab.Camera.Zoom, ww);
        tab.Camera.OffsetX = cx;
        tab.Camera.OffsetY = cy;

        // Decay zoom blur speed
        bool wasZooming = tab.Camera.ZoomSpeed > 0;
        tab.Camera.DecayZoomSpeed(dt);
        if (wasZooming) animating = true;

        // Poll analysis results while we're here
        bool gotResults = PollAnalysisResults();

        if (!animating)
            tab.SubmitPendingLookahead(_worker);

        // Batch DPI bitmap swap with this frame's camera update
        if (tab.DpiRenderReady)
        {
            tab.DpiRenderReady = false;
            InvalidatePage();
        }

        if (gotResults)
            InvalidateOverlay();

        if (animating)
        {
            InvalidateCamera();
            RequestAnimationFrame();
        }

        _frameTimer.Restart();
    }

    private bool PollAnalysisResults()
    {
        bool got = false;
        var (ww, wh) = GetWindowSize();
        while (_worker.Poll() is { } result)
        {
            got = true;
            Console.Error.WriteLine($"[Analysis] Got result for {Path.GetFileName(result.FilePath)} page {result.Page}: {result.Analysis.Blocks.Count} blocks");
            foreach (var tab in Tabs)
            {
                // Only apply results to the tab that owns this file
                if (tab.FilePath != result.FilePath) continue;

                tab.AnalysisCache[result.Page] = result.Analysis;
                if (tab.CurrentPage == result.Page && tab.PendingRailSetup)
                {
                    tab.Rail.SetAnalysis(result.Analysis, _config.NavigableClasses);
                    tab.PendingRailSetup = false;
                    Console.Error.WriteLine($"[Analysis] Rail has {tab.Rail.NavigableCount} navigable blocks, Active={tab.Rail.Active}");
                    if (tab.Rail.Active)
                        tab.StartSnap(ww, wh);
                }
            }
        }
        return got;
    }

    public void RequestAnimationFrame()
    {
        // Also ensure poll timer runs for analysis result polling
        if (_pollTimer is not null && !_pollTimer.IsEnabled)
            _pollTimer.Start();

        if (_animationRequested) return;
        _animationRequested = true;
        _frameTimer.Restart(); // reset so first frame gets a clean dt
        _window?.RequestAnimationFrame(OnAnimationFrame);
    }

    public void InvalidateCanvas()
    {
        InvalidateAll();
        OnPropertyChanged(nameof(ActiveTab));
    }

    public async void OpenDocument(string path)
    {
        try
        {
            Console.Error.WriteLine($"[OpenDocument] Opening: {path}");

            // Do heavy PDF + bitmap rendering off the UI thread
            TabViewModel? tab = null;
            await Task.Run(() =>
            {
                tab = new TabViewModel(path, _config);
                // LoadPageBitmap only renders the bitmap, does NOT submit analysis
                tab.LoadPageBitmap();
            });

            if (tab is null) return;

            Console.Error.WriteLine($"[OpenDocument] Loaded: {tab.PageCount} pages, {tab.PageWidth}x{tab.PageHeight}");

            var (ww, wh) = GetWindowSize();
            tab.CenterPage(ww, wh);
            tab.UpdateRailZoom(ww, wh);

            Tabs.Add(tab);
            ActiveTabIndex = Tabs.Count - 1;
            OnPropertyChanged(nameof(ActiveTab));
            InvalidateAll();

            // PageLayer.Bounds is still zero until Avalonia's layout pass propagates
            // PagePanel.Width/Height. DispatcherPriority.Background (4) is lower than
            // Layout (7) and Render (8), so this fires after both passes complete.
            Dispatcher.UIThread.Post(() => InvalidatePage(), DispatcherPriority.Background);

            // Submit analysis on UI thread (accesses worker's non-thread-safe state)
            tab.SubmitAnalysis(_worker);
            tab.QueueLookahead(_config.AnalysisLookaheadPages);
            RequestAnimationFrame();

            Console.Error.WriteLine("[OpenDocument] Tab added successfully");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to open {path}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    [RelayCommand]
    public async Task OpenFile()
    {
        if (_window is null) return;
        var files = await _window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open PDF",
            FileTypeFilter = [new FilePickerFileType("PDF Files") { Patterns = ["*.pdf"] }],
            AllowMultiple = false,
        });
        if (files is { Count: > 0 })
        {
            var path = files[0].TryGetLocalPath()
                       ?? files[0].Path.LocalPath;
            Console.Error.WriteLine($"[OpenFile] Selected: {path}");
            if (path is not null) OpenDocument(path);
        }
    }

    [RelayCommand]
    public void CloseTab(int index)
    {
        if (index < 0 || index >= Tabs.Count) return;
        var tab = Tabs[index];
        Tabs.RemoveAt(index);
        tab.Dispose();
        if (Tabs.Count == 0) ActiveTabIndex = 0;
        else if (ActiveTabIndex >= Tabs.Count) ActiveTabIndex = Tabs.Count - 1;
        OnPropertyChanged(nameof(ActiveTab));
        InvalidateAll();
    }

    [RelayCommand]
    public void SelectTab(int index)
    {
        if (index >= 0 && index < Tabs.Count)
        {
            ActiveTabIndex = index;
            OnPropertyChanged(nameof(ActiveTab));
            InvalidateAll();
        }
    }

    [RelayCommand]
    public void DuplicateTab()
    {
        if (ActiveTab is { } tab)
            OpenDocument(tab.FilePath);
    }

    [RelayCommand]
    public void GoToPage(int page)
    {
        if (ActiveTab is not { } tab) return;
        var (ww, wh) = GetWindowSize();
        tab.GoToPage(page, _worker, ww, wh);
        tab.QueueLookahead(_config.AnalysisLookaheadPages);
        OnPropertyChanged(nameof(ActiveTab));
        InvalidateAll();
        RequestAnimationFrame();
    }

    [RelayCommand]
    public void FitPage()
    {
        if (ActiveTab is not { } tab) return;
        var (ww, wh) = GetWindowSize();
        tab.CenterPage(ww, wh);
        tab.UpdateRailZoom(ww, wh);
        OnPropertyChanged(nameof(ActiveTab));
        InvalidateCamera();
    }

    [RelayCommand]
    public void SetColourEffect(ColourEffect effect)
    {
        _config.ColourEffect = effect;
        _config.Save();
        _colourEffects.Effect = effect;
        InvalidatePage();
        InvalidateOverlay();
        OnPropertyChanged(nameof(ActiveTab));
    }

    [RelayCommand]
    public void RunCleanup()
    {
        var (removed, freed) = CleanupService.RunCleanup();
        CleanupMessage = CleanupService.FormatReport(removed, freed);
    }

    public void OnConfigChanged()
    {
        _colourEffects.Effect = _config.ColourEffect;
        _colourEffects.Intensity = (float)_config.ColourEffectIntensity;
        foreach (var tab in Tabs)
        {
            tab.Rail.UpdateConfig(_config);
            tab.ReapplyNavigableClasses();
        }
        ApplyFontScale();
        _config.Save();
        InvalidateAll();
        OnPropertyChanged(nameof(ActiveTab));
    }

    private const double BaseFontSize = 14.0;

    private void ApplyFontScale()
    {
        if (_window is not null)
            _window.FontSize = BaseFontSize * _config.UiFontScale;
    }

    /// <summary>Returns the current font size for use when creating child windows.</summary>
    public double CurrentFontSize => BaseFontSize * _config.UiFontScale;

    public void HandleZoom(double scrollDelta, double cursorX, double cursorY, bool ctrlHeld)
    {
        if (ActiveTab is not { } tab) return;
        var (ww, wh) = GetWindowSize();

        if (ctrlHeld && tab.Rail.Active)
        {
            double step = scrollDelta * 2.0 * tab.Camera.Zoom;
            tab.Camera.OffsetX += step;
            tab.ClampCamera(ww, wh);
        }
        else
        {
            double oldZoom = tab.Camera.Zoom;
            double factor = 1.0 + scrollDelta * ZoomScrollSensitivity;
            double newZoom = Math.Clamp(oldZoom * factor, Camera.ZoomMin, Camera.ZoomMax);

            tab.Camera.OffsetX = cursorX - (cursorX - tab.Camera.OffsetX) * (newZoom / oldZoom);
            tab.Camera.OffsetY = cursorY - (cursorY - tab.Camera.OffsetY) * (newZoom / oldZoom);
            tab.Camera.Zoom = newZoom;
            tab.Camera.NotifyZoomChange();

            tab.UpdateRailZoom(ww, wh);
            if (tab.Rail.Active)
                tab.StartSnap(ww, wh);
            tab.ClampCamera(ww, wh);

            // Re-render at higher DPI if zoomed in significantly
            tab.UpdateRenderDpiIfNeeded();
        }
        InvalidateCamera();
        OnPropertyChanged(nameof(ActiveTab));
        RequestAnimationFrame();
    }

    public void HandlePan(double dx, double dy)
    {
        if (ActiveTab is not { } tab) return;
        var (ww, wh) = GetWindowSize();
        tab.Camera.OffsetX += dx;
        tab.Camera.OffsetY += dy;
        tab.ClampCamera(ww, wh);
        InvalidateCamera();
        OnPropertyChanged(nameof(ActiveTab));
    }

    public void HandleArrowDown()
    {
        if (ActiveTab is not { } tab) return;
        var (ww, wh) = GetWindowSize();

        if (tab.Rail.Active)
        {
            int currentPage = tab.CurrentPage;
            switch (tab.Rail.NextLine())
            {
                case NavResult.PageBoundaryNext:
                    tab.GoToPage(currentPage + 1, _worker, ww, wh);
                    tab.QueueLookahead(_config.AnalysisLookaheadPages);
                    if (tab.Rail.Active) tab.StartSnap(ww, wh);
                    break;
                case NavResult.Ok:
                    tab.StartSnap(ww, wh);
                    break;
            }
        }
        else
        {
            tab.Camera.OffsetY -= PanStep;
            tab.ClampCamera(ww, wh);
        }
        InvalidateNavigation();
    }

    public void HandleArrowUp()
    {
        if (ActiveTab is not { } tab) return;
        var (ww, wh) = GetWindowSize();

        if (tab.Rail.Active)
        {
            int currentPage = tab.CurrentPage;
            switch (tab.Rail.PrevLine())
            {
                case NavResult.PageBoundaryPrev:
                    tab.GoToPage(currentPage - 1, _worker, ww, wh);
                    tab.QueueLookahead(_config.AnalysisLookaheadPages);
                    if (tab.Rail.Active) { tab.Rail.JumpToEnd(); tab.StartSnap(ww, wh); }
                    break;
                case NavResult.Ok:
                    tab.StartSnap(ww, wh);
                    break;
            }
        }
        else
        {
            tab.Camera.OffsetY += PanStep;
            tab.ClampCamera(ww, wh);
        }
        InvalidateNavigation();
    }

    public void HandleArrowRight() => HandleHorizontalArrow(ScrollDirection.Forward, -PanStep);
    public void HandleArrowLeft() => HandleHorizontalArrow(ScrollDirection.Backward, PanStep);

    private void HandleHorizontalArrow(ScrollDirection direction, double panDelta)
    {
        if (ActiveTab is not { } tab) return;
        if (tab.Rail.Active)
            tab.Rail.StartScroll(direction, tab.Camera.OffsetX);
        else
        {
            var (ww, wh) = GetWindowSize();
            tab.Camera.OffsetX += panDelta;
            tab.ClampCamera(ww, wh);
        }
        InvalidateCamera();
        OnPropertyChanged(nameof(ActiveTab));
        RequestAnimationFrame();
    }

    public void HandleArrowRelease(bool isHorizontal)
    {
        if (isHorizontal)
            ActiveTab?.Rail.StopScroll();
        RequestAnimationFrame();
    }

    public void HandleClick(double canvasX, double canvasY)
    {
        if (ActiveTab is not { } tab || !tab.Rail.Active || !tab.Rail.HasAnalysis) return;

        double pageX = (canvasX - tab.Camera.OffsetX) / tab.Camera.Zoom;
        double pageY = (canvasY - tab.Camera.OffsetY) / tab.Camera.Zoom;

        if (tab.Rail.FindBlockAtPoint(pageX, pageY) is { } navIdx)
        {
            tab.Rail.CurrentBlock = navIdx;
            tab.Rail.CurrentLine = 0;
            var (ww, wh) = GetWindowSize();
            tab.StartSnap(ww, wh);
            InvalidateNavigation();
        }
    }

    public void HandleZoomKey(bool zoomIn)
    {
        if (ActiveTab is not { } tab) return;
        var (ww, wh) = GetWindowSize();
        double newZoom = zoomIn ? tab.Camera.Zoom * ZoomStep : tab.Camera.Zoom / ZoomStep;
        tab.ApplyZoom(newZoom, ww, wh);
        tab.Camera.NotifyZoomChange();
        tab.UpdateRenderDpiIfNeeded();
        InvalidateCamera();
        OnPropertyChanged(nameof(ActiveTab));
    }

    public void HandleResetZoom() => FitPage();

    private double _vpWidth = 1200;
    private double _vpHeight = 900;

    /// <summary>
    /// Called by MainWindow whenever the viewport control is resized.
    /// Keeps layout calculations consistent with the actual drawable area,
    /// not the full window ClientSize (which includes menu/tab/status chrome).
    /// </summary>
    public void SetViewportSize(double w, double h)
    {
        if (w > 0) _vpWidth = w;
        if (h > 0) _vpHeight = h;
    }

    private (double Width, double Height) GetWindowSize() => (_vpWidth, _vpHeight);

    private static string? FindModelPath()
    {
        const string filename = "PP-DocLayoutV3.onnx";
        string?[] candidates =
        [
            // Next to the executable
            Path.Combine(AppContext.BaseDirectory, "models", filename),
            // APPDIR (AppImage or similar)
            Environment.GetEnvironmentVariable("APPDIR") is { } appDir
                ? Path.Combine(appDir, "models", filename) : null,
            // User data directory
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "railreader2", "models", filename),
            // Current working directory
            Path.Combine("models", filename),
            // Walk up from CWD to find repo root's models/
            Path.Combine("..", "models", filename),
            Path.Combine("..", "..", "models", filename),
            Path.Combine("..", "..", "..", "models", filename),
        ];

        foreach (var path in candidates)
        {
            if (path is not null && File.Exists(path))
                return Path.GetFullPath(path);
        }
        return null;
    }

    // --- Granular invalidation helpers ---

    private void Invalidate(Action? target)
    {
        if (_invalidation is not null)
            target?.Invoke();
        else
            _invalidateCanvas?.Invoke();
    }

    private void InvalidateCamera() => Invalidate(_invalidation?.InvalidateCamera);
    private void InvalidatePage() => Invalidate(_invalidation?.InvalidatePage);
    private void InvalidateOverlay() => Invalidate(_invalidation?.InvalidateOverlay);

    private void InvalidateNavigation()
    {
        InvalidateCamera();
        InvalidateOverlay();
        OnPropertyChanged(nameof(ActiveTab));
        RequestAnimationFrame();
    }

    private void InvalidateAll()
    {
        InvalidateCamera();
        InvalidatePage();
        InvalidateOverlay();
    }

    public void RequestCameraUpdate() => InvalidateCamera();
}

public sealed class InvalidationCallbacks
{
    public Action? InvalidateCamera { get; init; }
    public Action? InvalidatePage { get; init; }
    public Action? InvalidateOverlay { get; init; }
}
