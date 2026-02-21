using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RailReader2.Models;
using RailReader2.Services;
using RailReader2.Views;
using SkiaSharp;

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
    [ObservableProperty] private bool _showSearch;
    [ObservableProperty] private bool _isRadialMenuOpen;
    [ObservableProperty] private double _radialMenuX;
    [ObservableProperty] private double _radialMenuY;

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    // Search state
    public List<SearchMatch> SearchMatches { get; private set; } = [];
    public List<SearchMatch>? CurrentPageSearchMatches { get; private set; }
    public int ActiveMatchIndex { get; set; }

    // Annotation tool state
    public AnnotationTool ActiveTool { get; set; } = AnnotationTool.None;
    public bool IsAnnotating => ActiveTool != AnnotationTool.None;
    public Annotation? SelectedAnnotation { get; set; }
    public Annotation? PreviewAnnotation { get; set; }
    public string ActiveAnnotationColor { get; set; } = "#FFFF00";
    public float ActiveAnnotationOpacity { get; set; } = 0.4f;
    public float ActiveStrokeWidth { get; set; } = 2f;

    // In-progress annotation building state
    private List<Models.PointF>? _freehandPoints;
    private float _rectStartX, _rectStartY;
    private int _highlightCharStart = -1;

    // Text selection state
    public string? SelectedText { get; set; }
    public List<HighlightRect>? TextSelectionRects { get; set; }
    private int _textSelectCharStart = -1;

    // Clipboard and radial menu callbacks wired by MainWindow
    public Action<string>? CopyToClipboard { get; set; }
    public Action? OnRadialMenuOpening { get; set; }

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

            var (gotResults, needsAnim) = PollAnalysisResults();
            var tab = ActiveTab;
            if (tab is not null && !_animationRequested)
                tab.SubmitPendingLookahead(_worker);
            if (gotResults)
                InvalidateOverlay();
            if (needsAnim)
                RequestAnimationFrame();
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
        var (gotResults, needsAnim) = PollAnalysisResults();
        if (needsAnim) animating = true;

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

    /// <summary>
    /// Drains the worker's result queue, caches results, and sets up rail
    /// navigation when the current page's analysis arrives.
    /// Returns (gotResults, needsAnimation) so callers can invalidate and
    /// request animation frames appropriately.
    /// </summary>
    private (bool GotResults, bool NeedsAnimation) PollAnalysisResults()
    {
        bool got = false;
        bool needsAnim = false;
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
                    // Re-check zoom threshold now that HasAnalysis is true —
                    // without this, rail mode won't activate until the next zoom change.
                    tab.UpdateRailZoom(ww, wh);
                    Console.Error.WriteLine($"[Analysis] Rail has {tab.Rail.NavigableCount} navigable blocks, Active={tab.Rail.Active}");
                    if (tab.Rail.Active)
                    {
                        tab.StartSnap(ww, wh);
                        needsAnim = true;
                    }
                }
            }
        }
        return (got, needsAnim);
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
            tab.LoadAnnotations();

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
        UpdateCurrentPageMatches();
        InvalidateAll();
        InvalidateSearch();
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
        RequestAnimationFrame();
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

    // --- Annotations ---

    public void OpenRadialMenu(double screenX, double screenY)
    {
        double menuSize = 210 * (_config?.UiFontScale ?? 1.0);
        RadialMenuX = screenX - menuSize / 2;
        RadialMenuY = screenY - menuSize / 2;
        OnRadialMenuOpening?.Invoke();
        IsRadialMenuOpen = true;
    }

    public void CloseRadialMenu()
    {
        IsRadialMenuOpen = false;
    }

    public void SetAnnotationTool(AnnotationTool tool)
    {
        ActiveTool = tool;
        SelectedAnnotation = null;
        PreviewAnnotation = null;
        _freehandPoints = null;
        _highlightCharStart = -1;

        // Clear text selection when switching away from TextSelect
        if (tool != AnnotationTool.TextSelect)
        {
            SelectedText = null;
            TextSelectionRects = null;
            _textSelectCharStart = -1;
            InvalidateAnnotations();
        }

        // Set default colours per tool
        switch (tool)
        {
            case AnnotationTool.TextSelect:
                break;
            case AnnotationTool.Highlight:
                ActiveAnnotationColor = "#FFFF00";
                ActiveAnnotationOpacity = 0.35f;
                break;
            case AnnotationTool.Pen:
                ActiveAnnotationColor = "#FF0000";
                ActiveAnnotationOpacity = 0.8f;
                ActiveStrokeWidth = 2f;
                break;
            case AnnotationTool.Rectangle:
                ActiveAnnotationColor = "#0066FF";
                ActiveAnnotationOpacity = 0.5f;
                ActiveStrokeWidth = 2f;
                break;
            case AnnotationTool.TextNote:
                ActiveAnnotationColor = "#FFCC00";
                ActiveAnnotationOpacity = 0.9f;
                break;
            case AnnotationTool.Eraser:
            case AnnotationTool.None:
                break;
        }

        CloseRadialMenu();
        OnPropertyChanged(nameof(IsAnnotating));
        OnPropertyChanged(nameof(ActiveTool));
    }

    public void CancelAnnotationTool()
    {
        PreviewAnnotation = null;
        _freehandPoints = null;
        _highlightCharStart = -1;
        _textSelectCharStart = -1;
        SelectedText = null;
        TextSelectionRects = null;
        ActiveTool = AnnotationTool.None;
        SelectedAnnotation = null;
        OnPropertyChanged(nameof(IsAnnotating));
        OnPropertyChanged(nameof(ActiveTool));
        InvalidateAnnotations();
    }

    public void HandleAnnotationPointerDown(double pageX, double pageY)
    {
        if (ActiveTab is not { } tab) return;

        switch (ActiveTool)
        {
            case AnnotationTool.TextSelect:
                _textSelectCharStart = FindNearestCharIndex(tab, (float)pageX, (float)pageY);
                SelectedText = null;
                TextSelectionRects = null;
                InvalidateAnnotations();
                break;
            case AnnotationTool.Highlight:
                _highlightCharStart = FindNearestCharIndex(tab, (float)pageX, (float)pageY);
                break;
            case AnnotationTool.Pen:
                _freehandPoints = [new Models.PointF((float)pageX, (float)pageY)];
                break;
            case AnnotationTool.Rectangle:
                _rectStartX = (float)pageX;
                _rectStartY = (float)pageY;
                break;
            case AnnotationTool.TextNote:
                // Single click places the note
                var note = new TextNoteAnnotation
                {
                    X = (float)pageX,
                    Y = (float)pageY,
                    Color = ActiveAnnotationColor,
                    Opacity = ActiveAnnotationOpacity,
                    Text = "Note",
                };
                tab.AddAnnotation(tab.CurrentPage, note);
                InvalidateAnnotations();
                break;
            case AnnotationTool.Eraser:
                EraseAtPoint(tab, (float)pageX, (float)pageY);
                break;
        }
    }

    public void HandleAnnotationPointerMove(double pageX, double pageY)
    {
        if (ActiveTab is not { } tab) return;

        switch (ActiveTool)
        {
            case AnnotationTool.TextSelect when _textSelectCharStart >= 0:
                int tsEnd = FindNearestCharIndex(tab, (float)pageX, (float)pageY);
                if (tsEnd >= 0)
                {
                    int tsStart = Math.Min(_textSelectCharStart, tsEnd);
                    int tsLen = Math.Max(_textSelectCharStart, tsEnd) - tsStart + 1;
                    var pageText = tab.GetOrExtractText(tab.CurrentPage);
                    TextSelectionRects = BuildHighlightRects(pageText, tsStart, tsLen);
                    // Extract text substring
                    int textEnd = Math.Min(tsStart + tsLen, pageText.Text.Length);
                    SelectedText = tsStart < pageText.Text.Length
                        ? pageText.Text[tsStart..textEnd]
                        : null;
                    InvalidateAnnotations();
                }
                break;
            case AnnotationTool.Highlight when _highlightCharStart >= 0:
                int endChar = FindNearestCharIndex(tab, (float)pageX, (float)pageY);
                if (endChar >= 0)
                {
                    int start = Math.Min(_highlightCharStart, endChar);
                    int end = Math.Max(_highlightCharStart, endChar);
                    var pageText = tab.GetOrExtractText(tab.CurrentPage);
                    var rects = BuildHighlightRects(pageText, start, end - start + 1);
                    PreviewAnnotation = new HighlightAnnotation
                    {
                        Rects = rects,
                        Color = ActiveAnnotationColor,
                        Opacity = ActiveAnnotationOpacity,
                    };
                    InvalidateAnnotations();
                }
                break;
            case AnnotationTool.Pen when _freehandPoints is not null:
                _freehandPoints.Add(new Models.PointF((float)pageX, (float)pageY));
                PreviewAnnotation = new FreehandAnnotation
                {
                    Points = [.. _freehandPoints],
                    Color = ActiveAnnotationColor,
                    Opacity = ActiveAnnotationOpacity,
                    StrokeWidth = ActiveStrokeWidth,
                };
                InvalidateAnnotations();
                break;
            case AnnotationTool.Rectangle:
                float rx = Math.Min(_rectStartX, (float)pageX);
                float ry = Math.Min(_rectStartY, (float)pageY);
                float rw = Math.Abs((float)pageX - _rectStartX);
                float rh = Math.Abs((float)pageY - _rectStartY);
                PreviewAnnotation = new RectAnnotation
                {
                    X = rx, Y = ry, W = rw, H = rh,
                    Color = ActiveAnnotationColor,
                    Opacity = ActiveAnnotationOpacity,
                    StrokeWidth = ActiveStrokeWidth,
                };
                InvalidateAnnotations();
                break;
        }
    }

    public void HandleAnnotationPointerUp(double pageX, double pageY)
    {
        if (ActiveTab is not { } tab) return;

        switch (ActiveTool)
        {
            case AnnotationTool.TextSelect:
                _textSelectCharStart = -1;
                // Selection stays visible until cancelled or tool changed
                break;
            case AnnotationTool.Highlight when PreviewAnnotation is HighlightAnnotation h:
                tab.AddAnnotation(tab.CurrentPage, h);
                PreviewAnnotation = null;
                _highlightCharStart = -1;
                InvalidateAnnotations();
                break;
            case AnnotationTool.Pen when PreviewAnnotation is FreehandAnnotation f:
                tab.AddAnnotation(tab.CurrentPage, f);
                PreviewAnnotation = null;
                _freehandPoints = null;
                InvalidateAnnotations();
                break;
            case AnnotationTool.Rectangle when PreviewAnnotation is RectAnnotation r:
                if (r.W > 1 && r.H > 1) // avoid accidental tiny rects
                    tab.AddAnnotation(tab.CurrentPage, r);
                PreviewAnnotation = null;
                InvalidateAnnotations();
                break;
        }
    }

    private void EraseAtPoint(TabViewModel tab, float pageX, float pageY)
    {
        if (tab.Annotations is null) return;
        if (!tab.Annotations.Pages.TryGetValue(tab.CurrentPage, out var list)) return;

        // Search from top (last drawn) to bottom for hit
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (AnnotationRenderer.HitTest(list[i], pageX, pageY))
            {
                tab.RemoveAnnotation(tab.CurrentPage, list[i]);
                InvalidateAnnotations();
                return;
            }
        }
    }

    private static int FindNearestCharIndex(TabViewModel tab, float pageX, float pageY)
    {
        var pageText = tab.GetOrExtractText(tab.CurrentPage);
        if (pageText.CharBoxes.Count == 0) return -1;

        float bestDist = float.MaxValue;
        int bestIdx = -1;
        for (int i = 0; i < pageText.CharBoxes.Count; i++)
        {
            var cb = pageText.CharBoxes[i];
            if (cb.Left == 0 && cb.Right == 0 && cb.Top == 0 && cb.Bottom == 0) continue;
            float cx = (cb.Left + cb.Right) / 2;
            float cy = (cb.Top + cb.Bottom) / 2;
            float dist = (cx - pageX) * (cx - pageX) + (cy - pageY) * (cy - pageY);
            if (dist < bestDist) { bestDist = dist; bestIdx = i; }
        }
        return bestIdx;
    }

    private static List<HighlightRect> BuildHighlightRects(PageText pageText, int charStart, int charLength)
    {
        var rects = new List<HighlightRect>();
        int end = Math.Min(charStart + charLength, pageText.CharBoxes.Count);

        float curLeft = 0, curTop = 0, curRight = 0, curBottom = 0;
        bool hasRect = false;
        const float lineThreshold = 4f;

        for (int i = charStart; i < end; i++)
        {
            var cb = pageText.CharBoxes[i];
            if (cb.Left == 0 && cb.Right == 0 && cb.Top == 0 && cb.Bottom == 0) continue;

            if (!hasRect)
            {
                curLeft = cb.Left; curTop = cb.Top; curRight = cb.Right; curBottom = cb.Bottom;
                hasRect = true;
            }
            else if (Math.Abs(cb.Top - curTop) < lineThreshold)
            {
                curLeft = Math.Min(curLeft, cb.Left);
                curRight = Math.Max(curRight, cb.Right);
                curTop = Math.Min(curTop, cb.Top);
                curBottom = Math.Max(curBottom, cb.Bottom);
            }
            else
            {
                rects.Add(new HighlightRect(curLeft - 1, curTop, curRight - curLeft + 2, curBottom - curTop));
                curLeft = cb.Left; curTop = cb.Top; curRight = cb.Right; curBottom = cb.Bottom;
            }
        }
        if (hasRect)
            rects.Add(new HighlightRect(curLeft - 1, curTop, curRight - curLeft + 2, curBottom - curTop));
        return rects;
    }

    public void CopySelectedText()
    {
        if (SelectedText is not null)
            CopyToClipboard?.Invoke(SelectedText);
        CloseRadialMenu();
    }

    public void UndoAnnotation()
    {
        ActiveTab?.Undo();
        InvalidateAnnotations();
    }

    public void RedoAnnotation()
    {
        ActiveTab?.Redo();
        InvalidateAnnotations();
    }

    private void InvalidateAnnotations()
    {
        _invalidation?.InvalidateAnnotations?.Invoke();
    }

    [RelayCommand]
    public async Task ExportAnnotated()
    {
        if (_window is null || ActiveTab is not { } tab || tab.Annotations is null) return;

        var file = await _window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export with Annotations",
            DefaultExtension = "pdf",
            FileTypeChoices = [new FilePickerFileType("PDF Files") { Patterns = ["*.pdf"] }],
            SuggestedFileName = Path.GetFileNameWithoutExtension(tab.FilePath) + "_annotated.pdf",
        });
        if (file is null) return;

        var outputPath = file.TryGetLocalPath() ?? file.Path.LocalPath;
        if (outputPath is null) return;

        try
        {
            await Task.Run(() =>
            {
                AnnotationExportService.Export(tab.Pdf, tab.Annotations, outputPath,
                    onProgress: (page, total) =>
                        Console.Error.WriteLine($"[Export] Page {page + 1} of {total}..."));
            });
            Console.Error.WriteLine($"[Export] Saved to {outputPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Export] Failed: {ex.Message}");
        }
    }

    // --- Search ---

    public void OpenSearch()
    {
        ShowSearch = true;
    }

    public void CloseSearch()
    {
        ShowSearch = false;
        SearchMatches = [];
        CurrentPageSearchMatches = null;
        ActiveMatchIndex = 0;
        InvalidateSearch();
    }

    public void ExecuteSearch(string query, bool caseSensitive, bool useRegex)
    {
        SearchMatches = [];
        CurrentPageSearchMatches = null;
        ActiveMatchIndex = 0;

        if (string.IsNullOrEmpty(query) || ActiveTab is not { } tab)
        {
            InvalidateSearch();
            return;
        }

        Regex? regex = null;
        if (useRegex)
        {
            try
            {
                var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                regex = new Regex(query, options);
            }
            catch (RegexParseException)
            {
                InvalidateSearch();
                return;
            }
        }

        var allMatches = new List<SearchMatch>();
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        for (int page = 0; page < tab.PageCount; page++)
        {
            var pageText = tab.GetOrExtractText(page);
            if (string.IsNullOrEmpty(pageText.Text)) continue;

            IEnumerable<(int Index, int Length)> hits;
            if (regex is not null)
            {
                hits = regex.Matches(pageText.Text).Select(m => (m.Index, m.Length));
            }
            else
            {
                hits = FindAllOccurrences(pageText.Text, query, comparison);
            }

            foreach (var (index, length) in hits)
            {
                var rects = BuildMatchRects(pageText, index, length);
                if (rects.Count > 0)
                    allMatches.Add(new SearchMatch(page, index, length, rects));
            }
        }

        SearchMatches = allMatches;
        if (allMatches.Count > 0)
        {
            // Find first match on or after current page
            int firstOnCurrentOrAfter = allMatches.FindIndex(m => m.PageIndex >= tab.CurrentPage);
            ActiveMatchIndex = firstOnCurrentOrAfter >= 0 ? firstOnCurrentOrAfter : 0;
            NavigateToActiveMatch();
        }

        UpdateCurrentPageMatches();
        InvalidateSearch();
    }

    public void NextMatch()
    {
        if (SearchMatches.Count == 0) return;
        ActiveMatchIndex = (ActiveMatchIndex + 1) % SearchMatches.Count;
        NavigateToActiveMatch();
        UpdateCurrentPageMatches();
        InvalidateSearch();
    }

    public void PreviousMatch()
    {
        if (SearchMatches.Count == 0) return;
        ActiveMatchIndex = (ActiveMatchIndex - 1 + SearchMatches.Count) % SearchMatches.Count;
        NavigateToActiveMatch();
        UpdateCurrentPageMatches();
        InvalidateSearch();
    }

    private void NavigateToActiveMatch()
    {
        if (ActiveTab is not { } tab) return;
        if (ActiveMatchIndex < 0 || ActiveMatchIndex >= SearchMatches.Count) return;
        var match = SearchMatches[ActiveMatchIndex];
        if (match.PageIndex != tab.CurrentPage)
            GoToPage(match.PageIndex);
    }

    public void UpdateCurrentPageMatches()
    {
        if (ActiveTab is not { } tab)
        {
            CurrentPageSearchMatches = null;
            return;
        }
        CurrentPageSearchMatches = SearchMatches
            .Where(m => m.PageIndex == tab.CurrentPage)
            .ToList();
    }

    private void InvalidateSearch()
    {
        _invalidation?.InvalidateSearch?.Invoke();
    }

    private static IEnumerable<(int Index, int Length)> FindAllOccurrences(string text, string query, StringComparison comparison)
    {
        int pos = 0;
        while (pos < text.Length)
        {
            int idx = text.IndexOf(query, pos, comparison);
            if (idx < 0) break;
            yield return (idx, query.Length);
            pos = idx + 1;
        }
    }

    /// <summary>
    /// Builds a list of SKRects for a text match by merging adjacent character boxes on the same line.
    /// </summary>
    private static List<SKRect> BuildMatchRects(PageText pageText, int charStart, int charLength)
    {
        var rects = new List<SKRect>();
        if (pageText.CharBoxes.Count == 0) return rects;

        int end = Math.Min(charStart + charLength, pageText.CharBoxes.Count);

        float curLeft = 0, curTop = 0, curRight = 0, curBottom = 0;
        bool hasRect = false;
        const float lineThreshold = 4f; // max Y difference to consider same line

        for (int i = charStart; i < end; i++)
        {
            var cb = pageText.CharBoxes[i];
            // Skip zero-size boxes (whitespace/control chars)
            if (cb.Left == 0 && cb.Right == 0 && cb.Top == 0 && cb.Bottom == 0)
                continue;

            if (!hasRect)
            {
                curLeft = cb.Left;
                curTop = cb.Top;
                curRight = cb.Right;
                curBottom = cb.Bottom;
                hasRect = true;
            }
            else if (Math.Abs(cb.Top - curTop) < lineThreshold)
            {
                // Same line — extend horizontally
                curLeft = Math.Min(curLeft, cb.Left);
                curRight = Math.Max(curRight, cb.Right);
                curTop = Math.Min(curTop, cb.Top);
                curBottom = Math.Max(curBottom, cb.Bottom);
            }
            else
            {
                // New line — flush current rect
                rects.Add(new SKRect(curLeft, curTop, curRight, curBottom));
                curLeft = cb.Left;
                curTop = cb.Top;
                curRight = cb.Right;
                curBottom = cb.Bottom;
            }
        }

        if (hasRect)
            rects.Add(new SKRect(curLeft, curTop, curRight, curBottom));

        return rects;
    }
}

public sealed class InvalidationCallbacks
{
    public Action? InvalidateCamera { get; init; }
    public Action? InvalidatePage { get; init; }
    public Action? InvalidateOverlay { get; init; }
    public Action? InvalidateSearch { get; init; }
    public Action? InvalidateAnnotations { get; init; }
}
