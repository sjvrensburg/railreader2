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

/// <summary>The side-panel tabs, used by ShowPane for menu-driven pane navigation. The enum order
/// matches the accordion's grid-row order (see <c>OutlinePanel</c>), so a section's row is just
/// <c>(int)Pane</c> — keep new panes appended.</summary>
public enum SidePane { Outline, Bookmarks, Index, Search, Comments, Portals }

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

    // Last observed semi-auto park state, to fire AutoScrollParked notifications on transitions.
    // Core parks mid-Tick without raising StateChanged, so the poll loop watches for the edge.
    private bool _lastAutoScrollParked;

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

    // --- Multi-viewport pane commands (raised to MainWindow, which owns the pane / window views) ---

    /// <summary>Add a split pane to the right of the focused one (VS Code "Split Right").</summary>
    public event Action? SplitRightRequested;
    /// <summary>Close the focused split pane / tear-off window (the primary pane can't be closed).</summary>
    public event Action? CloseSurfaceRequested;
    /// <summary>Move the focused split pane into its own floating window (or open a new window).</summary>
    public event Action? MoveSurfaceToWindowRequested;
    /// <summary>Collapse back to a single primary pane (close every extra pane + tear-off window).</summary>
    public event Action? CloseExtraSurfacesRequested;

    /// <summary>Whether viewport splitting / tear-off is available (a document must be open).</summary>
    public bool CanSplitViewport => ActiveTab is not null;

    public void RequestSplitRight() { if (ActiveTab is not null) SplitRightRequested?.Invoke(); }
    public void RequestCloseSurface() => CloseSurfaceRequested?.Invoke();
    public void RequestMoveSurfaceToWindow() { if (ActiveTab is not null) MoveSurfaceToWindowRequested?.Invoke(); }
    public void RequestCloseExtraSurfaces() => CloseExtraSurfacesRequested?.Invoke();

    [ObservableProperty] private bool _showMinimap;

    /// <summary>When armed (via the toolbar's "Start rail here" toggle), the next viewport click
    /// force-activates rail mode at that point regardless of zoom — see <see cref="ActivateRailAtClick"/>.
    /// Consumed (reset to false) by that click, by Escape, or by toggling the button off.</summary>
    [ObservableProperty] private bool _armActivateRailClick;

    /// <summary>When set (via a Freeze-mode control), the pointer shows the matching guide line(s) and
    /// the next viewport click drops the freeze split there — see <see cref="PlaceFreeze"/>. Cleared by
    /// that click, by Escape, by re-picking the same mode, or on tab switch.</summary>
    [ObservableProperty] private FreezeMode _freezeArmMode;

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
    /// <summary>Search matches on a specific page (or null). Lets each viewport surface render its OWN
    /// page's highlights — a split pane / tear-off can sit on a different page than the focused view,
    /// for which <see cref="CurrentPageSearchMatches"/> (the focused view's page) would be wrong.</summary>
    public IReadOnlyList<SearchMatch>? MatchesForPage(int page) => _controller.Search.MatchesForPage(page);
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

    // --- Menu-item enablement gating (so impossible actions grey out) ---

    /// <summary>True when a document tab is open. Document-dependent menu items gate on this.</summary>
    public bool HasDocument => ActiveTab is not null;

    /// <summary>True when the active document is encrypted/password-protected. Flattened annotated
    /// export refuses an encrypted source, so "Export with Annotations" gates this out.</summary>
    public bool IsActiveDocumentEncrypted => !string.IsNullOrEmpty(ActiveTab?.Pdf.Password);

    /// <summary>"Export with Annotations" (flattened PDF) is possible only for an unencrypted open
    /// document — the annotation export service refuses an encrypted source.</summary>
    public bool CanExportAnnotated => HasDocument && !IsActiveDocumentEncrypted;

    /// <summary>Block copy-as-LaTeX/Markdown/Description require a configured VLM endpoint (and an
    /// open document). Copy-as-Image needs neither a VLM (it is a local crop) — it gates on <see cref="HasDocument"/>.</summary>
    public bool CanVlmCopyBlock => HasDocument && !string.IsNullOrWhiteSpace(AppConfig.VlmEndpoint);

    /// <summary>True when semi-auto scroll has parked on a stop unit (equation / table / figure /
    /// heading, or a column / page break) and is waiting for an explicit advance keypress. Drives
    /// the "parked — press D" affordance. Transitions are detected during the animation poll (Core
    /// parks mid-Tick and does not raise StateChanged for it), see <see cref="OnAnimationFrame"/>.</summary>
    public bool AutoScrollParked => _controller.AutoScrollParked;
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
        // Push-based reading-context updates: Core fires these (on the UI thread) the moment the page
        // or rail reading position changes — including jumps that don't otherwise repaint the overlay
        // (e.g. NavigateToRole). Phase 3 removed the controller-level facades, so these are wired
        // per-viewport on the FOCUSED view (in WireFocusedSignals, re-pointed by FocusSurface) — the
        // old facade only ever fired for the focused view, so this is equivalent.
        WireAnnotationStoreSignals();
        SetupPollTimer();
    }

    // Last-published menu-gating values, so a spurious ActiveTab raise re-publishes nothing.
    private bool _gateHasDocument, _gateEncrypted, _gateCanExport, _gateCanVlm;

    /// <summary>Menu-enablement gating (<see cref="HasDocument"/>, <see cref="CanExportAnnotated"/>,
    /// <see cref="CanVlmCopyBlock"/>) all derive from the active tab — open/close/switch, encryption —
    /// and the VLM config. Every one of those points re-raises <see cref="ActiveTab"/> (tab ops and
    /// <see cref="OnConfigChanged"/> included), so refresh the gating flags alongside it rather than
    /// touching each call site. <see cref="ActiveTab"/> is also re-raised on every animation frame
    /// (overlay refresh), so only re-publish a flag whose value actually changed — otherwise auto-scroll
    /// would needlessly re-evaluate every menu item's IsEnabled binding ~60×/s.</summary>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName != nameof(ActiveTab)) return;

        if (HasDocument is var hasDoc && hasDoc != _gateHasDocument)
        { _gateHasDocument = hasDoc; OnPropertyChanged(nameof(HasDocument)); }
        if (IsActiveDocumentEncrypted is var enc && enc != _gateEncrypted)
        { _gateEncrypted = enc; OnPropertyChanged(nameof(IsActiveDocumentEncrypted)); }
        if (CanExportAnnotated is var canExport && canExport != _gateCanExport)
        { _gateCanExport = canExport; OnPropertyChanged(nameof(CanExportAnnotated)); }
        if (CanVlmCopyBlock is var canVlm && canVlm != _gateCanVlm)
        { _gateCanVlm = canVlm; OnPropertyChanged(nameof(CanVlmCopyBlock)); }
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

    // --- Viewport surfaces (multi-viewport: split panes + tear-off windows over one document) ---

    // Every live renderable surface (the primary docked pane + any extra split panes + tear-off
    // windows). The frame loop ticks each one's viewport independently; document-wide invalidations
    // broadcast to all. Always contains at least the primary DocumentView once the window is loaded.
    private readonly List<IViewportSurface> _surfaces = new();

    // Per-frame scratch: each live surface and its TickResult, reused so the hot loop allocates nothing.
    private readonly List<(IViewportSurface Surface, TickResult Result)> _tickScratch = new();
    // Per-frame snapshot of _surfaces, so a mid-frame (un)registration (the portal surface opening/
    // closing from EvaluatePortals) can't invalidate the tick enumeration. Reused to avoid per-frame alloc.
    private readonly List<IViewportSurface> _surfaceSnapshot = new();

    /// <summary>All live viewport surfaces (read-only). Consumed by the host's broadcast invalidation.</summary>
    public IReadOnlyList<IViewportSurface> Surfaces => _surfaces;

    /// <summary>True when more than one viewport surface is live — splitting or a tear-off is active.</summary>
    public bool HasMultipleSurfaces => _surfaces.Count > 1;

    public void RegisterSurface(IViewportSurface surface)
    {
        if (_surfaces.Contains(surface)) return;
        _surfaces.Add(surface);
        UpdateSurfaceFocusVisuals();
    }

    public void UnregisterSurface(IViewportSurface surface)
    {
        if (_surfaces.Remove(surface))
            UpdateSurfaceFocusVisuals();
    }

    /// <summary>The surface whose viewport is the controller's focused one (where input is routed), or null.</summary>
    public IViewportSurface? FocusedSurface
    {
        get
        {
            var vp = _controller.FocusedViewport;
            foreach (var s in _surfaces)
                if (ReferenceEquals(s.SurfaceViewport, vp)) return s;
            return null;
        }
    }

    /// <summary>Make <paramref name="vp"/> the focused viewport — all keyboard/scroll/menu commands then
    /// act on this surface (Core routes host input through <c>FocusedViewport</c>). Idempotent.</summary>
    public void FocusSurface(IViewportSurface surface, Viewport vp, bool wireReadingSignals = true)
    {
        // Re-point the per-viewport reading-context subscription to the now-focused view (replaces the
        // removed controller-level PageChanged/ReadingPositionChanged facades — Phase 3). Done before
        // the already-focused early-return so the very first focus (primary on open) gets wired too.
        // The confined portal viewport opts OUT (wireReadingSignals:false): it takes INPUT focus for its
        // toolbar/keys, but a11y announcements + the portal pin loop must keep tracking the MAIN reading
        // view, not the satellite — otherwise focusing the pop-out hijacks both (its
        // PageChanged/ReadingPositionChanged would drive OnReadingContextChanged). This realises the
        // intent already documented at OnPortalPointerPressed ("the reading-position sync is unaffected").
        if (wireReadingSignals) WireFocusedSignals(vp);
        if (ReferenceEquals(_controller.FocusedViewport, vp)) return;
        _controller.FocusedViewport = vp;
        // The controller's ambient size (input geometry) now tracks this surface.
        var (w, h) = surface.SurfaceSize;
        if (w > 0 && h > 0) _controller.FocusedViewport?.SetSize(w, h);
        UpdateSurfaceFocusVisuals();
        // The status bar / menu gating / rail toolbar reflect the focused view.
        InvalidateCamera();
        OnPropertyChanged(nameof(ActiveTab));
        // Freeze is per-viewport — the toolbar Freeze button's state/enable reflects the newly-focused view.
        OnPropertyChanged(nameof(IsFrozen));
        OnPropertyChanged(nameof(CanFreeze));
    }

    /// <summary>Focus the registered surface that currently renders <paramref name="vp"/> — used on a
    /// tab switch/open to route focus to the (active) tab's own viewport. Falls back to setting
    /// controller focus directly if no surface renders it yet. Idempotent.</summary>
    public void FocusViewport(Viewport vp)
    {
        RefreshTabLiveness();
        foreach (var s in _surfaces)
            if (ReferenceEquals(s.SurfaceViewport, vp))
            {
                FocusSurface(s, vp);
                return;
            }
        _controller.FocusedViewport = vp;
        WireFocusedSignals(vp);
        UpdateSurfaceFocusVisuals();
    }

    /// <summary>Keep each tab's own viewport live only while its tab is the active one (shown in the
    /// Document pane). Inactive tabs' viewports go non-live so RailReaderCore 0.44.0 (#77) can evict
    /// their page caches (<c>AnyViewportNeeds</c> honours <c>IsLive</c>) and skip them for
    /// reading-position persistence — resolving decision-#1 limitation (c). Secondary surface viewports
    /// (split panes / tear-offs) own their own <c>IsLive</c> and are never a tab's viewport, so they are
    /// untouched here. Cheap + idempotent: just toggles a flag, safe to call on every focus change.</summary>
    private void RefreshTabLiveness()
    {
        var active = ActiveTab;
        foreach (var t in Tabs)
            t.Viewport.IsLive = ReferenceEquals(t, active);
    }

    /// <summary>Remove <paramref name="vp"/> from whichever open document still owns it. Iterating the
    /// open tabs (all public API) keeps this safe whether the document is alive (frees the viewport now)
    /// or was already disposed (its model isn't found among the tabs → no-op, no double-free). Avoids
    /// touching the internal <c>Viewport.Owner</c>/<c>IsDisposed</c>. Shared by the secondary-pane
    /// teardown and the live portal viewport.</summary>
    internal void SafeRemoveViewport(Viewport vp)
    {
        foreach (var tab in Tabs)
            if (tab.State.Viewports.Contains(vp))
            {
                tab.State.RemoveViewport(vp);
                return;
            }
    }

    // The viewport whose PageChanged/ReadingPositionChanged currently drive OnReadingContextChanged.
    private Viewport? _signalWiredViewport;

    /// <summary>Subscribe the focused viewport's reading-context events to <see cref="OnReadingContextChanged"/>,
    /// unsubscribing the previously-focused one. Idempotent. Replaces the controller-level event facade
    /// removed in Phase 3 — the facade only ever fired for the focused view.</summary>
    private void WireFocusedSignals(Viewport vp)
    {
        if (ReferenceEquals(_signalWiredViewport, vp)) return;
        if (_signalWiredViewport is { } prev)
        {
            prev.PageChanged -= OnFocusedViewportPageChanged;
            prev.ReadingPositionChanged -= OnFocusedViewportReadingPositionChanged;
        }
        _signalWiredViewport = vp;
        vp.PageChanged += OnFocusedViewportPageChanged;
        vp.ReadingPositionChanged += OnFocusedViewportReadingPositionChanged;
    }

    private void OnFocusedViewportPageChanged(int _) => OnReadingContextChanged();
    private void OnFocusedViewportReadingPositionChanged(ReadingPosition _) => OnReadingContextChanged();

    /// <summary>Drop the focused-viewport reading-context subscription, releasing the strong reference
    /// the wiring holds. Needed when no viewport replaces it (last tab closed / shutdown) — otherwise
    /// <see cref="_signalWiredViewport"/> would keep a disposed viewport (and, via Viewport.Owner, its
    /// whole DocumentModel + PDFium handle) alive until the next document opens.</summary>
    private void UnwireFocusedSignals()
    {
        if (_signalWiredViewport is { } prev)
        {
            prev.PageChanged -= OnFocusedViewportPageChanged;
            prev.ReadingPositionChanged -= OnFocusedViewportReadingPositionChanged;
        }
        _signalWiredViewport = null;
    }

    /// <summary>Light up the focused pane's border, but only when more than one surface is live (a lone
    /// viewport has no focus ambiguity, so it shows no border).</summary>
    public void UpdateSurfaceFocusVisuals()
    {
        bool multi = _surfaces.Count > 1;
        var focused = _controller.FocusedViewport;
        foreach (var s in _surfaces)
            s.SetFocusedVisual(multi && ReferenceEquals(s.SurfaceViewport, focused));
    }

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

    /// <summary>The reading context (current page / rail line) changed: re-announce accessibility state
    /// and re-evaluate which portal the reading position is inside. Driven by Core's PageChanged /
    /// ReadingPositionChanged callbacks — the single place those two converge.</summary>
    private void OnReadingContextChanged()
    {
        AnnounceAccessibilityState();
        EvaluatePortals();
        // A page change can add/remove tables (CanFreeze) or take the focused view off its frozen page
        // (IsFrozen auto-clears in GetFreezeTiles) — keep the Freeze toggle's label/enable in sync.
        OnPropertyChanged(nameof(IsFrozen));
        OnPropertyChanged(nameof(CanFreeze));
    }

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
            // Only force a portal re-evaluation when something is still waiting on analysis (a pinned
            // target's page, or an automatic reference's caption page) — otherwise the
            // reading-position callbacks + memo already cover the steady case, and forcing on every
            // unrelated analysis result would defeat the fast path.
            EvaluatePortals(forceRender: gotResults && PortalResolvePending);
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
            // As above: force only when a pinned target or auto reference is still resolving, so
            // background read-ahead (one result per analysed page) doesn't bypass the memo on every page.
            EvaluatePortals(forceRender: gotResults && PortalResolvePending);

            bool hasWork = _controller.HasBackgroundAnalysisWork;
            bool railActive = _controller.FocusedViewport?.Owner?.Rail.Active == true;
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

    private bool _inAnimationFrame;

    /// <summary>True while the per-frame <c>OnAnimationFrame</c> is executing. The host defers
    /// structural portal-surface changes (register/host/teardown) when set — doing them mid-frame
    /// re-enters / mutates the surface list — but applies user-initiated ones (a peek) synchronously
    /// when clear, so they take effect before the next frame's portal evaluation can revise them.</summary>
    public bool IsInAnimationFrame => _inAnimationFrame;

    private void OnAnimationFrame(TimeSpan frameTime)
    {
        // Re-entrancy guard: a per-frame property change (e.g. ActiveTab → portal-view sync, or a
        // window Show pumping the dispatcher) must never nest a second frame — that would Clear/refill
        // the shared _tickScratch mid-enumeration ("Collection was modified"). A skipped nested frame is
        // harmless: the outer frame re-arms RequestAnimationFrame when anything is still animating.
        if (_inAnimationFrame) return;
        _inAnimationFrame = true;
        try { RunAnimationFrame(frameTime); }
        finally { _inAnimationFrame = false; }
    }

    private void RunAnimationFrame(TimeSpan frameTime)
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

        // Multi-viewport frame: drain the analysis worker ONCE for the whole document (not per
        // view), then advance each live surface's own viewport and apply its own TickResult to that
        // surface's layers. Core 0.41.0 ticks/clamps/snaps and seats each viewport against its OWN
        // Viewport.Width/Height (kept current by DocumentView's vp.SetSize), so no ambient-size swap
        // is needed here — the single-surface path is byte-identical to the old Tick(dt).
        bool anyAnimating = false;
        var focused = _controller.FocusedViewport;
        _tickScratch.Clear();
        // Iterate a snapshot: TickViewport fires ReadingPositionChanged → EvaluatePortals, which can
        // synchronously open/close the pop-out and (un)register the portal surface mid-frame. The
        // snapshot keeps this enumeration valid; a surface registered this frame ticks the next one.
        _surfaceSnapshot.Clear();
        _surfaceSnapshot.AddRange(_surfaces);
        foreach (var surface in _surfaceSnapshot)
        {
            if (surface.SurfaceViewport is not { } vp) continue;
            var r = _controller.TickViewport(vp, dt, pumpAnalysis: false);
            // A frozen view's rail snap / auto-scroll re-aims the camera each frame (incl. the horizontal
            // snap to the line start); clamp it back so the body can't slide left of / above the frozen
            // panes and reveal the row labels / header behind them.
            ClampFrozenCamera(vp);
            _tickScratch.Add((surface, r));
            anyAnimating |= r.StillAnimating;
        }

        // One document-global analysis pump, quiescent only when nothing is animating (the gate the
        // single-view Tick(dt) applied to the focused view).
        var (gotResults, _, gotPageChange) = _controller.PumpAnalysis(quiescent: !anyAnimating);

        foreach (var (surface, r) in _tickScratch)
        {
            // A just-arrived analysis result (gotResults) can seat any view's rail, and an analysis
            // page change (gotPageChange) is document-global — so fold them into every surface.
            bool pageChanged = r.PageChanged || gotPageChange;
            bool overlayChanged = r.OverlayChanged || gotResults;
            bool isFocused = ReferenceEquals(surface.SurfaceViewport, focused);
            if (pageChanged)
            {
                // A page cross calls just RenderPage, so refresh this surface's per-page search and
                // annotation overlays alongside the page bitmap (otherwise the previous page's rects
                // stay painted over the new page).
                surface.RenderPage();
                surface.RenderSearch();
                surface.RenderAnnotations();
            }
            if (overlayChanged) surface.RenderOverlay();
            if (r.AnnotationsChanged) surface.RenderAnnotations();
            if (r.CameraChanged)
            {
                surface.RenderCamera();
                if (isFocused) _invalidation?.UpdateZoomDisplay?.Invoke();
            }
            // The focused surface drives the status bar + menu/rail-toolbar gating, which read
            // FocusedViewport — refresh them when its page or overlay changed (so a focused split pane /
            // tear-off advancing its OWN page updates the page/zoom/rail readout, not just the primary's).
            if (isFocused && (overlayChanged || pageChanged)) OnPropertyChanged(nameof(ActiveTab));
        }
        _tickScratch.Clear();

        // Semi-auto scroll parks mid-Tick (no StateChanged for it), so watch the edge here and
        // surface it so the "parked — press D" affordance and status bar update.
        bool parked = _controller.AutoScrollParked;
        if (parked != _lastAutoScrollParked)
        {
            _lastAutoScrollParked = parked;
            OnPropertyChanged(nameof(AutoScrollParked));
        }

        if (anyAnimating) RequestAnimationFrame();
    }

    public void RequestAnimationFrame()
    {
        if (_pollTimer is not null && !_pollTimer.IsEnabled)
            _pollTimer.Start();

        if (_animationRequested) return;
        _animationRequested = true;
        // RequestAnimationFrame is deprecated in Avalonia 12 in favour of compositor-based
        // animation timers, but those callbacks fire on the composition thread. Our per-frame
        // OnAnimationFrame (Controller.Tick + DocumentModel mutation + layer invalidation) must
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

    public void Dispose()
    {
        UnwireFocusedSignals();
        DisposePortalImages();
        DisposeFreezeImages();
        _controller.Dispose();
    }

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

    // How long a status toast stays on screen. Long enough to read a short sentence to completion
    // (the previous 1.5s clipped messages mid-read); the timer is reset on each new toast, so rapid
    // toggles still just show the latest.
    private const int ToastDurationMs = 4000;

    public void ShowStatusToast(string message)
    {
        // Already showing this exact message → keep it as-is. Without this, a repeated trigger (e.g. every
        // scroll-wheel notch while zoom is frozen-locked) would dispose+recreate the timer on each call,
        // churning timers and re-arming the same toast indefinitely.
        if (message == StatusToast) return;
        StatusToast = message;
        _toastTimer?.Dispose();
        _toastTimer = new Timer(_ =>
            Dispatcher.UIThread.Post(() => StatusToast = null),
            null, ToastDurationMs, Timeout.Infinite);
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
        var doc = _controller.FocusedViewport?.Owner;
        if (doc is null || IsScanAllActive) return;

        // Already fully scanned?
        if (doc.AnalysedPageCount >= doc.PageCount)
        {
            ShowStatusToast("All pages already scanned");
            return;
        }

        IsScanAllActive = true;
        if (ActiveTab is { } tab) tab.FullScanPeekIndex = null;
        _scanAllOriginalWindowPages = _appConfig.BackgroundAnalysisWindowPages;
        _scanAllLastScanned = doc.AnalysedPageCount;
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
        var doc = _controller.FocusedViewport?.Owner;
        if (doc is null) { CancelScanAll(); return; }

        // Poll any completed analysis results
        _controller.PollAnalysisResults();

        // Submit next page if worker is idle
        bool workerIdle = _controller.Worker is { IsIdle: true };
        if (workerIdle)
            _controller.TrySubmitBackgroundReadAhead();

        int scanned = doc.AnalysedPageCount;
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
        var doc = _controller.FocusedViewport?.Owner;

        StopScanAllTimer();

        // Capture counts before trimming so the toast reflects the sweep result.
        int scanned = doc?.AnalysedPageCount ?? 0;
        int total = doc?.PageCount ?? 0;

        // Build the full figure index from whatever was scanned, store per-tab.
        if (doc is not null && ActiveTab is { } tab)
            tab.FullScanPeekIndex = PeekIndexBuilder.Build(doc.CanonicalAnalyses, doc.PageCount);

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
    private void TrimDistantAnalysisCache(DocumentModel doc)
    {
        int window = _scanAllOriginalWindowPages;
        if (window <= 0) return; // whole-document mode: don't trim

        int center = doc.CurrentPage;
        int lo = Math.Max(0, center - window);
        int hi = Math.Min(doc.PageCount - 1, center + window);

        // Core owns the (page, params)-keyed analysis cache now (Phase 3) — ask it to drop every
        // page outside the keep-window. Replaces the old downcast-to-mutable-Dictionary hack.
        doc.EvictAnalysisOutside(lo, hi);
    }

    private const double BaseFontSize = 14.0;

    private void ApplyFontScale()
    {
        if (_window is not null)
            _window.FontSize = BaseFontSize * AppConfig.UiFontScale;
        // Lets MainWindow forward the new scale to windows it owns (the portal pop-out).
        OnPropertyChanged(nameof(CurrentFontSize));
    }

    public double CurrentFontSize => BaseFontSize * AppConfig.UiFontScale;

    // Phase 3: Core no longer keeps an ambient size — each viewport is sized via Viewport.SetSize.
    // This sizes the focused viewport (the surface the user is acting on); a DocumentView sizes its
    // own viewport directly in its layout handler.
    public void SetViewportSize(double w, double h) => _controller.FocusedViewport?.SetSize(w, h);

    /// <summary>The focused viewport's size, or (0,0) if none — replaces the removed
    /// <c>controller.GetViewportSize()</c> ambient accessor (Phase 3).</summary>
    private (double, double) FocusedViewportSize()
        => _controller.FocusedViewport is { } v ? (v.Width, v.Height) : (0.0, 0.0);

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
    internal void InvalidatePortalMarkers() => _invalidation?.InvalidatePortalMarkers?.Invoke();

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
        OnPropertyChanged(nameof(CanFreeze));
    }

    private void InvalidateNavigation()
    {
        InvalidateCamera();
        InvalidateOverlay();
        InvalidatePage();
        OnPropertyChanged(nameof(ActiveTab));
        OnPropertyChanged(nameof(CanFreeze));
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

    /// <summary>Re-render the portal marker overlay (accent moved / portal added or removed).</summary>
    public Action? InvalidatePortalMarkers { get; init; }

    /// <summary>Tell the document viewport's accessibility peer to re-evaluate and announce its state.
    /// Driven by Core's PageChanged / ReadingPositionChanged callbacks.</summary>
    public Action? AnnounceAccessibility { get; init; }

    /// <summary>Refresh the focused viewport's zoom % in the status bar, without re-rendering any
    /// surface. The per-frame loop calls this when the focused surface's camera changed (the broadcast
    /// <see cref="InvalidateCamera"/> already folds the same status-bar update in).</summary>
    public Action? UpdateZoomDisplay { get; init; }
}
