using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RailReader.Core;
using RailReader.Core.Commands;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using RailReader2.Services;

namespace RailReader2.ViewModels;

/// <summary>The side-panel tabs, used by ShowPane for menu-driven pane navigation.</summary>
public enum SidePane { Outline, Bookmarks, Index, Search, Comments }

// Core infrastructure: fields, constructor, animation, invalidation, config, status toast.
// See partial class files for: Documents, Navigation, Annotations, Search.
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly DocumentController _controller;
    private readonly ILogger _logger;
    private Window? _window;
    private DispatcherTimer? _pollTimer;
    private DispatcherTimer? _backgroundTimer;
    private InvalidationCallbacks? _invalidation;
    private bool _animationRequested;

    // Avalonia passes a compositor-synchronized TimeSpan to RequestAnimationFrame
    // callbacks. We use successive timestamps to compute dt. A null value means
    // the next callback is the first of a new animation sequence (after idle).
    private TimeSpan? _lastFrameTime;

    [ObservableProperty] private int _activeTabIndex;
    [ObservableProperty] private bool _showOutline;

    /// <summary>Which side-panel section is currently expanded in the accordion, or null when
    /// every section is collapsed. The window key handler reads/sets it for the pane shortcuts,
    /// and the accordion keeps it in two-way sync with the open Expander. Null is meaningful:
    /// collapsing the open section clears it, so a later request for the same pane registers as
    /// a change and re-expands it.</summary>
    [ObservableProperty] private SidePane? _activePane = SidePane.Outline;

    /// <summary>Set by the Search pane while its input box has focus, so the window-level
    /// key handler lets text keys through instead of treating them as nav shortcuts.</summary>
    public bool IsSearchInputFocused { get; set; }

    /// <summary>
    /// Callback set by the code-behind to read the current sidebar column width
    /// from the grid (which may have been resized via GridSplitter).
    /// </summary>
    public Func<double>? ReadSidePanelWidth { get; set; }

    partial void OnShowOutlineChanged(bool value)
    {
        // Keep the active tab's sidebar state in sync
        if (ActiveTab is { } tab)
            tab.ShowSidePanel = value;
    }

    /// <summary>Show the side panel and switch it to the given pane (for discoverability
    /// via the View menu).</summary>
    public void ShowPane(SidePane pane)
    {
        ActivePane = pane;
        ShowOutline = true;
    }

    /// <summary>Toggle a side pane: hide the side panel if it is already the active pane,
    /// otherwise show the panel and switch to it (keyboard pane shortcuts).</summary>
    public void TogglePane(SidePane pane)
    {
        if (ShowOutline && ActivePane == pane)
            ShowOutline = false;
        else
        {
            ActivePane = pane;
            ShowOutline = true;
        }
    }

    /// <summary>Raised when bookmarks or navigation-back availability change, so the
    /// Bookmarks pane can refresh its list and Back button.</summary>
    public event Action? BookmarksChanged;
    internal void NotifyBookmarksChanged() => BookmarksChanged?.Invoke();

    /// <summary>Raised when a side pane navigates the document via a click, so the window can
    /// move keyboard focus to the viewport — otherwise the pane keeps the focus and subsequent
    /// arrows/scroll move its list instead of the page.</summary>
    public event Action? ViewportFocusRequested;

    /// <summary>Called when a side pane navigates the document via a click. A discrete jump
    /// shouldn't inherit continuous-scroll momentum from an arrow key that's still held (in rail
    /// mode that briefly fought the new position), so reset the held arrow input first, then ask
    /// the window to focus the viewport.</summary>
    public void RequestViewportFocus()
    {
        _controller.HandleArrowRelease(true);
        _controller.ClearPageEdgeHold();
        ViewportFocusRequested?.Invoke();
    }

    [ObservableProperty] private bool _showMinimap;
    [ObservableProperty] private bool _showSettings;
    [ObservableProperty] private bool _showAbout;
    [ObservableProperty] private bool _showShortcuts;
    [ObservableProperty] private bool _showGoToPage;
    [ObservableProperty] private string? _cleanupMessage;

    [ObservableProperty] private bool _isFullScreen;
    [ObservableProperty] private bool _showFullScreenHeader;
    [ObservableProperty] private bool _showFullScreenFooter;
    [ObservableProperty] private bool _showBookmarkDialog;

    /// <summary>
    /// When true, the annotation toolbar's tool section is shown and annotation
    /// editing gestures are active. Text selection/copy remain available outside
    /// this mode. Toggled via the toolbar, the right-click menu, Ctrl+E, the Edit
    /// menu, or implicitly by picking an annotation tool (keys 1–5).
    /// </summary>
    [ObservableProperty] private bool _isAnnotationMode;

    partial void OnIsAnnotationModeChanged(bool value)
    {
        if (!value)
        {
            // Leaving annotation mode: drop any active tool and selected annotation
            // so the viewport returns to plain browse/pan. Entering preserves any
            // current text selection so markup can be applied immediately.
            SetAnnotationTool(AnnotationTool.None);
            SelectedAnnotation = null;
            OnPropertyChanged(nameof(SelectedAnnotation));
            InvalidateAnnotations();
        }
    }

    /// <summary>Raised after any annotation content mutation (add/edit/delete/undo/
    /// import/review-state) so views like the Comments panel can refresh.</summary>
    public event Action? AnnotationsMutated;
    internal void NotifyAnnotationsMutated() => AnnotationsMutated?.Invoke();

    // --- Scan All (whole-document figure discovery) ---
    [ObservableProperty] private bool _isScanAllActive;
    [ObservableProperty] private string _scanAllProgress = "";
    private DispatcherTimer? _scanAllTimer;
    private int _scanAllOriginalWindowPages;
    private int _scanAllLastScanned;
    private int _scanAllStallTicks;
    // ~30s of stalled progress (no page completed) before we give up — long
    // enough to ride out a slow page on a weak CPU, short enough to recover
    // from a wedged analysis worker without locking the UI indefinitely.
    private const int ScanAllStallTickLimit = 600;

    /// <summary>True when the tab bar should be visible (not fullscreen, or hovering at top edge).</summary>
    public bool IsTabBarVisible => !IsFullScreen || ShowFullScreenHeader;

    /// <summary>True when the status bar should be visible (not fullscreen, or hovering at bottom edge).</summary>
    public bool IsStatusBarVisible => !IsFullScreen || ShowFullScreenFooter;

    partial void OnIsFullScreenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsTabBarVisible));
        OnPropertyChanged(nameof(IsStatusBarVisible));
        if (!value)
        {
            ShowFullScreenHeader = false;
            ShowFullScreenFooter = false;
        }
    }

    partial void OnShowFullScreenHeaderChanged(bool value) => OnPropertyChanged(nameof(IsTabBarVisible));
    partial void OnShowFullScreenFooterChanged(bool value) => OnPropertyChanged(nameof(IsStatusBarVisible));

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    // Delegated state from controller subsystems
    public List<SearchMatch> SearchMatches => _controller.Search.SearchMatches;
    public List<SearchMatch>? CurrentPageSearchMatches => _controller.Search.CurrentPageSearchMatches;
    public int ActiveMatchIndex
    {
        get => _controller.Search.ActiveMatchIndex;
        set => _controller.Search.ActiveMatchIndex = value;
    }

    public AnnotationTool ActiveTool => _controller.Annotations.ActiveTool;
    public bool IsAnnotating => _controller.Annotations.IsAnnotating;
    public Annotation? SelectedAnnotation
    {
        get => _controller.Annotations.SelectedAnnotation;
        set => _controller.Annotations.SelectedAnnotation = value;
    }
    public Annotation? PreviewAnnotation => _controller.Annotations.PreviewAnnotation;
    public string ActiveAnnotationColor
    {
        get => _controller.Annotations.ActiveAnnotationColor;
        set => _controller.Annotations.ActiveAnnotationColor = value;
    }
    public float ActiveAnnotationOpacity
    {
        get => _controller.Annotations.ActiveAnnotationOpacity;
        set => _controller.Annotations.ActiveAnnotationOpacity = value;
    }
    public float ActiveStrokeWidth
    {
        get => _controller.Annotations.ActiveStrokeWidth;
        set => _controller.Annotations.ActiveStrokeWidth = value;
    }

    public string? SelectedText => _controller.Annotations.SelectedText;
    public List<HighlightRect>? TextSelectionRects => _controller.Annotations.TextSelectionRects;

    public Action<string>? CopyToClipboard
    {
        get => _controller.Annotations.CopyToClipboard;
        set => _controller.Annotations.CopyToClipboard = value;
    }

    public bool AutoScrollActive => _controller.AutoScrollActive;
    public bool RailPaused => _controller.RailPaused;

    public void ResumeRailFromPause()
        => Dispatch(_controller.ResumeRailFromPause, InvalidateCameraAndTab, animate: true);

    [ObservableProperty] private bool _jumpMode;
    partial void OnJumpModeChanged(bool value) => _controller.JumpMode = value;

    /// <summary>The mutable, file-backed config. UI binds to this; Core sees only an immutable snapshot via <see cref="CoreSettings"/>.</summary>
    public AppConfig AppConfig => _appConfig;
    private readonly AppConfig _appConfig;
    public ColourEffectShaders ColourEffects { get; }
    public DocumentController Controller => _controller;

    public TabViewModel? ActiveTab =>
        ActiveTabIndex >= 0 && ActiveTabIndex < Tabs.Count ? Tabs[ActiveTabIndex] : null;

    /// <summary>
    /// Human-readable name of the layout-detection model loaded at startup
    /// (e.g. "PP-DocLayoutV3", "Docling Heron", "Custom: foo.onnx"). Null if
    /// the analyzer failed to initialise (layout-less mode). Shown in the
    /// debug overlay so users can tell at a glance which model is active.
    /// </summary>
    public string? ActiveLayoutModelName { get; private set; }

    /// <summary>Path to the current session log file, or null if file logging unavailable.</summary>
    public string? LogFilePath => _logger.LogFilePath;

    public MainWindowViewModel(AppConfig config, ILogger? logger = null)
    {
        _appConfig = config;
        _logger = logger ?? RailReaderLogging.Logger;
        ColourEffects = new ColourEffectShaders(_logger);
        _controller = new DocumentController(config.ToCoreSettings(), config, CompositeAnnotationStore.Default,
            new AvaloniaThreadMarshaller(), new RailReader.Renderer.Skia.SkiaPdfServiceFactory(), _logger);
        try
        {
            var resolution = CustomLayoutModelLoader.ResolveModel(config, _logger);
            if (resolution.ModelPath != null && resolution.Capabilities != null && resolution.Factory != null)
            {
                _logger.Debug($"[ONNX] Starting worker with model: {resolution.ModelPath}");
                _controller.InitializeWorker(resolution.Capabilities, resolution.Factory);
                ActiveLayoutModelName = resolution.DisplayName;
            }
        }
        catch (Exception ex) { _logger.Error("[ONNX] Worker init failed", ex); }
        _controller.StateChanged += OnControllerStateChanged;
        _controller.StatusMessage += ShowStatusToast;
        // Push-based accessibility announcements: Core fires these (on the UI thread) the moment the
        // page or rail reading position changes — including jumps that don't otherwise repaint the
        // overlay (e.g. NavigateToRole). The render path still calls NotifyAccessibilityStateChanged
        // for mode transitions; the peer's signature debounce makes the overlap free.
        _controller.PageChanged = p => { AnnounceAccessibilityState(); PageChangedNotification?.Invoke(p); };
        _controller.ReadingPositionChanged = _ => AnnounceAccessibilityState();
        WireAnnotationStoreSignals();
        SetupPollTimer();
    }

    private void OnControllerStateChanged(string propertyName)
    {
        switch (propertyName)
        {
            case nameof(AutoScrollActive):
                OnPropertyChanged(nameof(AutoScrollActive));
                break;
        }
    }

    public void SetWindow(Window window)
    {
        _window = window;
        ApplyFontScale();
    }
    public void SetInvalidation(InvalidationCallbacks callbacks) => _invalidation = callbacks;

    // --- Accessibility / automation queries (consumed by DocumentViewportAutomationPeer) ---

    /// <summary>The current rail reading position (page / block / line + role and extracted text), or
    /// null when not rail-reading. Role and text are computed in Core, so the a11y peer no longer has to
    /// hand-roll text extraction. See <see cref="DocumentController.GetReadingPosition"/>.</summary>
    public ReadingPosition? GetReadingPosition() => _controller.GetReadingPosition();

    /// <summary>Structured layout of a page (block roles, reading order, text previews) for the
    /// on-demand accessibility/automation read channel. Defaults to the current page; null until the
    /// page has been analysed. See <see cref="DocumentController.GetPageDescription"/>.</summary>
    public PageDescription? GetPageDescription(int? page = null) => _controller.GetPageDescription(page: page);

    private void AnnounceAccessibilityState() => _invalidation?.AnnounceAccessibility?.Invoke();

    // --- Poll timer & animation ---

    private void SetupPollTimer()
    {
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _pollTimer.Tick += (_, _) =>
        {
            if (_animationRequested) return;

            var (gotResults, needsAnim, _) = _controller.PollAnalysisResults();
            var tab = ActiveTab;
            if (tab is not null && !_animationRequested)
                tab.SubmitPendingLookahead(_controller.Worker);
            if (gotResults)
                InvalidateOverlay();
            if (needsAnim)
                RequestAnimationFrame();
            bool workerBusy = _controller.Worker is not null && !_controller.Worker.IsIdle;
            if (!workerBusy) _pollTimer?.Stop();
        };

        // Separate low-frequency timer for background analysis.
        // Runs independently of the animation loop to avoid interfering with
        // zoom/scroll performance. Polls at 500ms — fast enough to keep
        // the pipeline fed, slow enough to be invisible.
        _backgroundTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _backgroundTimer.Tick += (_, _) =>
        {
            if (_controller.Worker is null) return;

            // Poll results even if no animation frame is running
            var (gotResults, _, _) = _controller.PollAnalysisResults();
            if (gotResults)
                InvalidateOverlay();

            bool hasWork = _controller.HasBackgroundAnalysisWork;
            bool railActive = _controller.ActiveDocument?.Rail.Active == true;
            if (!railActive && _controller.Worker.IsIdle && hasWork)
                _controller.TrySubmitBackgroundReadAhead();

            if (!hasWork)
                _backgroundTimer?.Stop();
        };
    }

    /// <summary>
    /// Start the background analysis timer if there's work to do.
    /// Called after adding a document.
    /// </summary>
    public void StartBackgroundAnalysis()
    {
        if (_backgroundTimer is not null && !_backgroundTimer.IsEnabled
            && _controller.HasBackgroundAnalysisWork)
            _backgroundTimer.Start();
    }

    private void OnAnimationFrame(TimeSpan frameTime)
    {
        _animationRequested = false;

        // Raw dt from Avalonia's compositor-synchronized timestamp.
        // Capped at 33ms (one frame at 30 fps) so a stall (app backgrounded,
        // system waking from sleep) doesn't produce a huge position jump in
        // snap and zoom animations. Autoscroll is wall-clock based and ignores
        // this value, so the cap only affects those short-lived animations.
        double dt = _lastFrameTime is { } last
            ? Math.Min((frameTime - last).TotalSeconds, 1.0 / 30.0)
            : 1.0 / 60.0;
        _lastFrameTime = frameTime;

        var result = _controller.Tick(dt);

        if (result.PageChanged)
        {
            // A rail/auto-scroll page cross surfaces only here and calls just
            // InvalidatePage, so refresh the per-page search and annotation overlays
            // alongside the page bitmap — otherwise the previous page's rects stay
            // painted over the new page (e.g. page 7's search highlights on page 1).
            InvalidatePage();
            InvalidateSearch();
            InvalidateAnnotations();
        }
        if (result.OverlayChanged)
        {
            InvalidateOverlay();
            OnPropertyChanged(nameof(ActiveTab));
        }
        if (result.AnnotationsChanged) InvalidateAnnotations();
        if (result.CameraChanged) InvalidateCamera();

        bool stillAnimating = result.StillAnimating;
        if (stillAnimating) RequestAnimationFrame();
        // Fire AnimationSettled exactly once on the animating→idle edge. The external
        // control surface (IRailReaderControl.Settled) uses this as its cut/sync backbone;
        // an eased verb returns immediately and the runner waits for this signal.
        else if (_wasAnimating) AnimationSettled?.Invoke();
        _wasAnimating = stillAnimating;
    }

    /// <summary>True while the most recent animation frame reported StillAnimating, so the
    /// next idle frame can raise <see cref="AnimationSettled"/> once on the falling edge.</summary>
    private bool _wasAnimating;

    /// <summary>Raised once when an eased camera animation completes. Drives
    /// <see cref="ControlBus.IRailReaderControl.Settled"/>; fired on the UI thread.</summary>
    public event Action? AnimationSettled;

    public void RequestAnimationFrame()
    {
        if (_pollTimer is not null && !_pollTimer.IsEnabled)
            _pollTimer.Start();

        if (_animationRequested) return;
        _animationRequested = true;
        // RequestAnimationFrame is deprecated in Avalonia 12 in favour of compositor-based
        // animation timers, but those callbacks fire on the composition thread. Our per-frame
        // OnAnimationFrame (Controller.Tick + DocumentState mutation + layer invalidation) must
        // run on the UI thread, so we keep the UI-thread-synced RequestAnimationFrame here.
#pragma warning disable CS0618 // Type or member is obsolete
        _window?.RequestAnimationFrame(OnAnimationFrame);
#pragma warning restore CS0618
    }

    public void InvalidateCanvas()
    {
        InvalidateAll();
        OnPropertyChanged(nameof(ActiveTab));
    }

    public void Dispose() => _controller.Dispose();

    /// <summary>
    /// Fire-and-forget a Task from a UI event handler, logging any fault.
    /// Use this only where the caller is an Avalonia event handler that cannot
    /// return Task (routed event, window.Opened, click handler lambda).
    /// </summary>
    public void FireAndForget(Task task, string context)
    {
        _ = task.ContinueWith(t =>
        {
            if (t.Exception is { } ex)
                _logger.Error($"[{context}] Unhandled task fault", ex);
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    // --- Status toast ---

    [ObservableProperty] private string? _statusToast;
    private Timer? _toastTimer;

    public void ShowStatusToast(string message)
    {
        StatusToast = message;
        _toastTimer?.Dispose();
        _toastTimer = new Timer(_ =>
            Dispatcher.UIThread.Post(() => StatusToast = null),
            null, 1500, Timeout.Infinite);
    }

    // CompositeAnnotationStore.Default is a process-global singleton and its two signals are
    // settable Action properties (not events) — assign once here. A save can complete off the
    // UI thread, so marshal before touching the toast. Desktop-only: the CLI/Export hosts only
    // Load, never Save, so they never raise these.
    private void WireAnnotationStoreSignals()
    {
        CompositeAnnotationStore.Default.OnSidecarFallback = (_, reason) =>
            Dispatcher.UIThread.Post(() => ShowStatusToast(SidecarFallbackMessage(reason)));
        CompositeAnnotationStore.Default.OnSidecarMigration = _ =>
            Dispatcher.UIThread.Post(() => ShowStatusToast(
                "Your private notes will be written into this PDF the next time it is saved."));
    }

    private static string SidecarFallbackMessage(SidecarFallbackReason reason) => reason switch
    {
        SidecarFallbackReason.ReadOnly => "Annotations stored separately — this PDF is read-only.",
        SidecarFallbackReason.Signed => "Annotations stored separately — this PDF is signed.",
        _ => "Annotations stored separately from this PDF.",
    };

    // --- Config ---

    [RelayCommand]
    public void RunCleanup()
    {
        var (removed, freed) = CleanupService.RunCleanup();
        CleanupMessage = CleanupService.FormatReport(removed, freed);
    }

    public void SetDarkMode(bool dark)
    {
        AppConfig.DarkMode = dark;
        AppConfig.Save();
        Avalonia.Application.Current!.RequestedThemeVariant =
            dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
    }

    public void OnConfigChanged()
    {
        _controller.OnConfigChanged(_appConfig.ToCoreSettings());
        _appConfig.Save();
        ApplyFontScale();
        InvalidateAll();
        OnPropertyChanged(nameof(ActiveTab));
    }

    public void OnSliderChanged() => _controller.OnSliderChanged(_appConfig.ToCoreSettings());

    // --- Scan All ---

    /// <summary>True when Scan All can be started (document open, not already scanning).</summary>
    public bool CanStartScanAll => !IsScanAllActive && ActiveTab is not null;

    [RelayCommand(CanExecute = nameof(CanStartScanAll))]
    public void StartScanAll()
    {
        var doc = _controller.ActiveDocument;
        if (doc is null || IsScanAllActive) return;

        // Already fully scanned?
        if (doc.AnalysisCache.Count >= doc.PageCount)
        {
            ShowStatusToast("All pages already scanned");
            return;
        }

        IsScanAllActive = true;
        if (ActiveTab is { } tab) tab.FullScanPeekIndex = null;
        _scanAllOriginalWindowPages = _appConfig.BackgroundAnalysisWindowPages;
        _scanAllLastScanned = doc.AnalysisCache.Count;
        _scanAllStallTicks = 0;

        // Expand to whole-document sweep and re-centre the queue from page 0
        _appConfig.BackgroundAnalysisWindowPages = 0;
        _controller.OnConfigChanged(_appConfig.ToCoreSettings());

        // Reset background queue to start from page 0
        doc.QueueLookahead(0);

        // Fast scan timer: polls results + submits next page at ~20 Hz
        _scanAllTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _scanAllTimer.Tick += OnScanAllTick;
        _scanAllTimer.Start();

        ScanAllProgress = $"Scanning… 0 of {doc.PageCount} pages";
        StartScanAllCommand.NotifyCanExecuteChanged();
    }

    private void OnScanAllTick(object? sender, EventArgs e)
    {
        var doc = _controller.ActiveDocument;
        if (doc is null) { CancelScanAll(); return; }

        // Poll any completed analysis results
        _controller.PollAnalysisResults();

        // Submit next page if worker is idle
        bool workerIdle = _controller.Worker is { IsIdle: true };
        if (workerIdle)
            _controller.TrySubmitBackgroundReadAhead();

        int scanned = doc.AnalysisCache.Count;
        int total = doc.PageCount;
        ScanAllProgress = $"Scanning… {scanned} of {total} pages";

        // Normal completion: every page analyzed.
        if (scanned >= total) { CompleteScanAll(); return; }

        // The queue is drained and the worker is idle, yet some pages are still
        // missing from the cache (e.g. they failed to render/analyze and were
        // dropped — the queue cursor never re-offers them). No further results
        // will ever arrive, so finish with what we have instead of spinning.
        if (workerIdle && !doc.HasPendingBackgroundWork) { CompleteScanAll(); return; }

        // Stall watchdog: if no page has completed for a sustained period — e.g.
        // the analysis worker thread faulted and is wedged (IsIdle stuck false,
        // queue never drains) — give up gracefully rather than locking the modal
        // overlay forever (only Escape would otherwise dismiss it).
        if (scanned > _scanAllLastScanned)
        {
            _scanAllLastScanned = scanned;
            _scanAllStallTicks = 0;
        }
        else if (++_scanAllStallTicks >= ScanAllStallTickLimit)
        {
            _logger.Error($"[ScanAll] Stalled at {scanned} of {total} pages — aborting sweep.");
            CompleteScanAll();
        }
    }

    private void CompleteScanAll() => TeardownScanAll(completed: true);

    public void CancelScanAll()
    {
        if (!IsScanAllActive) return;
        TeardownScanAll(completed: false);
    }

    /// <summary>
    /// Shared teardown for both completion and cancellation: stops the timer,
    /// persists the figure index, restores the analysis window, and clears state.
    /// On completion (not cancellation) it also trims distant page caches and
    /// reports a result toast.
    /// </summary>
    private void TeardownScanAll(bool completed)
    {
        var doc = _controller.ActiveDocument;

        StopScanAllTimer();

        // Capture counts before trimming so the toast reflects the sweep result.
        int scanned = doc?.AnalysisCache.Count ?? 0;
        int total = doc?.PageCount ?? 0;

        // Build the full figure index from whatever was scanned, store per-tab.
        if (doc is not null && ActiveTab is { } tab)
            tab.FullScanPeekIndex = PeekIndexBuilder.Build(doc.AnalysisCache, doc.PageCount);

        // Restore the analysis window the user had before the sweep.
        _appConfig.BackgroundAnalysisWindowPages = _scanAllOriginalWindowPages;
        _controller.OnConfigChanged(_appConfig.ToCoreSettings());

        // On completion, trim distant page caches so the whole-document sweep
        // doesn't leave every page resident. Figure data survives on the tab.
        // Removed pages are re-analyzed on demand when navigated to.
        if (completed && doc is not null)
            TrimDistantAnalysisCache(doc);

        IsScanAllActive = false;
        ScanAllProgress = "";
        StartScanAllCommand.NotifyCanExecuteChanged();

        if (completed)
            ShowStatusToast(scanned >= total
                ? "Scan complete"
                : $"Scan finished — {scanned} of {total} pages (some could not be analysed)");
    }

    private void StopScanAllTimer()
    {
        if (_scanAllTimer is not null)
        {
            _scanAllTimer.Stop();
            _scanAllTimer.Tick -= OnScanAllTick;
            _scanAllTimer = null;
        }
    }

    /// <summary>
    /// Removes analysis cache entries for pages outside the normal analysis window
    /// around the current page. The per-tab <see cref="TabViewModel.FullScanPeekIndex"/>
    /// preserves the figure/equation/table data. Removed pages will be re-analyzed
    /// on demand when the user navigates near them.
    /// </summary>
    private void TrimDistantAnalysisCache(DocumentState doc)
    {
        int window = _scanAllOriginalWindowPages;
        if (window <= 0) return; // whole-document mode: don't trim

        int center = doc.CurrentPage;
        int lo = Math.Max(0, center - window);
        int hi = Math.Min(doc.PageCount - 1, center + window);

        // Downcast from IReadOnlyDictionary to Dictionary to remove entries.
        // The runtime type is Dictionary<int, PageAnalysis> (DocumentState._analysisCache).
        // If a future Core version wraps the cache, log it loudly rather than
        // silently skipping the trim — otherwise a whole-document sweep would
        // leave every page's analysis resident with no visible symptom.
        if (doc.AnalysisCache is not Dictionary<int, PageAnalysis> mutable)
        {
            _logger.Error(
                $"[ScanAll] AnalysisCache is {doc.AnalysisCache.GetType().Name}, not a mutable " +
                "Dictionary — cannot trim distant pages; analysis memory will not be reclaimed.");
            return;
        }

        var keysToRemove = new List<int>();
        foreach (var kvp in mutable)
        {
            if (kvp.Key < lo || kvp.Key > hi)
                keysToRemove.Add(kvp.Key);
        }

        foreach (var key in keysToRemove)
            mutable.Remove(key);
    }

    private const double BaseFontSize = 14.0;

    private void ApplyFontScale()
    {
        if (_window is not null)
            _window.FontSize = BaseFontSize * AppConfig.UiFontScale;
    }

    public double CurrentFontSize => BaseFontSize * AppConfig.UiFontScale;

    public void SetViewportSize(double w, double h) => _controller.SetViewportSize(w, h);

    // --- Invalidation helpers ---

    /// <summary>
    /// Common pattern: call a controller method, invalidate, optionally request animation.
    /// </summary>
    private void Dispatch(Action action, Action? invalidate = null, bool animate = false)
    {
        // Scan All is modal: every navigation/camera action routed through Dispatch
        // is suppressed for the scan's duration. Entry points that don't go through
        // Dispatch (NavigateBack/Forward, arrow Left/Right, HandleClick) carry their
        // own IsScanAllActive guard.
        if (IsScanAllActive) return;
        action();
        invalidate?.Invoke();
        if (animate) RequestAnimationFrame();
    }

    private void InvalidateCamera() => _invalidation?.InvalidateCamera?.Invoke();
    private void InvalidatePage() => _invalidation?.InvalidatePage?.Invoke();
    private void InvalidateOverlay() => _invalidation?.InvalidateOverlay?.Invoke();

    /// <summary>Raised whenever the search layer is invalidated (match navigation, page
    /// change, results finalised) so the Search pane refreshes its "N of M" display.</summary>
    public event Action? SearchInvalidated;
    private void InvalidateSearch()
    {
        _invalidation?.InvalidateSearch?.Invoke();
        SearchInvalidated?.Invoke();
    }

    private void InvalidateAnnotations() => _invalidation?.InvalidateAnnotations?.Invoke();

    private void InvalidateAfterNavigation()
    {
        OnPropertyChanged(nameof(ActiveTab));
        InvalidateAll();
        RequestAnimationFrame();
    }

    private void InvalidateCameraAndTab()
    {
        InvalidateCamera();
        OnPropertyChanged(nameof(ActiveTab));
    }

    private void InvalidateNavigation()
    {
        InvalidateCamera();
        InvalidateOverlay();
        InvalidatePage();
        OnPropertyChanged(nameof(ActiveTab));
        RequestAnimationFrame();
    }

    private void InvalidateAll()
    {
        InvalidateCamera();
        InvalidatePage();
        InvalidateOverlay();
        InvalidateSearch();
        InvalidateAnnotations();
    }

    public void RequestCameraUpdate() => InvalidateCamera();
}

public sealed class InvalidationCallbacks
{
    public Action? InvalidateCamera { get; init; }
    public Action? InvalidatePage { get; init; }
    public Action? InvalidateOverlay { get; init; }
    public Action? InvalidateSearch { get; init; }
    public Action? InvalidateAnnotations { get; init; }

    /// <summary>Tell the document viewport's accessibility peer to re-evaluate and announce its state.
    /// Driven by Core's PageChanged / ReadingPositionChanged callbacks.</summary>
    public Action? AnnounceAccessibility { get; init; }
}
