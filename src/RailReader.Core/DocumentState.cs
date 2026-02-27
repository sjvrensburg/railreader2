using RailReader.Core.Models;
using RailReader.Core.Services;
using SkiaSharp;

namespace RailReader.Core;

/// <summary>
/// Per-document state: PDF, camera, rail nav, analysis cache, annotations.
/// UI-free — no Avalonia dependency.
/// </summary>
public sealed class DocumentState : IDisposable
{
    private readonly PdfService _pdf;
    private readonly AppConfig _config;
    private readonly IThreadMarshaller _marshaller;

    private string _title;
    private int _currentPage;
    private double _pageWidth;
    private double _pageHeight;
    private bool _debugOverlay;
    private bool _pendingRailSetup;

    /// <summary>Fires when a property changes. Parameter is the property name.</summary>
    public Action<string>? StateChanged;

    public string Title
    {
        get => _title;
        set { if (_title != value) { _title = value; StateChanged?.Invoke(nameof(Title)); } }
    }

    public int CurrentPage
    {
        get => _currentPage;
        set { if (_currentPage != value) { _currentPage = value; StateChanged?.Invoke(nameof(CurrentPage)); } }
    }

    public double PageWidth
    {
        get => _pageWidth;
        set { if (_pageWidth != value) { _pageWidth = value; StateChanged?.Invoke(nameof(PageWidth)); } }
    }

    public double PageHeight
    {
        get => _pageHeight;
        set { if (_pageHeight != value) { _pageHeight = value; StateChanged?.Invoke(nameof(PageHeight)); } }
    }

    public bool DebugOverlay
    {
        get => _debugOverlay;
        set { if (_debugOverlay != value) { _debugOverlay = value; StateChanged?.Invoke(nameof(DebugOverlay)); } }
    }

    public bool PendingRailSetup
    {
        get => _pendingRailSetup;
        set { if (_pendingRailSetup != value) { _pendingRailSetup = value; StateChanged?.Invoke(nameof(PendingRailSetup)); } }
    }

    public string FilePath { get; }
    public int PageCount { get; }
    public PdfService Pdf => _pdf;
    public Camera Camera { get; } = new();
    public RailNav Rail { get; }
    public Dictionary<int, PageAnalysis> AnalysisCache { get; } = [];
    public Dictionary<int, PageText> TextCache { get; } = [];
    public Queue<int> PendingAnalysis { get; } = new();
    public List<OutlineEntry> Outline { get; }

    // Annotations
    public AnnotationFile? Annotations { get; set; }
    public bool AnnotationsDirty { get; set; }
    public Stack<IUndoAction> UndoStack { get; } = new();
    public Stack<IUndoAction> RedoStack { get; } = new();
    private Timer? _autoSaveTimer;

    // Cached page bitmap, GPU-ready image, and the DPI it was rendered at
    public SKBitmap? CachedBitmap { get; private set; }
    public SKImage? CachedImage { get; private set; }
    public int CachedDpi { get; private set; }

    // Small pre-scaled thumbnail used by the minimap (≤200×280 px).
    public SKBitmap? MinimapBitmap { get; private set; }

    public DocumentState(string filePath, AppConfig config, IThreadMarshaller marshaller)
    {
        _config = config;
        _marshaller = marshaller;
        FilePath = filePath;
        _pdf = new PdfService(filePath);
        PageCount = _pdf.PageCount;
        _title = Path.GetFileName(filePath);
        Rail = new RailNav(config);
        Outline = _pdf.Outline;
    }

    /// <summary>
    /// Renders the current page bitmap. Safe to call from a background thread.
    /// Does NOT submit analysis (which requires UI-thread access to the worker).
    /// </summary>
    public void LoadPageBitmap()
    {
        try
        {
            CachedImage = null;
            CachedBitmap = null;
            MinimapBitmap = null;

            var (w, h) = _pdf.GetPageSize(CurrentPage);
            PageWidth = w;
            PageHeight = h;

            int dpi = PdfService.CalculateRenderDpi(Camera.Zoom);
            CachedBitmap = _pdf.RenderPage(CurrentPage, dpi);
            CachedImage = SKImage.FromBitmap(CachedBitmap);
            CachedDpi = dpi;
            MinimapBitmap = _pdf.RenderThumbnail(CurrentPage);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to render page {CurrentPage}: {ex.Message}");
            CachedBitmap = null;
        }
    }

    private bool _dpiRenderPending;

    /// <summary>
    /// Set to true when a DPI re-render completes. The next animation frame
    /// picks this up and invalidates the page layer atomically with the
    /// camera update, avoiding mid-frame bitmap swaps.
    /// </summary>
    public bool DpiRenderReady { get; set; }

    /// <summary>
    /// Called on the UI thread when a DPI re-render completes, so the view can
    /// request an animation frame to pick up the new bitmap.
    /// </summary>
    public Action? OnDpiRenderComplete { get; set; }

    /// <summary>
    /// Checks if the current zoom demands a different DPI and schedules an
    /// async re-render on a background thread.
    /// </summary>
    public bool UpdateRenderDpiIfNeeded()
    {
        if (_dpiRenderPending) return false;

        int neededDpi = PdfService.CalculateRenderDpi(Camera.Zoom);
        if (neededDpi > CachedDpi * 1.5 || (neededDpi < CachedDpi * 0.5 && CachedDpi > 150))
        {
            _dpiRenderPending = true;
            int page = CurrentPage;
            Task.Run(() =>
            {
                try
                {
                    var newBitmap = _pdf.RenderPage(page, neededDpi);
                    _marshaller.Post(() =>
                    {
                        if (CurrentPage == page)
                        {
                            var newImage = SKImage.FromBitmap(newBitmap);
                            var oldImage = CachedImage;
                            var oldBitmap = CachedBitmap;
                            CachedBitmap = newBitmap;
                            CachedImage = newImage;
                            CachedDpi = neededDpi;
                            DpiRenderReady = true;
                            oldImage?.Dispose();
                            oldBitmap?.Dispose();
                            OnDpiRenderComplete?.Invoke();
                        }
                        else
                        {
                            newBitmap.Dispose();
                        }
                        _dpiRenderPending = false;
                    });
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Failed to re-render page at {neededDpi} DPI: {ex.Message}");
                    _marshaller.Post(() => _dpiRenderPending = false);
                }
            });
            return true;
        }
        return false;
    }

    public void SubmitAnalysis(AnalysisWorker? worker)
    {
        if (AnalysisCache.TryGetValue(CurrentPage, out var cached))
        {
            Console.Error.WriteLine($"[SubmitAnalysis] Page {CurrentPage}: cache hit, {cached.Blocks.Count} blocks");
            ApplyAnalysis(cached);
            return;
        }

        if (worker is null) return;

        if (worker.IsInFlight(FilePath, CurrentPage))
        {
            Console.Error.WriteLine($"[SubmitAnalysis] Page {CurrentPage}: already in flight");
            PendingRailSetup = true;
            return;
        }

        int page = CurrentPage;
        double pageW = PageWidth, pageH = PageHeight;
        string filePath = FilePath;
        PendingRailSetup = true;

        Console.Error.WriteLine($"[SubmitAnalysis] Page {page}: scheduling pixmap on background thread...");
        Task.Run(() =>
        {
            try
            {
                var (rgb, pxW, pxH) = _pdf.RenderPagePixmap(page, LayoutConstants.InputSize);
                Console.Error.WriteLine($"[SubmitAnalysis] Page {page}: pixmap ready {pxW}x{pxH}, submitting...");
                _marshaller.Post(() =>
                {
                    if (CurrentPage != page) return;
                    worker.Submit(new AnalysisRequest
                    {
                        FilePath = filePath,
                        Page = page,
                        RgbBytes = rgb,
                        PxW = pxW,
                        PxH = pxH,
                        PageW = pageW,
                        PageH = pageH,
                    });
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to prepare analysis input: {ex.Message}");
                _marshaller.Post(() => PendingRailSetup = false);
            }
        });
    }

    public void ReapplyNavigableClasses()
    {
        if (AnalysisCache.TryGetValue(CurrentPage, out var cached))
            Rail.SetAnalysis(cached, _config.NavigableClasses);
    }

    private void ApplyAnalysis(PageAnalysis analysis)
    {
        Rail.SetAnalysis(analysis, _config.NavigableClasses);
        PendingRailSetup = false;
    }

    public void QueueLookahead(int count)
    {
        PendingAnalysis.Clear();
        for (int i = 1; i <= count; i++)
        {
            int page = CurrentPage + i;
            if (page < PageCount && !AnalysisCache.ContainsKey(page))
                PendingAnalysis.Enqueue(page);
        }
    }

    public bool SubmitPendingLookahead(AnalysisWorker? worker)
    {
        if (worker is null || !worker.IsIdle) return false;
        if (PendingRailSetup) return false;

        while (PendingAnalysis.Count > 0)
        {
            int page = PendingAnalysis.Dequeue();
            if (AnalysisCache.ContainsKey(page) || worker.IsInFlight(FilePath, page)) continue;

            try
            {
                var (rgb, pxW, pxH) = _pdf.RenderPagePixmap(page, LayoutConstants.InputSize);
                worker.Submit(new AnalysisRequest
                {
                    FilePath = FilePath, Page = page, RgbBytes = rgb, PxW = pxW, PxH = pxH,
                    PageW = PageWidth, PageH = PageHeight,
                });
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Lookahead prepare failed for page {page + 1}: {ex.Message}");
            }
        }
        return false;
    }

    public void GoToPage(int page, AnalysisWorker? worker, double windowWidth, double windowHeight)
    {
        page = Math.Clamp(page, 0, PageCount - 1);
        if (page == CurrentPage) return;

        double oldZoom = Camera.Zoom;
        CurrentPage = page;
        LoadPageBitmap();
        SubmitAnalysis(worker);
        Camera.Zoom = oldZoom;
        ClampCamera(windowWidth, windowHeight);
    }

    public void CenterPage(double windowWidth, double windowHeight)
    {
        if (PageWidth <= 0 || PageHeight <= 0 || windowWidth <= 0 || windowHeight <= 0) return;
        double scaleX = windowWidth / PageWidth;
        double scaleY = windowHeight / PageHeight;
        Camera.Zoom = Math.Min(scaleX, scaleY);
        double scaledW = PageWidth * Camera.Zoom;
        double scaledH = PageHeight * Camera.Zoom;
        Camera.OffsetX = (windowWidth - scaledW) / 2.0;
        Camera.OffsetY = (windowHeight - scaledH) / 2.0;
    }

    public void FitWidth(double windowWidth, double windowHeight)
    {
        if (PageWidth <= 0 || windowWidth <= 0) return;
        Camera.Zoom = Math.Clamp(windowWidth / PageWidth, Camera.ZoomMin, Camera.ZoomMax);
        double scaledW = PageWidth * Camera.Zoom;
        double scaledH = PageHeight * Camera.Zoom;
        Camera.OffsetX = (windowWidth - scaledW) / 2.0;
        Camera.OffsetY = scaledH <= windowHeight ? (windowHeight - scaledH) / 2.0 : 0;
    }

    public void ClampCamera(double windowWidth, double windowHeight)
    {
        double scaledW = PageWidth * Camera.Zoom;
        double scaledH = PageHeight * Camera.Zoom;

        if (scaledW <= windowWidth)
            Camera.OffsetX = (windowWidth - scaledW) / 2.0;
        else
            Camera.OffsetX = Math.Clamp(Camera.OffsetX, windowWidth - scaledW, 0);

        if (scaledH <= windowHeight)
            Camera.OffsetY = (windowHeight - scaledH) / 2.0;
        else
            Camera.OffsetY = Math.Clamp(Camera.OffsetY, windowHeight - scaledH, 0);
    }

    public void ApplyZoom(double newZoom, double windowWidth, double windowHeight)
    {
        Camera.Zoom = Math.Clamp(newZoom, Camera.ZoomMin, Camera.ZoomMax);
        UpdateRailZoom(windowWidth, windowHeight);
        if (Rail.Active)
            StartSnap(windowWidth, windowHeight);
        ClampCamera(windowWidth, windowHeight);
    }

    public void UpdateRailZoom(double windowWidth, double windowHeight)
    {
        Rail.UpdateZoom(Camera.Zoom, Camera.OffsetX, Camera.OffsetY, windowWidth, windowHeight);
    }

    public void StartSnap(double windowWidth, double windowHeight)
    {
        Rail.StartSnapToCurrent(Camera.OffsetX, Camera.OffsetY, Camera.Zoom, windowWidth, windowHeight);
    }

    public void LoadAnnotations()
    {
        Annotations = AnnotationService.Load(FilePath) ?? new AnnotationFile
        {
            SourcePdf = Path.GetFileName(FilePath),
        };
    }

    public void SaveAnnotations()
    {
        if (Annotations is not null && AnnotationsDirty)
        {
            bool hasAnnotations = Annotations.Pages.Values.Any(list => list.Count > 0);
            var sidecarPath = AnnotationService.GetSidecarPath(FilePath);
            if (hasAnnotations)
            {
                AnnotationService.Save(FilePath, Annotations);
            }
            else if (File.Exists(sidecarPath))
            {
                try { File.Delete(sidecarPath); }
                catch { /* ignore */ }
            }
            AnnotationsDirty = false;
        }
    }

    public void MarkAnnotationsDirty()
    {
        AnnotationsDirty = true;
        // Debounced auto-save: restart the timer on each modification
        _autoSaveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _autoSaveTimer?.Dispose();
        _autoSaveTimer = new Timer(_ => _marshaller.Post(SaveAnnotations), null, 1000, Timeout.Infinite);
    }

    public void AddAnnotation(int page, Annotation annotation)
    {
        if (Annotations is null) return;
        if (!Annotations.Pages.TryGetValue(page, out var list))
        {
            list = [];
            Annotations.Pages[page] = list;
        }
        list.Add(annotation);

        var action = new AddAnnotationAction(page, annotation);
        UndoStack.Push(action);
        RedoStack.Clear();
        MarkAnnotationsDirty();
    }

    public void UpdateAnnotationText(int page, TextNoteAnnotation note, string newText)
    {
        note.Text = newText;
        MarkAnnotationsDirty();
    }

    public void PushUndoAction(IUndoAction action)
    {
        UndoStack.Push(action);
        RedoStack.Clear();
        MarkAnnotationsDirty();
    }

    public void RemoveAnnotation(int page, Annotation annotation)
    {
        if (Annotations is null) return;
        var action = new RemoveAnnotationAction(page, annotation);
        action.Redo(Annotations);

        UndoStack.Push(action);
        RedoStack.Clear();
        MarkAnnotationsDirty();
    }

    public void Undo()
    {
        if (UndoStack.Count == 0 || Annotations is null) return;
        var action = UndoStack.Pop();
        action.Undo(Annotations);
        RedoStack.Push(action);
        MarkAnnotationsDirty();
    }

    public void Redo()
    {
        if (RedoStack.Count == 0 || Annotations is null) return;
        var action = RedoStack.Pop();
        action.Redo(Annotations);
        UndoStack.Push(action);
        MarkAnnotationsDirty();
    }

    /// <summary>
    /// Returns cached text for a page, extracting it on first access.
    /// </summary>
    public PageText GetOrExtractText(int pageIndex)
    {
        if (TextCache.TryGetValue(pageIndex, out var cached))
            return cached;
        var text = PdfTextService.ExtractPageText(_pdf.PdfBytes, pageIndex);
        TextCache[pageIndex] = text;
        return text;
    }

    public void Dispose()
    {
        _autoSaveTimer?.Dispose();
        SaveAnnotations();
        var img = CachedImage;
        CachedImage = null;
        img?.Dispose();
        var bmp = CachedBitmap;
        CachedBitmap = null;
        bmp?.Dispose();
        var mmBmp = MinimapBitmap;
        MinimapBitmap = null;
        mmBmp?.Dispose();
        _pdf.Dispose();
    }
}
