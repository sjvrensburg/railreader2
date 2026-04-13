using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;

namespace RailReader2.ViewModels;

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

    [ObservableProperty] private bool _showMinimap;
    [ObservableProperty] private bool _showSettings;
    [ObservableProperty] private bool _showAbout;
    [ObservableProperty] private bool _showShortcuts;
    [ObservableProperty] private bool _showGoToPage;
    [ObservableProperty] private string? _cleanupMessage;

    [ObservableProperty] private bool _isFullScreen;
    [ObservableProperty] private bool _showFullScreenHeader;
    [ObservableProperty] private bool _showFullScreenFooter;
    [ObservableProperty] private bool _isRadialMenuOpen;
    [ObservableProperty] private bool _showBookmarkDialog;
    [ObservableProperty] private double _radialMenuX;
    [ObservableProperty] private double _radialMenuY;

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

    public AppConfig Config => _controller.Config;
    public ColourEffectShaders ColourEffects { get; }
    public DocumentController Controller => _controller;

    public TabViewModel? ActiveTab =>
        ActiveTabIndex >= 0 && ActiveTabIndex < Tabs.Count ? Tabs[ActiveTabIndex] : null;

    /// <summary>Path to the current session log file, or null if file logging unavailable.</summary>
    public string? LogFilePath => _logger.LogFilePath;

    public MainWindowViewModel(AppConfig config, ILogger? logger = null)
    {
        _logger = logger ?? AppConfig.Logger;
        ColourEffects = new ColourEffectShaders(_logger);
        _controller = new DocumentController(config, new AvaloniaThreadMarshaller(),
            new RailReader.Renderer.Skia.SkiaPdfServiceFactory(), _logger);
        try { _controller.InitializeWorker(); }
        catch (FileNotFoundException) { /* ONNX model not found — layout analysis disabled */ }
        _controller.StateChanged += OnControllerStateChanged;
        _controller.StatusMessage += ShowStatusToast;
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

    // --- Poll timer & animation ---

    private void SetupPollTimer()
    {
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _pollTimer.Tick += (_, _) =>
        {
            if (_animationRequested) return;

            var (gotResults, needsAnim) = _controller.PollAnalysisResults();
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
            var (gotResults, _) = _controller.PollAnalysisResults();
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

        if (result.PageChanged) InvalidatePage();
        if (result.OverlayChanged)
        {
            InvalidateOverlay();
            OnPropertyChanged(nameof(ActiveTab));
        }
        if (result.AnnotationsChanged) InvalidateAnnotations();
        if (result.CameraChanged) InvalidateCamera();
        if (result.StillAnimating) RequestAnimationFrame();
    }

    public void RequestAnimationFrame()
    {
        if (_pollTimer is not null && !_pollTimer.IsEnabled)
            _pollTimer.Start();

        if (_animationRequested) return;
        _animationRequested = true;
        _window?.RequestAnimationFrame(OnAnimationFrame);
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
        }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
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

    // --- Config ---

    [RelayCommand]
    public void RunCleanup()
    {
        var (removed, freed) = CleanupService.RunCleanup();
        CleanupMessage = CleanupService.FormatReport(removed, freed);
    }

    public void SetDarkMode(bool dark)
    {
        Config.DarkMode = dark;
        Config.Save();
        Avalonia.Application.Current!.RequestedThemeVariant =
            dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
    }

    public void OnConfigChanged()
    {
        _controller.OnConfigChanged();
        ApplyFontScale();
        InvalidateAll();
        OnPropertyChanged(nameof(ActiveTab));
    }

    public void OnSliderChanged() => _controller.OnSliderChanged();

    private const double BaseFontSize = 14.0;

    private void ApplyFontScale()
    {
        if (_window is not null)
            _window.FontSize = BaseFontSize * Config.UiFontScale;
    }

    public double CurrentFontSize => BaseFontSize * Config.UiFontScale;

    public void SetViewportSize(double w, double h) => _controller.SetViewportSize(w, h);

    // --- Invalidation helpers ---

    /// <summary>
    /// Common pattern: call a controller method, invalidate, optionally request animation.
    /// </summary>
    private void Dispatch(Action action, Action? invalidate = null, bool animate = false)
    {
        action();
        invalidate?.Invoke();
        if (animate) RequestAnimationFrame();
    }

    private void InvalidateCamera() => _invalidation?.InvalidateCamera?.Invoke();
    private void InvalidatePage() => _invalidation?.InvalidatePage?.Invoke();
    private void InvalidateOverlay() => _invalidation?.InvalidateOverlay?.Invoke();
    private void InvalidateSearch() => _invalidation?.InvalidateSearch?.Invoke();
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
}
