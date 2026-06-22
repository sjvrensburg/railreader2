using CommunityToolkit.Mvvm.ComponentModel;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using SkiaSharp;

namespace RailReader2.ViewModels;

/// <summary>
/// Thin Avalonia wrapper around one <b>view</b> of a document: its own <see cref="Viewport"/>
/// (camera / rail / current page) plus the shared <see cref="DocumentModel"/> (PDF, caches,
/// annotations, outline). A tab is the document's <c>Primary</c> view for the first tab opened on a
/// file; opening the same file again adds another <see cref="Viewport"/> to the <em>same</em> model
/// (railreader2#180 decision #1), so several tabs can share one model (and its caches / annotations)
/// while each keeps its own camera and reading position.
/// Surfaces [ObservableProperty] for data binding and delegates only what the Views actually need.
/// </summary>
public sealed partial class TabViewModel : ObservableObject, IDisposable
{
    /// <summary>The shared document model (PDF, caches, annotations, outline). Several tabs of the
    /// same file share one of these.</summary>
    public DocumentModel State { get; }

    /// <summary>This tab's own view onto the document — its camera, rail, and current page. The
    /// model's <c>Primary</c> for the first tab on a file; an added viewport for duplicate tabs.</summary>
    public Viewport Viewport { get; }

    [ObservableProperty] private string _title;
    [ObservableProperty] private int _currentPage;
    [ObservableProperty] private double _pageWidth;
    [ObservableProperty] private double _pageHeight;
    [ObservableProperty] private bool _debugOverlay;
    [ObservableProperty] private bool _pendingRailSetup;
    [ObservableProperty] private bool _lineFocusBlur;
    [ObservableProperty] private bool _lineHighlightEnabled = true;
    [ObservableProperty] private bool _marginCropping;

    /// <summary>
    /// Full-scan figure index from a completed Scan All operation. Persists per-document
    /// so switching tabs doesn't lose the data. Null until a scan is performed.
    /// </summary>
    public PeekIndex? FullScanPeekIndex { get; set; }

    /// <summary>This document's linked-context portals (shell-managed sidecar). Loaded in
    /// <c>OpenDocument</c> right after annotations; follows <c>ActiveTab</c> for free. Duplicate tabs
    /// of the same PDF each hold their own set (last-writer-wins; documented, not solved in v1).</summary>
    public Services.PortalSet Portals { get; set; } = new();

    /// <summary>Whether the side panel (outline/bookmarks/search) is visible for this tab.</summary>
    public bool ShowSidePanel { get; set; }

    /// <summary>Width of the side panel in pixels (preserved across tab switches).</summary>
    public double SidePanelWidth { get; set; } = 220;

    // Read-only document-level properties used by Views (shared across tabs of the same file).
    public string FilePath => State.FilePath;
    public int PageCount => State.PageCount;
    public IPdfService Pdf => State.Pdf;
    public IReadOnlyDictionary<int, PageAnalysis> AnalysisCache => State.CanonicalAnalyses;
    public List<OutlineEntry> Outline => State.Outline;
    public AnnotationFile Annotations => State.Annotations;

    // Per-view properties used by Views — this tab's own viewport, not the model's Primary.
    public Camera Camera => Viewport.Camera;
    public RailNav Rail => Viewport.Rail;
    public int CachedDpi => Viewport.CachedDpi;

    /// <summary>
    /// Per-viewport GPU-image lifecycle for THIS tab's view. The minimap shares it via the
    /// DocumentView; a detached split-pane / tear-off owns its own <see cref="ViewportImages"/>.
    /// </summary>
    public ViewportImages Images { get; }

    /// <summary>
    /// Returns the cached SKImage for GPU rendering and the previous image that
    /// was replaced (if any). The caller is responsible for disposing the retired
    /// image on a thread-safe boundary (e.g. the composition thread via OnMessage).
    /// Created on demand from the IRenderedPage; the UI layer owns this because
    /// SKImage.FromBitmap must be called on the UI thread.
    /// </summary>
    public (SKImage? Current, SKImage? Retired) GetCachedImage() => Images.GetCachedImage();

    /// <summary>
    /// Returns the current cached image without lifecycle management.
    /// Use only when no image transition is expected (e.g. minimap snapshot).
    /// </summary>
    public SKImage? CachedImage => Images.CachedImage;

    public SKBitmap? MinimapBitmap => Images.MinimapBitmap;

    /// <summary>
    /// Returns <see cref="MinimapBitmap"/> wrapped as an SKImage so the canvas
    /// can use sampling-aware DrawImage. Re-wraps when the underlying bitmap
    /// changes; previous wrappers are disposed.
    /// </summary>
    public SKImage? MinimapImage => Images.MinimapImage;

    public Action? OnDpiRenderComplete
    {
        get => Viewport.OnDpiRenderComplete;
        set => Viewport.OnDpiRenderComplete = value;
    }

    /// <summary>Construct a tab over <paramref name="viewport"/> (the model's <c>Primary</c> for a
    /// first-open, or an added viewport for a duplicate tab) on the shared <paramref name="state"/>.</summary>
    public TabViewModel(DocumentModel state, Viewport viewport)
    {
        State = state;
        Viewport = viewport;
        Images = new ViewportImages(viewport);

        _title = state.Title;
        _currentPage = viewport.CurrentPage;
        _pageWidth = viewport.PageWidth;
        _pageHeight = viewport.PageHeight;
        _debugOverlay = state.DebugOverlay;
        _pendingRailSetup = viewport.PendingRailSetup;
        _lineFocusBlur = state.LineFocusBlur;
        _lineHighlightEnabled = state.LineHighlightEnabled;
        _marginCropping = state.MarginCropping;

        // Per-view changes (page / dims / pending rail) come from THIS viewport; document-level
        // changes (title + display prefs, which are doc-level since Phase 3 #2) come from the model.
        viewport.StateChanged += OnViewportStateChanged;
        state.StateChanged += OnStateChanged;
    }

    private void OnViewportStateChanged(string propertyName)
    {
        switch (propertyName)
        {
            case nameof(Viewport.CurrentPage): CurrentPage = Viewport.CurrentPage; break;
            case nameof(Viewport.PageWidth): PageWidth = Viewport.PageWidth; break;
            case nameof(Viewport.PageHeight): PageHeight = Viewport.PageHeight; break;
            case nameof(Viewport.PendingRailSetup): PendingRailSetup = Viewport.PendingRailSetup; break;
        }
    }

    private void OnStateChanged(string propertyName)
    {
        switch (propertyName)
        {
            case nameof(Title): Title = State.Title; break;
            case nameof(DebugOverlay): DebugOverlay = State.DebugOverlay; break;
            case nameof(LineFocusBlur): LineFocusBlur = State.LineFocusBlur; break;
            case nameof(LineHighlightEnabled): LineHighlightEnabled = State.LineHighlightEnabled; break;
            case nameof(MarginCropping): MarginCropping = State.MarginCropping; break;
        }
    }

    // Sync observable property changes back: per-view → this viewport; display prefs → the model.
    partial void OnTitleChanged(string value) => State.Title = value;
    partial void OnCurrentPageChanged(int value) => Viewport.CurrentPage = value;
    partial void OnPageWidthChanged(double value) => Viewport.PageWidth = value;
    partial void OnPageHeightChanged(double value) => Viewport.PageHeight = value;
    partial void OnDebugOverlayChanged(bool value) => State.DebugOverlay = value;
    partial void OnPendingRailSetupChanged(bool value) => Viewport.PendingRailSetup = value;
    partial void OnLineFocusBlurChanged(bool value) => State.LineFocusBlur = value;
    partial void OnLineHighlightEnabledChanged(bool value) => State.LineHighlightEnabled = value;
    partial void OnMarginCroppingChanged(bool value) => State.MarginCropping = value;

    // Methods used by Views (MinimapControl, MainWindow) — act on this tab's own viewport.
    public void CenterPage(double ww, double wh) => Viewport.CenterPage(ww, wh);
    public void ClampCamera(double ww, double wh) => Viewport.ClampCamera(ww, wh);
    public void UpdateRailZoom(double ww, double wh) => Viewport.UpdateRailZoom(ww, wh);
    public void StartSnap(double ww, double wh) => Viewport.StartSnap(ww, wh);
    public void LoadAnnotations(AnnotationFileManager manager) => State.LoadAnnotations(manager);
    public bool SubmitPendingLookahead(AnalysisWorker? worker) => State.SubmitPendingLookahead(Viewport, worker);

    /// <summary>Detaches this tab's listeners and frees its per-view images. Does NOT dispose the
    /// shared <see cref="State"/> or remove the <see cref="Viewport"/> — model/viewport lifecycle is
    /// owned by <c>MainWindowViewModel</c> (a model is disposed only when its last tab closes).</summary>
    public void Dispose()
    {
        Viewport.StateChanged -= OnViewportStateChanged;
        State.StateChanged -= OnStateChanged;
        Images.Dispose();
    }
}
