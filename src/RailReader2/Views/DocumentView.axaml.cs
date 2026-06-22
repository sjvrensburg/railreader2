using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Renderer.Skia;
using RailReader2.ViewModels;
using SkiaSharp;
// The x:Name="Viewport" control (ViewportPanel) collides with the Core Viewport type;
// alias the type so per-view references are unambiguous.
using CoreViewport = RailReader.Core.Viewport;

namespace RailReader2.Views;

/// <summary>
/// Self-contained PDF viewport: the layered <see cref="ViewportPanel"/> + composition
/// layers, minimap, annotation toolbar, and scan-all overlay, plus all per-document
/// render-state building and invalidation. Renders a given <see cref="TabViewModel"/>
/// (its <see cref="Tab"/>); global state (config, colour effects, viewport size,
/// animation scheduling) comes from <see cref="MainWindowViewModel"/> via <c>Shared</c>.
///
/// This was extracted verbatim from MainWindow so a DocumentView can later be instantiated
/// per Dock document; behaviour for the single-active case is unchanged.
/// </summary>
public partial class DocumentView : UserControl, IViewportSurface
{
    private MainWindowViewModel? _shared;
    private TabViewModel? _tab;
    // True when _images is owned by this view (a detached/secondary surface created its own) and
    // must be disposed on rebind/teardown; false when borrowed from _tab.Images (the primary
    // surface shares the document's images with the minimap and must NOT dispose them).
    private bool _ownsImages;
    // A freshly-bound secondary viewport that had no layout size yet: centre it on first SizeChanged.
    private bool _pendingCenter;
    // The viewport this surface renders. Today always the tab's Primary view; a split-pane /
    // tear-off surface will render a detached viewport of the same document (multi-viewport).
    // Per-view geometry (camera/rail/page/dims) is read from here; model + focused state
    // (annotations, analysis cache, selection, search, display prefs) stays on _tab / _shared.
    private CoreViewport? _viewport;
    // Per-view GPU-image lifecycle for _viewport (shared with the minimap via _tab.Images
    // while _viewport is the Primary view; a detached surface gets its own).
    private ViewportImages? _images;
    private bool _wired;

    // Minimap-redraw throttle: skip imperceptible sub-pixel indicator moves.
    private double _lastMinimapOx;
    private double _lastMinimapOy;
    private double _lastMinimapZoom;
    private SKImage? _lastMinimapImage;

    // Cache for the z-ordered annotation list. The sort only changes when the
    // page's annotation set changes (add/remove); pan/zoom frames reuse it.
    private int _annoSortPage = -1;
    private object? _annoSortSource;
    private int _annoSortCount = -1;
    private List<Annotation>? _annoSortResult;

    // Cache for the active search-match local index. Only changes on match
    // navigation or page change, not while the camera moves.
    private int _searchIdxPage = -1;
    private int _searchIdxActive = int.MinValue;
    private object? _searchIdxMatches;
    private int _searchIdxResult = -1;

    // Cache for the page-space portal marker list. Marker positions depend only on page + portal set;
    // the accent depends on the displayed portal. So rebuild only when (page, count, displayed) change —
    // camera-only frames reuse it and just re-send a fresh matrix.
    private List<PortalMarkerInfo>? _markerCache;
    private TabViewModel? _markerTab;       // cache is per-document: two tabs can share (page,count,displayed)
    private int _markerPage = int.MinValue;
    private int _markerCount = -1;
    private string? _markerDisplayed = "\u0000"; // sentinel distinct from null and any portal id

    // Armed freeze-mode guide line(s) for THIS pane: the mode + the pointer position in SCREEN space.
    // Null when not arming. Set by ViewportPanel as the pointer moves while a freeze mode is armed.
    private (FreezeMode Mode, double X, double Y)? _freezeGuide;

    public DocumentView()
    {
        InitializeComponent();
        // The floating Unfreeze chip clears THIS pane's freeze (per-viewport) and refreshes immediately.
        FreezeChip.Click += (_, _) =>
        {
            if (_shared is { } vm && _viewport is { } vp)
            {
                vm.UnfreezeViewport(vp);
                RenderFreezePanes();
            }
        };
    }

    public TabViewModel? Tab => _tab;

    /// <summary>Wire the viewport to the shared VM and render the given tab. Call once after
    /// the view is in the tree (the host sets Shared before the layers need it).</summary>
    public void Initialize(MainWindowViewModel shared, TabViewModel? tab)
    {
        _shared = shared;
        _tab = tab;
        _viewport = tab?.Viewport;
        _images = tab?.Images;

        Viewport.ViewModel = shared;
        Viewport.OwnerView = this; // so screen↔page mapping uses THIS pane's viewport camera
        Minimap.ViewModel = shared;
        Minimap.OwnerView = this; // so the minimap reflects + drives THIS pane's viewport, not the primary
        ToolBar.ViewModel = shared;

        _ownsImages = false; // borrowing tab.Images (shared with the minimap)
        // Size THIS pane's own viewport (Phase 3: no controller ambient size any more).
        _viewport?.SetSize(Viewport.Bounds.Width, Viewport.Bounds.Height);
        if (!_wired)
        {
            Viewport.SizeChanged += OnViewportSizeChanged;
            // A press anywhere in this pane makes it the focused surface (input routes here). Tunnel +
            // handledEventsToo so it fires even when ViewportPanel marks the event handled for its own
            // drag/click logic.
            Viewport.AddHandler(InputElement.PointerPressedEvent, OnViewportPressedForFocus,
                RoutingStrategies.Tunnel, handledEventsToo: true);
            _wired = true;
        }

        UpdateLayerBindings(tab);

        // window.Opened (which calls OpenDocument) can fire before this finishes wiring.
        // If a tab is already present, re-center and push fresh state now that layout
        // is complete (CenterPage previously ran with the wrong viewport size).
        if (_viewport is { } vp && Viewport.Bounds.Width > 0)
        {
            vp.CenterPage(Viewport.Bounds.Width, Viewport.Bounds.Height);
            vp.UpdateRailZoom(Viewport.Bounds.Width, Viewport.Bounds.Height);
            UpdatePagePanelSize(tab);
            UpdateLayerBindings(tab);
        }
    }

    public void Teardown()
    {
        if (_wired)
        {
            Viewport.SizeChanged -= OnViewportSizeChanged;
            Viewport.RemoveHandler(InputElement.PointerPressedEvent, OnViewportPressedForFocus);
            _wired = false;
        }
        if (_ownsImages)
        {
            _images?.Dispose();
            _images = null;
            _ownsImages = false;
        }
        // Drop references so a late event (a queued pointer / size change during the reparent or
        // close) can't reach a removed/disposed Core viewport through OwnerView → SurfaceViewport.
        Viewport.OwnerView = null;
        Minimap.OwnerView = null;
        _viewport = null;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Re-parenting (e.g. moving a docked split pane into a tear-off window) tears down each
        // composition layer's visual and recreates it empty on re-attach (CompositionLayerControl
        // nulls its visual on detach). A static pane produces no TickResult change, so the frame
        // loop won't repaint it — re-push this surface's full state here so it isn't blank until a
        // later resize/interaction. No-op on the initial attach: _viewport is still null then
        // (Initialize runs afterwards and pushes state itself).
        if (_shared is not null && _viewport is not null)
        {
            UpdatePagePanelSize(_tab);
            UpdateAllLayers();
            Minimap.InvalidateVisual();
        }
    }

    // ── IViewportSurface ──────────────────────────────────────────────────────────

    /// <summary>The Core viewport this surface renders (per-view camera/rail/page), or null.</summary>
    public CoreViewport? SurfaceViewport => _viewport;

    public (double Width, double Height) SurfaceSize => (Viewport.Bounds.Width, Viewport.Bounds.Height);

    /// <summary>This surface's GPU image wraps, for its OWN embedded minimap: a detached pane owns its
    /// <see cref="ViewportImages"/>, the primary borrows the document's. Null before binding.</summary>
    internal SKImage? SurfaceMinimapImage => _images?.MinimapImage;
    internal SKImage? SurfacePageImage => _images?.CachedImage;

    public void SetFocusedVisual(bool focused)
        => FocusBorder.BorderThickness = new Thickness(focused ? 2 : 0);

    /// <summary>Render this surface against a specific viewport of the active document (a split pane /
    /// tear-off window), with its own <see cref="ViewportImages"/>. The model state (annotations,
    /// analysis cache, prefs) stays on <see cref="Tab"/>; only the per-view geometry + page images move.
    /// Disposes the previously-owned images if any.</summary>
    public void BindViewport(CoreViewport vp, ViewportImages images, bool ownsImages)
    {
        if (_ownsImages && _images is { } old && !ReferenceEquals(old, images))
            old.Dispose();
        _viewport = vp;
        _images = images;
        _ownsImages = ownsImages;

        // A detached viewport needs its own wake hooks: Core sets DpiRenderReady / fires the analysis
        // fan-out off the UI thread, and only an animation frame picks those up. (The primary view's
        // equivalent is wired via tab.OnDpiRenderComplete in Initialize.)
        if (_shared is { } vm)
        {
            vp.OnDpiRenderComplete = () => vm.RequestAnimationFrame();
            vp.RequestAnimation = () => vm.RequestAnimationFrame();
        }

        var (ww, wh) = (Viewport.Bounds.Width, Viewport.Bounds.Height);
        if (ww > 0 && wh > 0)
        {
            vp.SetSize(ww, wh);
            vp.CenterPage(ww, wh);
            vp.UpdateRailZoom(ww, wh);
            _pendingCenter = false;
        }
        else
        {
            // Not laid out yet (a freshly-created pane); centre on the first SizeChanged.
            _pendingCenter = true;
        }
        UpdatePagePanelSize(_tab);
        UpdateAllLayers();
        Minimap.InvalidateVisual();
    }

    private void OnViewportPressedForFocus(object? sender, PointerPressedEventArgs e)
    {
        if (_shared is { } vm && _viewport is { } vp)
            vm.FocusSurface(this, vp);
    }

    /// <summary>Switch the rendered tab (active document changed).</summary>
    public void SetTab(TabViewModel? tab)
    {
        // SetTab is driven by OnPropertyChanged(nameof(ActiveTab)), which also fires once
        // per OverlayChanged animation frame (rail line advance / auto-scroll) and after
        // every InvalidateCanvas/OnConfigChanged. In all those same-tab cases the layers
        // were already repainted (InvalidateOverlay / InvalidateAll) just before, so the
        // full tab-switch path here — rebuild+resend all four layer states, re-wire the
        // per-frame DPI closure, and force an *unthrottled* minimap repaint — is pure
        // per-frame waste. Only the throttled panel/minimap sync is needed on a same-tab call.
        if (ReferenceEquals(_tab, tab))
        {
            UpdatePagePanelSize(tab);
            return;
        }

        // Stop the outgoing tab driving this view: a late DPI render completing on the old tab
        // would otherwise call OnDpiRenderComplete and schedule a wasted animation frame.
        if (_tab is { } prev)
        {
            prev.OnDpiRenderComplete = null;
            prev.Viewport.RequestAnimation = null; // stop the outgoing tab waking this pane
        }
        if (_ownsImages)
            _images?.Dispose();
        _tab = tab;
        _viewport = tab?.Viewport;
        _images = tab?.Images;
        _ownsImages = false; // borrowing tab.Images
        UpdateLayerBindings(tab);
        UpdatePagePanelSize(tab);
        Minimap.InvalidateVisual();
    }

    public void UpdateAnnotationCursor() => Viewport.UpdateAnnotationCursor();

    /// <summary>Move keyboard focus to the viewport so nav keys drive the page (used after a
    /// side pane navigates via a click).</summary>
    public void FocusViewport() => Viewport.Focus();

    // ── Invalidation entry points (called by MainWindow's InvalidationCallbacks) ──

    public void RenderCamera()
    {
        UpdatePagePanelSize(_tab);
        UpdateAllLayers();
    }

    public void RenderPage()
    {
        var state = BuildPageState(_tab);
        PageLayer.UpdateState(state);
        if (!ReferenceEquals(state.Image, _lastMinimapImage))
        {
            _lastMinimapImage = state.Image;
            Minimap.InvalidateVisual();
        }
        RenderPortalMarkers();   // a rail page cross changes which page's markers apply
        RenderFreezePanes();     // a colour-effect / DPI change re-renders the page → refresh frozen tiles' effect too
        Viewport.NotifyAccessibilityStateChanged(); // page change → announce
    }

    /// <summary>Re-send the freeze-panes overlay state. Called from the camera path (UpdateAllLayers)
    /// and from RenderPage so a colour-effect change (which invalidates the page, not the camera)
    /// repaints the frozen tiles with the new effect immediately rather than on the next navigation.</summary>
    public void RenderFreezePanes()
    {
        FreezeLayer.UpdateState(BuildFreezePaneState(_tab));
        // Show the per-pane Unfreeze chip whenever THIS view is frozen.
        FreezeChip.IsVisible = _shared is { } vm && _viewport is { } vp && vm.IsViewportFrozen(vp);
    }

    /// <summary>Set/clear this pane's armed freeze-mode guide line(s) at a screen-space point (or
    /// <see cref="FreezeMode.None"/> to clear), re-rendering the freeze layer only when it changes.
    /// Called by <see cref="ViewportPanel"/> as the pointer moves while a freeze mode is armed.</summary>
    public void SetFreezeGuide(FreezeMode mode, double screenX, double screenY)
    {
        var next = mode == FreezeMode.None ? ((FreezeMode, double, double)?)null : (mode, screenX, screenY);
        if (System.Collections.Generic.EqualityComparer<(FreezeMode, double, double)?>.Default.Equals(_freezeGuide, next))
            return;
        _freezeGuide = next;
        RenderFreezePanes();
    }

    /// <summary>Re-send the portal marker overlay state (markers added/removed, accent moved, or page
    /// changed). Cheap: the page-space marker list is cached and only rebuilt when it actually changes.</summary>
    public void RenderPortalMarkers() => PortalMarkerLayer.UpdateState(BuildPortalMarkerState(_tab));

    public void RenderOverlay()
    {
        OverlayLayer.UpdateState(BuildOverlayState(_tab));
        Viewport.NotifyAccessibilityStateChanged(); // rail line / mode change → announce
    }
    public void RenderSearch() => SearchLayer.UpdateState(BuildSearchState(_tab));
    public void RenderAnnotations() => AnnotationLayer.UpdateState(BuildAnnotationState(_tab));

    /// <summary>Push-based accessibility announce (from Core's PageChanged / ReadingPositionChanged),
    /// independent of a layer repaint. Cheap no-op when no AT-SPI/UIA client is connected.</summary>
    public void NotifyAccessibility() => Viewport.NotifyAccessibilityStateChanged();

    private void OnViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_shared is null) return;
        var (ww, wh) = (Viewport.Bounds.Width, Viewport.Bounds.Height);
        if (_viewport is { } vp)
        {
            // Per-view size → correct tick/clamp/seat + ReadingPosition.HorizontalFraction for this
            // surface. Phase 3: each viewport owns its size; there is no controller ambient size.
            vp.SetSize(ww, wh);
            if (_pendingCenter)
            {
                // First layout of a freshly-bound pane: fit/centre it now that it has bounds.
                vp.CenterPage(ww, wh);
                vp.UpdateRailZoom(ww, wh);
                _pendingCenter = false;
            }
            vp.ClampCamera(ww, wh);
            UpdatePagePanelSize(_tab);
            UpdateAllLayers();
        }
        // No bound viewport yet → nothing to size (Phase 3 removed the controller ambient size; the
        // primary is sized from its own bounds once a tab binds here).
    }

    private void UpdateLayerBindings(TabViewModel? tab)
    {
        if (_shared is null) return;
        UpdateAllLayers();
        if (tab is not null)
        {
            // Wire this tab's viewport wake hooks (mirrors BindViewport for split panes): a DPI
            // re-render or an off-thread analysis fan-out only repaints if the viewport can request a
            // frame. Needed for a shared-model tab shown here while focus is on another surface — its
            // rail would otherwise seat without repainting until an unrelated frame.
            tab.OnDpiRenderComplete = () => _shared.RequestAnimationFrame();
            tab.Viewport.RequestAnimation = () => _shared.RequestAnimationFrame();
        }
    }

    /// <summary>Sends fresh state to all composition layer handlers.</summary>
    private void UpdateAllLayers()
    {
        PageLayer.UpdateState(BuildPageState(_tab));
        OverlayLayer.UpdateState(BuildOverlayState(_tab));
        SearchLayer.UpdateState(BuildSearchState(_tab));
        AnnotationLayer.UpdateState(BuildAnnotationState(_tab));
        RenderFreezePanes();
        PortalMarkerLayer.UpdateState(BuildPortalMarkerState(_tab));
    }

    /// <summary>
    /// Updates PagePanel dimensions (used by the minimap and scrollbar calculations)
    /// and conditionally invalidates the minimap when the viewport position changes
    /// enough to be visible at its small display size. The camera transform itself is
    /// applied inside each layer's Skia canvas.
    /// </summary>
    private void UpdatePagePanelSize(TabViewModel? tab)
    {
        if (_viewport is not { } vp)
        {
            PagePanel.Width = 0;
            PagePanel.Height = 0;
            return;
        }

        PagePanel.Width = vp.PageWidth;
        PagePanel.Height = vp.PageHeight;

        // The minimap is ≤200×280px — sub-pixel viewport indicator movement is
        // invisible. Use thresholds large enough to skip redraws during smooth
        // scrolling frames where the visual change is imperceptible.
        if (Math.Abs(vp.Camera.OffsetX - _lastMinimapOx) > 24.0 ||
            Math.Abs(vp.Camera.OffsetY - _lastMinimapOy) > 24.0 ||
            Math.Abs(vp.Camera.Zoom - _lastMinimapZoom) > 0.02)
        {
            _lastMinimapOx = vp.Camera.OffsetX;
            _lastMinimapOy = vp.Camera.OffsetY;
            _lastMinimapZoom = vp.Camera.Zoom;
            Minimap.InvalidateVisual();
        }
    }

    // ── State builders ──────────────────────────────────────────────────────────

    private static SKMatrix BuildCamera(CoreViewport? vp)
    {
        if (vp is null) return SKMatrix.Identity;
        float zoom = (float)vp.Camera.Zoom;
        return SKMatrix.CreateScaleTranslation(
            zoom, zoom, (float)vp.Camera.OffsetX, (float)vp.Camera.OffsetY);
    }

    private PdfPageRenderState BuildPageState(TabViewModel? tab)
    {
        var vm = _shared!;
        float lineY = 0, lineH = 0;
        if (_viewport?.Rail is { Active: true, NavigableCount: > 0 })
        {
            var line = _viewport.Rail.CurrentLineInfo;
            lineY = line.Y;
            lineH = line.Height;
        }
        var (image, retired) = _images?.GetCachedImage() ?? (null, null);
        if (retired is not null)
        {
            // Send the retired image to the composition thread for safe disposal.
            // If the layer is detached (visual gone), the message is silently dropped,
            // so dispose immediately on the UI thread as a fallback.
            if (!PageLayer.TrySendMessage(new RetireImage(retired)))
                retired.Dispose();
        }
        return new PdfPageRenderState(
            Image: image,
            PageW: (float)(_viewport?.PageWidth ?? 0),
            PageH: (float)(_viewport?.PageHeight ?? 0),
            Camera: BuildCamera(_viewport),
            ScrollSpeed: (float)(_viewport?.Rail.ScrollSpeed ?? 0),
            ZoomSpeed: (float)(_viewport?.Camera.ZoomSpeed ?? 0),
            MotionBlur: vm.AppConfig.MotionBlur,
            MotionBlurIntensity: (float)vm.AppConfig.MotionBlurIntensity,
            // On a table the scoped table focus-dim (in the overlay) replaces the page line dim.
            LineFocusBlur: (tab?.LineFocusBlur ?? false) && !vm.TableFocusActive(_viewport),
            LineFocusIntensity: (float)vm.AppConfig.LineFocusBlurIntensity,
            LinePadding: (float)vm.AppConfig.LinePadding,
            LineY: lineY,
            LineH: lineH,
            Effect: vm.Controller.ActiveColourEffect,
            EffectIntensity: vm.Controller.ActiveColourIntensity,
            Effects: vm.ColourEffects);
    }

    private RailOverlayRenderState BuildOverlayState(TabViewModel? tab)
    {
        var vm = _shared!;
        LayoutBlock? currentBlock = null;
        LineInfo currentLine = default;
        if (_viewport?.Rail is { Active: true, HasAnalysis: true } rail && rail.NavigableCount > 0)
        {
            currentBlock = rail.CurrentNavigableBlock;
            currentLine = rail.CurrentLineInfo;
        }
        PageAnalysis? debugAnalysis = null;
        if (tab?.DebugOverlay == true && _viewport is { } dbgVp)
            tab.AnalysisCache.TryGetValue(dbgVp.CurrentPage, out debugAnalysis);

        // Scoped table focus aids: when the rail is on a table row with cells, the overlay draws the
        // scoped tint/dim and the package line highlight + page dim are suppressed (passed false).
        bool tableFocus = vm.TableFocusActive(_viewport);
        var tableScope = vm.EffectiveTableFocusScope;
        CellInfo? tableCell = tableFocus ? _viewport!.Rail.CurrentCellInfo : null;
        // The column band is only needed (and only inferred) for the column-bearing scopes.
        Services.ColumnBand? tableColumn = tableFocus
            && tableScope is Services.TableFocusScope.Column or Services.TableFocusScope.RowAndColumn
            ? vm.CurrentTableColumn(_viewport) : null;

        return new RailOverlayRenderState(
            Camera: BuildCamera(_viewport),
            PageW: (float)(_viewport?.PageWidth ?? 0),
            PageH: (float)(_viewport?.PageHeight ?? 0),
            CurrentBlock: currentBlock,
            CurrentLine: currentLine,
            DebugOverlay: tab?.DebugOverlay ?? false,
            DebugAnalysis: debugAnalysis,
            DebugModelLabel: vm.ActiveLayoutModelName,
            Effect: vm.Controller.ActiveColourEffect,
            LineFocusBlur: (tab?.LineFocusBlur ?? false) && !tableFocus,
            LineHighlightEnabled: (tab?.LineHighlightEnabled ?? true) && !tableFocus,
            LinePadding: (float)vm.AppConfig.LinePadding,
            Tint: vm.AppConfig.LineHighlightTint,
            TintOpacity: (float)vm.AppConfig.LineHighlightOpacity,
            TableScope: tableScope,
            // Highlight + dim stay on the usual controls (H / F); the table scope only shapes them.
            TableHighlight: tableFocus && (tab?.LineHighlightEnabled ?? true),
            TableDim: tableFocus && (tab?.LineFocusBlur ?? false),
            TableDimIntensity: (float)vm.AppConfig.LineFocusBlurIntensity,
            TableCell: tableCell,
            TableColumn: tableColumn);
    }

    private static readonly FreezePaneRenderState EmptyFreeze =
        new(null, null, null, default, default, default, ColourEffect.None, 0f, null);

    /// <summary>Builds the table freeze-panes overlay state: pulls the (lazily rendered) crop images
    /// from the VM, forwards any retired crops to the layer for composition-thread disposal, and maps
    /// the three page-space regions to screen-space destination rects (pinned to the table's top-left,
    /// clamped to the viewport; the top band tracks horizontal scroll, the left band vertical).</summary>
    private FreezePaneRenderState BuildFreezePaneState(TabViewModel? tab)
    {
        var vm = _shared!;
        if (tab is null || _viewport is not { } fvp) return EmptyFreeze;

        // Per-viewport (railreader2#180): each pane renders ITS OWN viewport's freeze, so a freeze in
        // one split/tab no longer bleeds into the others.
        var tiles = vm.GetFreezeTiles(fvp, out var retired);
        foreach (var img in retired)
            if (!FreezeLayer.TrySendMessage(new RetireImage(img)))
                img.Dispose();

        var cam = BuildCamera(_viewport);
        float zoom = cam.ScaleX, ox = cam.TransX, oy = cam.TransY;

        // Committed frozen crops (absent while merely previewing a pick).
        SKImage? corner = null, top = null, left = null;
        SKRect cornerDst = default, topDst = default, leftDst = default;
        if (tiles is { } t)
        {
            float pinX = Math.Max(0f, t.CornerBox.X * zoom + ox);
            float pinY = Math.Max(0f, t.CornerBox.Y * zoom + oy);
            corner = t.Corner; top = t.Top; left = t.Left;
            cornerDst = Dst(t.CornerBox, zoom, pinX, pinY);                  // static at the pin
            topDst = Dst(t.TopBox, zoom, t.TopBox.X * zoom + ox, pinY);      // tracks horizontal scroll
            leftDst = Dst(t.LeftBox, zoom, pinX, t.LeftBox.Y * zoom + oy);   // tracks vertical scroll
        }

        // Armed freeze-mode guide line(s) for this pane — already in screen space (they track the
        // pointer exactly, no snapping). Drawn full-length by the handler.
        bool showGuide = false, guideH = false, guideV = false;
        float guideX = 0f, guideY = 0f;
        if (_freezeGuide is { } g)
        {
            showGuide = true;
            guideH = g.Mode is FreezeMode.Rows or FreezeMode.Both;
            guideV = g.Mode is FreezeMode.Columns or FreezeMode.Both;
            guideX = (float)g.X;
            guideY = (float)g.Y;
        }

        if (corner is null && top is null && left is null && !showGuide) return EmptyFreeze;

        return new FreezePaneRenderState(
            corner, top, left, cornerDst, topDst, leftDst,
            vm.Controller.ActiveColourEffect, vm.Controller.ActiveColourIntensity, vm.ColourEffects,
            showGuide, guideH, guideV, guideX, guideY);

        static SKRect Dst(BBox box, float zoom, float x, float y)
            => SKRect.Create(x, y, box.W * zoom, box.H * zoom);
    }

    private AnnotationRenderState BuildAnnotationState(TabViewModel? tab)
    {
        var vm = _shared!;
        List<Annotation>? pageAnnotations = null;
        if (tab is not null && _viewport is { } vp)
            tab.Annotations.Pages.TryGetValue(vp.CurrentPage, out pageAnnotations);

        // Pre-sort by z-order on the UI thread so the compositor doesn't need LINQ.
        // Cache the result: z-order only changes when the page's annotation set
        // changes, so pan/zoom frames (which re-send camera every tick) reuse it.
        List<Annotation>? sorted;
        if (pageAnnotations is not { Count: > 1 })
        {
            sorted = pageAnnotations;
        }
        else if (ReferenceEquals(pageAnnotations, _annoSortSource)
            && pageAnnotations.Count == _annoSortCount
            && _viewport!.CurrentPage == _annoSortPage)
        {
            sorted = _annoSortResult;
        }
        else
        {
            sorted = AnnotationRenderer.SortByZOrder(pageAnnotations);
            _annoSortSource = pageAnnotations;
            _annoSortCount = pageAnnotations.Count;
            _annoSortPage = _viewport!.CurrentPage;
            _annoSortResult = sorted;
        }

        return new AnnotationRenderState(
            Camera: BuildCamera(_viewport),
            PageAnnotations: sorted,
            SelectedAnnotation: vm.SelectedAnnotation,
            PreviewAnnotation: vm.PreviewAnnotation,
            TextSelectionRects: vm.TextSelectionRects);
    }

    private SearchRenderState BuildSearchState(TabViewModel? tab)
    {
        var vm = _shared!;
        // Render THIS surface's own page matches: a split pane / tear-off can sit on a different page
        // than the focused view, so key the highlights by this viewport's page (MatchesForPage) rather
        // than the focused view's CurrentPageSearchMatches.
        var matches = _viewport is { } mvp ? vm.MatchesForPage(mvp.CurrentPage) : null;
        int activeLocalIndex = -1;
        if (matches is { Count: > 0 } && _viewport is { } vp)
        {
            // The active local index only changes on match navigation or page
            // change — not while the camera moves. Cache across camera-only frames.
            if (ReferenceEquals(matches, _searchIdxMatches)
                && vm.ActiveMatchIndex == _searchIdxActive
                && vp.CurrentPage == _searchIdxPage)
            {
                activeLocalIndex = _searchIdxResult;
            }
            else
            {
                activeLocalIndex = OverlayRenderer.ComputeActiveLocalIndex(
                    vm.SearchMatches, matches, vm.ActiveMatchIndex, vp.CurrentPage);
                _searchIdxMatches = matches;
                _searchIdxActive = vm.ActiveMatchIndex;
                _searchIdxPage = vp.CurrentPage;
                _searchIdxResult = activeLocalIndex;
            }
        }

        // Compute viewport in page space for search highlight culling.
        var camera = BuildCamera(_viewport);
        SKRect viewport = SKRect.Empty;
        if (camera.TryInvert(out var inv))
        {
            var bounds = Bounds;
            viewport = inv.MapRect(new SKRect(0, 0, (float)bounds.Width, (float)bounds.Height));
        }

        return new SearchRenderState(
            Camera: camera,
            Matches: matches,
            ActiveLocalIndex: activeLocalIndex,
            ViewportInPageSpace: viewport);
    }

    private PortalMarkerRenderState BuildPortalMarkerState(TabViewModel? tab)
    {
        var vm = _shared!;
        int page = _viewport?.CurrentPage ?? int.MinValue;
        int count = tab?.Portals.Portals.Count ?? 0;
        string? displayed = vm.DisplayedPortalId;

        if (_markerCache is null || !ReferenceEquals(tab, _markerTab) || page != _markerPage
            || count != _markerCount || displayed != _markerDisplayed)
        {
            _markerTab = tab;
            _markerPage = page;
            _markerCount = count;
            _markerDisplayed = displayed;
            _markerCache = [];
            foreach (var m in vm.BuildPortalMarkers(_viewport))
                _markerCache.Add(new PortalMarkerInfo(
                    m.Kind == PortalMarkerKind.Source, m.PageX, m.PageY, m.IsActive, m.Count));
        }
        return new PortalMarkerRenderState(BuildCamera(_viewport), _markerCache);
    }
}
