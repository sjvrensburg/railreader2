using CommunityToolkit.Mvvm.ComponentModel;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using SkiaSharp;

namespace RailReader2.ViewModels;

/// <summary>
/// Thin Avalonia wrapper around <see cref="DocumentState"/>.
/// Surfaces [ObservableProperty] for data binding and delegates all logic.
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

    public string FilePath => State.FilePath;
    public int PageCount => State.PageCount;
    public PdfService Pdf => State.Pdf;
    public Camera Camera => State.Camera;
    public RailNav Rail => State.Rail;
    public Dictionary<int, PageAnalysis> AnalysisCache => State.AnalysisCache;
    public Dictionary<int, PageText> TextCache => State.TextCache;
    public Queue<int> PendingAnalysis => State.PendingAnalysis;
    public List<OutlineEntry> Outline => State.Outline;

    // Annotations — delegated to State
    public AnnotationFile? Annotations
    {
        get => State.Annotations;
        set => State.Annotations = value;
    }
    public bool AnnotationsDirty
    {
        get => State.AnnotationsDirty;
        set => State.AnnotationsDirty = value;
    }
    public Stack<IUndoAction> UndoStack => State.UndoStack;
    public Stack<IUndoAction> RedoStack => State.RedoStack;

    // Cached bitmaps — delegated to State
    public SKBitmap? CachedBitmap => State.CachedBitmap;
    public SKImage? CachedImage => State.CachedImage;
    public int CachedDpi => State.CachedDpi;
    public SKBitmap? MinimapBitmap => State.MinimapBitmap;

    public bool DpiRenderReady
    {
        get => State.DpiRenderReady;
        set => State.DpiRenderReady = value;
    }

    public Action? OnDpiRenderComplete
    {
        get => State.OnDpiRenderComplete;
        set => State.OnDpiRenderComplete = value;
    }

    public TabViewModel(string filePath, AppConfig config)
        : this(new DocumentState(filePath, config, new AvaloniaThreadMarshaller()))
    {
    }

    public TabViewModel(DocumentState state)
    {
        State = state;

        // Initialize from state
        _title = state.Title;
        _currentPage = state.CurrentPage;
        _pageWidth = state.PageWidth;
        _pageHeight = state.PageHeight;
        _debugOverlay = state.DebugOverlay;
        _pendingRailSetup = state.PendingRailSetup;

        // Sync state changes back to observable properties
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
        }
    }

    // Sync observable property changes back to state (when set from UI side)
    partial void OnTitleChanged(string value) => State.Title = value;
    partial void OnCurrentPageChanged(int value) => State.CurrentPage = value;
    partial void OnPageWidthChanged(double value) => State.PageWidth = value;
    partial void OnPageHeightChanged(double value) => State.PageHeight = value;
    partial void OnDebugOverlayChanged(bool value) => State.DebugOverlay = value;
    partial void OnPendingRailSetupChanged(bool value) => State.PendingRailSetup = value;

    // All logic delegated to DocumentState
    public void LoadPageBitmap() => State.LoadPageBitmap();
    public bool UpdateRenderDpiIfNeeded() => State.UpdateRenderDpiIfNeeded();
    public void SubmitAnalysis(AnalysisWorker? worker) => State.SubmitAnalysis(worker);
    public void ReapplyNavigableClasses() => State.ReapplyNavigableClasses();
    public void QueueLookahead(int count) => State.QueueLookahead(count);
    public bool SubmitPendingLookahead(AnalysisWorker? worker) => State.SubmitPendingLookahead(worker);
    public void GoToPage(int page, AnalysisWorker? worker, double ww, double wh) => State.GoToPage(page, worker, ww, wh);
    public void CenterPage(double ww, double wh) => State.CenterPage(ww, wh);
    public void FitWidth(double ww, double wh) => State.FitWidth(ww, wh);
    public void ClampCamera(double ww, double wh) => State.ClampCamera(ww, wh);
    public void ApplyZoom(double newZoom, double ww, double wh) => State.ApplyZoom(newZoom, ww, wh);
    public void UpdateRailZoom(double ww, double wh) => State.UpdateRailZoom(ww, wh);
    public void StartSnap(double ww, double wh) => State.StartSnap(ww, wh);
    public void LoadAnnotations() => State.LoadAnnotations();
    public void SaveAnnotations() => State.SaveAnnotations();
    public void MarkAnnotationsDirty() => State.MarkAnnotationsDirty();
    public void AddAnnotation(int page, Annotation annotation) => State.AddAnnotation(page, annotation);
    public void UpdateAnnotationText(int page, TextNoteAnnotation note, string newText) => State.UpdateAnnotationText(page, note, newText);
    public void RemoveAnnotation(int page, Annotation annotation) => State.RemoveAnnotation(page, annotation);
    public void Undo() => State.Undo();
    public void Redo() => State.Redo();
    public PageText GetOrExtractText(int pageIndex) => State.GetOrExtractText(pageIndex);

    public void Dispose()
    {
        State.StateChanged -= OnStateChanged;
        State.Dispose();
    }
}
