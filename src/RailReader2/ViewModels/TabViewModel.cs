using CommunityToolkit.Mvvm.ComponentModel;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using SkiaSharp;

namespace RailReader2.ViewModels;

/// <summary>
/// Thin Avalonia wrapper around <see cref="DocumentState"/>.
/// Surfaces [ObservableProperty] for data binding and delegates
/// only what the Views actually need.
/// </summary>
public sealed partial class TabViewModel : ObservableObject, IDisposable
{
    public DocumentState State { get; }

    [ObservableProperty] private string _title;
    [ObservableProperty] private int _currentPage;
    [ObservableProperty] private double _pageWidth;
    [ObservableProperty] private double _pageHeight;
    [ObservableProperty] private bool _debugOverlay;
    [ObservableProperty] private bool _pendingRailSetup;
    [ObservableProperty] private ColourEffect _colourEffect;
    [ObservableProperty] private bool _lineFocusBlur;
    [ObservableProperty] private bool _lineHighlightEnabled = true;
    /// <summary>Whether the side panel (outline/bookmarks/search) is visible for this tab.</summary>
    public bool ShowSidePanel { get; set; }

    /// <summary>Width of the side panel in pixels (preserved across tab switches).</summary>
    public double SidePanelWidth { get; set; } = 220;

    // Read-only properties used by Views
    public string FilePath => State.FilePath;
    public int PageCount => State.PageCount;
    public IPdfService Pdf => State.Pdf;
    public Camera Camera => State.Camera;
    public RailNav Rail => State.Rail;
    public IReadOnlyDictionary<int, PageAnalysis> AnalysisCache => State.AnalysisCache;
    public List<OutlineEntry> Outline => State.Outline;
    public AnnotationFile Annotations => State.Annotations;
    public int CachedDpi => State.CachedDpi;

    /// <summary>
    /// Returns the cached SKImage for GPU rendering and the previous image that
    /// was replaced (if any). The caller is responsible for disposing the retired
    /// image on a thread-safe boundary (e.g. the composition thread via OnMessage).
    /// Created on demand from the IRenderedPage; the UI layer owns this because
    /// SKImage.FromBitmap must be called on the UI thread.
    /// </summary>
    public (SKImage? Current, SKImage? Retired) GetCachedImage()
    {
        if (State.CachedPage is SkiaRenderedPage sp)
        {
            if (_cachedImage is null || _cachedImagePage != sp)
            {
                var retired = _cachedImage;
                _cachedImage = SKImage.FromBitmap(sp.Bitmap);
                _cachedImagePage = sp;
                return (_cachedImage, retired);
            }
            return (_cachedImage, null);
        }
        return (null, null);
    }

    /// <summary>
    /// Returns the current cached image without lifecycle management.
    /// Use only when no image transition is expected (e.g. minimap snapshot).
    /// </summary>
    public SKImage? CachedImage => _cachedImage;

    private SKImage? _cachedImage;
    private SkiaRenderedPage? _cachedImagePage;

    public SKBitmap? MinimapBitmap => (State.MinimapPage as SkiaRenderedPage)?.Bitmap;

    public Action? OnDpiRenderComplete
    {
        get => State.OnDpiRenderComplete;
        set => State.OnDpiRenderComplete = value;
    }

    public TabViewModel(DocumentState state)
    {
        State = state;

        _title = state.Title;
        _currentPage = state.CurrentPage;
        _pageWidth = state.PageWidth;
        _pageHeight = state.PageHeight;
        _debugOverlay = state.DebugOverlay;
        _pendingRailSetup = state.PendingRailSetup;
        _colourEffect = state.ColourEffect;
        _lineFocusBlur = state.LineFocusBlur;
        _lineHighlightEnabled = state.LineHighlightEnabled;
        state.StateChanged += OnStateChanged;
    }

    private void OnStateChanged(string propertyName)
    {
        switch (propertyName)
        {
            case nameof(Title): Title = State.Title; break;
            case nameof(CurrentPage): CurrentPage = State.CurrentPage; break;
            case nameof(PageWidth): PageWidth = State.PageWidth; break;
            case nameof(PageHeight): PageHeight = State.PageHeight; break;
            case nameof(DebugOverlay): DebugOverlay = State.DebugOverlay; break;
            case nameof(PendingRailSetup): PendingRailSetup = State.PendingRailSetup; break;
            case nameof(ColourEffect): ColourEffect = State.ColourEffect; break;
            case nameof(LineFocusBlur): LineFocusBlur = State.LineFocusBlur; break;
            case nameof(LineHighlightEnabled): LineHighlightEnabled = State.LineHighlightEnabled; break;
        }
    }

    // Sync observable property changes back to state (when set from UI side)
    partial void OnTitleChanged(string value) => State.Title = value;
    partial void OnCurrentPageChanged(int value) => State.CurrentPage = value;
    partial void OnPageWidthChanged(double value) => State.PageWidth = value;
    partial void OnPageHeightChanged(double value) => State.PageHeight = value;
    partial void OnDebugOverlayChanged(bool value) => State.DebugOverlay = value;
    partial void OnPendingRailSetupChanged(bool value) => State.PendingRailSetup = value;
    partial void OnColourEffectChanged(ColourEffect value) => State.ColourEffect = value;
    partial void OnLineFocusBlurChanged(bool value) => State.LineFocusBlur = value;
    partial void OnLineHighlightEnabledChanged(bool value) => State.LineHighlightEnabled = value;
    // Methods used by Views (MinimapControl, MainWindow)
    public void CenterPage(double ww, double wh) => State.CenterPage(ww, wh);
    public void ClampCamera(double ww, double wh) => State.ClampCamera(ww, wh);
    public void UpdateRailZoom(double ww, double wh) => State.UpdateRailZoom(ww, wh);
    public void StartSnap(double ww, double wh) => State.StartSnap(ww, wh);
    public void LoadAnnotations(AnnotationFileManager manager) => State.LoadAnnotations(manager);
    public bool SubmitPendingLookahead(AnalysisWorker? worker) => State.SubmitPendingLookahead(worker);

    public void Dispose()
    {
        State.StateChanged -= OnStateChanged;
        _cachedImage?.Dispose();
        _cachedImage = null;
        _cachedImagePage = null;
        State.Dispose();
    }
}
