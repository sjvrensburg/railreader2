using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Core;

/// <summary>
/// Per-document state: PDF, camera, rail nav, analysis cache, annotations.
/// UI-free — no Avalonia dependency.
/// </summary>
public sealed class DocumentState : IDisposable
{
    private readonly IPdfService _pdf;
    private readonly IPdfTextService _pdfText;
    private readonly IThreadMarshaller _marshaller;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    internal bool IsDisposed { get; private set; }

    private string _title;
    private int _currentPage;
    private double _pageWidth;
    private double _pageHeight;
    private bool _debugOverlay;
    private bool _pendingRailSetup;
    private ColourEffect _colourEffect;
    private bool _lineFocusBlur;
    private bool _lineHighlightEnabled = true;
    /// <summary>Fires when a property changes. Parameter is the property name.</summary>
    public Action<string>? StateChanged;

    /// <summary>Sets a backing field and fires StateChanged if the value changed.</summary>
    private bool SetField<T>(ref T field, T value, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        StateChanged?.Invoke(propertyName);
        return true;
    }

    public string Title
    {
        get => _title;
        set => SetField(ref _title, value, nameof(Title));
    }

    public int CurrentPage
    {
        get => _currentPage;
        set => SetField(ref _currentPage, value, nameof(CurrentPage));
    }

    public double PageWidth
    {
        get => _pageWidth;
        set => SetField(ref _pageWidth, value, nameof(PageWidth));
    }

    public double PageHeight
    {
        get => _pageHeight;
        set => SetField(ref _pageHeight, value, nameof(PageHeight));
    }

    public bool DebugOverlay
    {
        get => _debugOverlay;
        set => SetField(ref _debugOverlay, value, nameof(DebugOverlay));
    }

    public bool PendingRailSetup
    {
        get => _pendingRailSetup;
        set => SetField(ref _pendingRailSetup, value, nameof(PendingRailSetup));
    }

    public ColourEffect ColourEffect
    {
        get => _colourEffect;
        set => SetField(ref _colourEffect, value, nameof(ColourEffect));
    }

    public bool LineFocusBlur
    {
        get => _lineFocusBlur;
        set => SetField(ref _lineFocusBlur, value, nameof(LineFocusBlur));
    }

    public bool LineHighlightEnabled
    {
        get => _lineHighlightEnabled;
        set => SetField(ref _lineHighlightEnabled, value, nameof(LineHighlightEnabled));
    }

    public string FilePath { get; }
    public int PageCount { get; }
    public IPdfService Pdf => _pdf;
    public IPdfTextService PdfText => _pdfText;
    public Camera Camera { get; } = new();
    public RailNav Rail { get; }
    public Dictionary<int, PageAnalysis> AnalysisCache { get; } = [];
    public Dictionary<int, PageText> TextCache { get; } = [];
    public Dictionary<int, List<PdfLink>> LinkCache { get; } = [];
    public Queue<int> PendingAnalysis { get; } = new();

    /// <summary>
    /// When non-zero, indicates that this page was reached via rail navigation
    /// and should be skipped if analysis reveals no navigable blocks.
    /// +1 = forward, -1 = backward.
    /// </summary>
    public int PendingSkipDirection { get; set; }

    /// <summary>
    /// Number of pages already skipped in the current skip sequence.
    /// Used to show a cumulative count in the skip notification.
    /// </summary>
    public int PendingSkipCount { get; set; }
    public List<OutlineEntry> Outline { get; }

    // Navigation history (back/forward) — per-document so tab switching doesn't cross-pollinate
    public Stack<int> BackStack { get; } = new();
    public Stack<int> ForwardStack { get; } = new();

    // Annotations (shared via AnnotationFileManager when set)
    public AnnotationFile Annotations { get; set; } = new();
    public Stack<IUndoAction> UndoStack { get; } = new();
    public Stack<IUndoAction> RedoStack { get; } = new();
    private AnnotationFileManager? _annotationManager;

    // Cached rendered page and the DPI it was rendered at
    public IRenderedPage? CachedPage { get; private set; }
    public int CachedDpi { get; private set; }

    // Small pre-scaled thumbnail used by the minimap (≤200×280 px).
    public IRenderedPage? MinimapPage { get; private set; }

    public DocumentState(string filePath, IPdfService pdf, IPdfTextService pdfText,
        AppConfig config, IThreadMarshaller marshaller, ILogger? logger = null)
    {
        _marshaller = marshaller;
        _logger = logger ?? NullLogger.Instance;
        FilePath = filePath;
        _pdf = pdf;
        _pdfText = pdfText;
        PageCount = _pdf.PageCount;
        _title = Path.GetFileName(filePath);
        _colourEffect = config.ColourEffect;
        _lineFocusBlur = config.LineFocusBlur;
        _lineHighlightEnabled = config.LineHighlightEnabled;
        Rail = new RailNav(config);
        Outline = _pdf.Outline;
    }

    /// <summary>
    /// Renders the current page bitmap. Safe to call from a background thread.
    /// Does NOT submit analysis (which requires UI-thread access to the worker).
    /// </summary>
    public void LoadPageBitmap()
    {
        var oldPage = CachedPage;
        var oldMinimap = MinimapPage;
        CachedPage = null;
        MinimapPage = null;
        oldPage?.Dispose();
        oldMinimap?.Dispose();

        var (w, h) = _pdf.GetPageSize(CurrentPage);
        PageWidth = w;
        PageHeight = h;

        int dpi = CalculateRenderDpi(Camera.Zoom);
        CachedPage = _pdf.RenderPage(CurrentPage, dpi);
        CachedDpi = dpi;
        MinimapPage = _pdf.RenderThumbnail(CurrentPage);
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

        int neededDpi = CalculateRenderDpi(Camera.Zoom);
        if (neededDpi > CachedDpi * 1.5 || (neededDpi < CachedDpi * 0.5 && CachedDpi > 150))
        {
            _dpiRenderPending = true;
            int page = CurrentPage;
            var ct = _cts.Token;
            Task.Run(() =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var newPage = _pdf.RenderPage(page, neededDpi);
                    _marshaller.Post(() =>
                    {
                        if (IsDisposed || CurrentPage != page)
                        {
                            newPage.Dispose();
                            _dpiRenderPending = false;
                            return;
                        }
                        var oldPage = CachedPage;
                        CachedPage = newPage;
                        CachedDpi = neededDpi;
                        DpiRenderReady = true;
                        oldPage?.Dispose();
                        OnDpiRenderComplete?.Invoke();
                        _dpiRenderPending = false;
                    });
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.Error($"Failed to re-render page at {neededDpi} DPI: {ex.Message}", ex);
                    _marshaller.Post(() => _dpiRenderPending = false);
                }
            }, ct);
            return true;
        }
        return false;
    }

    public void SubmitAnalysis(AnalysisWorker? worker, HashSet<int> navigableClasses)
    {
        if (AnalysisCache.TryGetValue(CurrentPage, out var cached))
        {
            _logger.Debug($"[SubmitAnalysis] Page {CurrentPage}: cache hit, {cached.Blocks.Count} blocks");
            ApplyAnalysis(cached, navigableClasses);
            return;
        }

        if (worker is null) return;

        if (worker.IsInFlight(FilePath, CurrentPage))
        {
            _logger.Debug($"[SubmitAnalysis] Page {CurrentPage}: already in flight");
            PendingRailSetup = true;
            return;
        }

        int page = CurrentPage;
        double pageW = PageWidth, pageH = PageHeight;
        string filePath = FilePath;
        PendingRailSetup = true;

        _logger.Debug($"[SubmitAnalysis] Page {page}: scheduling pixmap on background thread...");
        var ct = _cts.Token;
        Task.Run(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var (rgb, pxW, pxH) = _pdf.RenderPagePixmap(page, LayoutConstants.InputSize);
                _logger.Debug($"[SubmitAnalysis] Page {page}: pixmap ready {pxW}x{pxH}, submitting...");
                _marshaller.Post(() =>
                {
                    if (IsDisposed || CurrentPage != page) return;
                    worker.Submit(new AnalysisRequest(filePath, page, rgb, pxW, pxH, pageW, pageH));
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.Error($"Failed to prepare analysis input: {ex.Message}", ex);
                _marshaller.Post(() => { if (!IsDisposed) PendingRailSetup = false; });
            }
        }, ct);
    }

    public void ReapplyNavigableClasses(HashSet<int> navigableClasses)
    {
        if (AnalysisCache.TryGetValue(CurrentPage, out var cached))
            Rail.SetAnalysis(cached, navigableClasses);
    }

    private void ApplyAnalysis(PageAnalysis analysis, HashSet<int> navigableClasses)
    {
        Rail.SetAnalysis(analysis, navigableClasses);
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

            string filePath = FilePath;
            double pageW = PageWidth, pageH = PageHeight;
            var ct = _cts.Token;
            Task.Run(() =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var (rgb, pxW, pxH) = _pdf.RenderPagePixmap(page, LayoutConstants.InputSize);
                    _marshaller.Post(() =>
                    {
                        if (!IsDisposed)
                            worker.Submit(new AnalysisRequest(filePath, page, rgb, pxW, pxH, pageW, pageH));
                    });
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.Error($"Lookahead prepare failed for page {page + 1}: {ex.Message}", ex);
                }
            }, ct);
            return true;
        }
        return false;
    }

    public void GoToPage(int page, AnalysisWorker? worker, HashSet<int> navigableClasses, double windowWidth, double windowHeight)
    {
        page = Math.Clamp(page, 0, PageCount - 1);
        if (page == CurrentPage) return;

        double oldZoom = Camera.Zoom;
        CurrentPage = page;
        LoadPageBitmap();
        SubmitAnalysis(worker, navigableClasses);
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

    public void UpdateRailZoom(double windowWidth, double windowHeight,
        double? cursorPageX = null, double? cursorPageY = null)
    {
        Rail.UpdateZoom(Camera.Zoom, Camera.OffsetX, Camera.OffsetY, windowWidth, windowHeight,
            cursorPageX, cursorPageY);
    }

    public void StartSnap(double windowWidth, double windowHeight)
    {
        Rail.StartSnapToCurrent(Camera.OffsetX, Camera.OffsetY, Camera.Zoom, windowWidth, windowHeight);
    }

    public void StartSnapPreservingPosition(double windowWidth, double windowHeight,
        double horizontalFraction, double lineScreenY)
    {
        Rail.StartSnapPreservingPosition(Camera.OffsetX, Camera.OffsetY, Camera.Zoom,
            windowWidth, windowHeight, horizontalFraction, lineScreenY);
    }

    public void StartSnapToEnd(double windowWidth, double windowHeight)
    {
        Rail.StartSnapToCurrentEnd(Camera.OffsetX, Camera.OffsetY, Camera.Zoom, windowWidth, windowHeight);
    }

    public void LoadAnnotations(AnnotationFileManager manager)
    {
        _annotationManager = manager;
        Annotations = manager.Checkout(FilePath);
    }

    public void MarkAnnotationsDirty()
    {
        _annotationManager?.MarkDirty(FilePath);
    }

    // --- Bookmarks ---

    public void AddBookmark(string name, int page)
    {
        Annotations.Bookmarks.Add(new BookmarkEntry { Name = name, Page = page });
        MarkAnnotationsDirty();
    }

    public void RemoveBookmark(int index)
    {
        if (index < 0 || index >= Annotations.Bookmarks.Count) return;
        Annotations.Bookmarks.RemoveAt(index);
        MarkAnnotationsDirty();
    }

    public void RenameBookmark(int index, string newName)
    {
        if (index < 0 || index >= Annotations.Bookmarks.Count) return;
        Annotations.Bookmarks[index].Name = newName;
        MarkAnnotationsDirty();
    }

    public void AddAnnotation(int page, Annotation annotation)
    {
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
        var action = new RemoveAnnotationAction(page, annotation);
        action.Redo(Annotations);

        UndoStack.Push(action);
        RedoStack.Clear();
        MarkAnnotationsDirty();
    }

    public void Undo()
    {
        if (UndoStack.Count == 0) return;
        var action = UndoStack.Pop();
        action.Undo(Annotations);
        RedoStack.Push(action);
        MarkAnnotationsDirty();
    }

    public void Redo()
    {
        if (RedoStack.Count == 0) return;
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
        var text = _pdfText.ExtractPageText(_pdf.PdfBytes, pageIndex);
        TextCache[pageIndex] = text;
        return text;
    }

    /// <summary>
    /// Returns cached links for a page, extracting them on first access.
    /// </summary>
    public List<PdfLink> GetOrExtractLinks(int pageIndex)
    {
        if (LinkCache.TryGetValue(pageIndex, out var cached))
            return cached;
        var links = PdfLinkService.ExtractPageLinks(_pdf.PdfBytes, pageIndex);
        LinkCache[pageIndex] = links;
        return links;
    }

    /// <summary>
    /// Hit-tests a point against PDF links on the current page.
    /// Uses the cached link list for fast in-memory lookup.
    /// </summary>
    public PdfLink? HitTestLink(double pageX, double pageY)
    {
        var links = GetOrExtractLinks(CurrentPage);
        foreach (var link in links)
        {
            if (link.Rect.Contains((float)pageX, (float)pageY))
                return link;
        }
        return null;
    }

    /// <summary>
    /// Calculates the appropriate render DPI for a zoom level.
    /// Pure math — no rendering-library dependency.
    /// </summary>
    public static int CalculateRenderDpi(double zoom)
    {
        int raw = (int)(zoom * 150);
        int rounded = ((raw + 37) / 75) * 75;
        return Math.Clamp(rounded, 150, 600);
    }

    public void Dispose()
    {
        IsDisposed = true;
        _cts.Cancel();
        _annotationManager?.Release(FilePath);
        var page = CachedPage;
        CachedPage = null;
        page?.Dispose();
        var mm = MinimapPage;
        MinimapPage = null;
        mm?.Dispose();
        _cts.Dispose();
    }
}
