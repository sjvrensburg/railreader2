using Avalonia.Controls;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Renderer.Skia;
using RailReader2.ViewModels;
using SkiaSharp;

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
public partial class DocumentView : UserControl
{
    private MainWindowViewModel? _shared;
    private TabViewModel? _tab;
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

    public DocumentView()
    {
        InitializeComponent();
    }

    public TabViewModel? Tab => _tab;

    /// <summary>Wire the viewport to the shared VM and render the given tab. Call once after
    /// the view is in the tree (the host sets Shared before the layers need it).</summary>
    public void Initialize(MainWindowViewModel shared, TabViewModel? tab)
    {
        _shared = shared;
        _tab = tab;

        Viewport.ViewModel = shared;
        Minimap.ViewModel = shared;
        ToolBar.ViewModel = shared;

        shared.SetViewportSize(Viewport.Bounds.Width, Viewport.Bounds.Height);
        if (!_wired)
        {
            Viewport.SizeChanged += OnViewportSizeChanged;
            _wired = true;
        }

        UpdateLayerBindings(tab);

        // window.Opened (which calls OpenDocument) can fire before this finishes wiring.
        // If a tab is already present, re-center and push fresh state now that layout
        // is complete (CenterPage previously ran with the wrong viewport size).
        if (tab is not null && Viewport.Bounds.Width > 0)
        {
            tab.CenterPage(Viewport.Bounds.Width, Viewport.Bounds.Height);
            tab.UpdateRailZoom(Viewport.Bounds.Width, Viewport.Bounds.Height);
            UpdatePagePanelSize(tab);
            UpdateLayerBindings(tab);
        }
    }

    public void Teardown()
    {
        if (_wired)
        {
            Viewport.SizeChanged -= OnViewportSizeChanged;
            _wired = false;
        }
    }

    /// <summary>Switch the rendered tab (active document changed).</summary>
    public void SetTab(TabViewModel? tab)
    {
        // Stop the outgoing tab driving this view: a late DPI render completing on the old tab
        // would otherwise call OnDpiRenderComplete and schedule a wasted animation frame.
        if (_tab is { } prev && !ReferenceEquals(prev, tab))
            prev.OnDpiRenderComplete = null;
        _tab = tab;
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
        Viewport.NotifyAccessibilityStateChanged(); // page change → announce
    }

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
        _shared.SetViewportSize(Viewport.Bounds.Width, Viewport.Bounds.Height);
        if (_tab is { } tab)
        {
            var (ww, wh) = (Viewport.Bounds.Width, Viewport.Bounds.Height);
            tab.ClampCamera(ww, wh);
            UpdatePagePanelSize(tab);
            UpdateAllLayers();
        }
    }

    private void UpdateLayerBindings(TabViewModel? tab)
    {
        if (_shared is null) return;
        UpdateAllLayers();
        if (tab is not null)
            tab.OnDpiRenderComplete = () => _shared.RequestAnimationFrame();
    }

    /// <summary>Sends fresh state to all four composition layer handlers.</summary>
    private void UpdateAllLayers()
    {
        PageLayer.UpdateState(BuildPageState(_tab));
        OverlayLayer.UpdateState(BuildOverlayState(_tab));
        SearchLayer.UpdateState(BuildSearchState(_tab));
        AnnotationLayer.UpdateState(BuildAnnotationState(_tab));
    }

    /// <summary>
    /// Updates PagePanel dimensions (used by the minimap and scrollbar calculations)
    /// and conditionally invalidates the minimap when the viewport position changes
    /// enough to be visible at its small display size. The camera transform itself is
    /// applied inside each layer's Skia canvas.
    /// </summary>
    private void UpdatePagePanelSize(TabViewModel? tab)
    {
        if (tab is null)
        {
            PagePanel.Width = 0;
            PagePanel.Height = 0;
            return;
        }

        PagePanel.Width = tab.PageWidth;
        PagePanel.Height = tab.PageHeight;

        // The minimap is ≤200×280px — sub-pixel viewport indicator movement is
        // invisible. Use thresholds large enough to skip redraws during smooth
        // scrolling frames where the visual change is imperceptible.
        if (Math.Abs(tab.Camera.OffsetX - _lastMinimapOx) > 24.0 ||
            Math.Abs(tab.Camera.OffsetY - _lastMinimapOy) > 24.0 ||
            Math.Abs(tab.Camera.Zoom - _lastMinimapZoom) > 0.02)
        {
            _lastMinimapOx = tab.Camera.OffsetX;
            _lastMinimapOy = tab.Camera.OffsetY;
            _lastMinimapZoom = tab.Camera.Zoom;
            Minimap.InvalidateVisual();
        }
    }

    // ── State builders ──────────────────────────────────────────────────────────

    private static SKMatrix BuildCamera(TabViewModel? tab)
    {
        if (tab is null) return SKMatrix.Identity;
        float zoom = (float)tab.Camera.Zoom;
        return SKMatrix.CreateScaleTranslation(
            zoom, zoom, (float)tab.Camera.OffsetX, (float)tab.Camera.OffsetY);
    }

    private PdfPageRenderState BuildPageState(TabViewModel? tab)
    {
        var vm = _shared!;
        float lineY = 0, lineH = 0;
        if (tab?.Rail is { Active: true, NavigableCount: > 0 })
        {
            var line = tab.Rail.CurrentLineInfo;
            lineY = line.Y;
            lineH = line.Height;
        }
        var (image, retired) = tab?.GetCachedImage() ?? (null, null);
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
            PageW: (float)(tab?.PageWidth ?? 0),
            PageH: (float)(tab?.PageHeight ?? 0),
            Camera: BuildCamera(tab),
            ScrollSpeed: (float)(tab?.Rail.ScrollSpeed ?? 0),
            ZoomSpeed: (float)(tab?.Camera.ZoomSpeed ?? 0),
            MotionBlur: vm.AppConfig.MotionBlur,
            MotionBlurIntensity: (float)vm.AppConfig.MotionBlurIntensity,
            LineFocusBlur: tab?.LineFocusBlur ?? false,
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
        if (tab?.Rail is { Active: true, HasAnalysis: true } rail && rail.NavigableCount > 0)
        {
            currentBlock = rail.CurrentNavigableBlock;
            currentLine = rail.CurrentLineInfo;
        }
        PageAnalysis? debugAnalysis = null;
        if (tab?.DebugOverlay == true)
            tab.AnalysisCache.TryGetValue(tab.CurrentPage, out debugAnalysis);

        return new RailOverlayRenderState(
            Camera: BuildCamera(tab),
            PageW: (float)(tab?.PageWidth ?? 0),
            PageH: (float)(tab?.PageHeight ?? 0),
            CurrentBlock: currentBlock,
            CurrentLine: currentLine,
            DebugOverlay: tab?.DebugOverlay ?? false,
            DebugAnalysis: debugAnalysis,
            DebugModelLabel: vm.ActiveLayoutModelName,
            Effect: vm.Controller.ActiveColourEffect,
            LineFocusBlur: tab?.LineFocusBlur ?? false,
            LineHighlightEnabled: tab?.LineHighlightEnabled ?? true,
            LinePadding: (float)vm.AppConfig.LinePadding,
            Tint: vm.AppConfig.LineHighlightTint,
            TintOpacity: (float)vm.AppConfig.LineHighlightOpacity);
    }

    private AnnotationRenderState BuildAnnotationState(TabViewModel? tab)
    {
        var vm = _shared!;
        List<Annotation>? pageAnnotations = null;
        if (tab is not null)
            tab.Annotations.Pages.TryGetValue(tab.CurrentPage, out pageAnnotations);

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
            && tab!.CurrentPage == _annoSortPage)
        {
            sorted = _annoSortResult;
        }
        else
        {
            sorted = AnnotationRenderer.SortByZOrder(pageAnnotations);
            _annoSortSource = pageAnnotations;
            _annoSortCount = pageAnnotations.Count;
            _annoSortPage = tab!.CurrentPage;
            _annoSortResult = sorted;
        }

        return new AnnotationRenderState(
            Camera: BuildCamera(tab),
            PageAnnotations: sorted,
            SelectedAnnotation: vm.SelectedAnnotation,
            PreviewAnnotation: vm.PreviewAnnotation,
            TextSelectionRects: vm.TextSelectionRects);
    }

    private SearchRenderState BuildSearchState(TabViewModel? tab)
    {
        var vm = _shared!;
        // Refresh the per-page match cache against the page currently on screen.
        // SearchService only updates it on match navigation, so scroll/GoToPage page
        // changes would otherwise leave it showing a previous page's matches.
        vm.RefreshCurrentPageSearchMatches();
        var matches = vm.CurrentPageSearchMatches;
        int activeLocalIndex = -1;
        if (matches is { Count: > 0 } && tab is not null)
        {
            // The active local index only changes on match navigation or page
            // change — not while the camera moves. Cache across camera-only frames.
            if (ReferenceEquals(matches, _searchIdxMatches)
                && vm.ActiveMatchIndex == _searchIdxActive
                && tab.CurrentPage == _searchIdxPage)
            {
                activeLocalIndex = _searchIdxResult;
            }
            else
            {
                activeLocalIndex = OverlayRenderer.ComputeActiveLocalIndex(
                    vm.SearchMatches, matches, vm.ActiveMatchIndex, tab.CurrentPage);
                _searchIdxMatches = matches;
                _searchIdxActive = vm.ActiveMatchIndex;
                _searchIdxPage = tab.CurrentPage;
                _searchIdxResult = activeLocalIndex;
            }
        }

        // Compute viewport in page space for search highlight culling.
        var camera = BuildCamera(tab);
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
}
