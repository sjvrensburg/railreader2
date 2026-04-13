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
        _marshaller.AssertUIThread();
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
    private readonly Dictionary<int, PageAnalysis> _analysisCache = [];
    private readonly Dictionary<int, PageText> _textCache = [];
    private readonly Dictionary<int, List<PdfLink>> _linkCache = [];
    public IReadOnlyDictionary<int, PageAnalysis> AnalysisCache => _analysisCache;
    public IReadOnlyDictionary<int, PageText> TextCache => _textCache;
    public IReadOnlyDictionary<int, List<PdfLink>> LinkCache => _linkCache;
    public Queue<int> PendingAnalysis { get; } = new();
    internal BackgroundAnalysisQueue BackgroundQueue { get; private set; } = null!;

    /// <summary>Number of pages with cached analysis results.</summary>
    public int AnalysedPageCount => _analysisCache.Count;

    /// <summary>Whether this document has unanalysed pages remaining.</summary>
    public bool HasPendingBackgroundWork => !BackgroundQueue.IsExhausted;

    /// <summary>Fires on the UI thread when a new page analysis result is cached.</summary>
    public event Action? AnalysisCacheUpdated;

    /// <summary>
    /// When set, this page was reached via rail navigation and should be
    /// skipped if analysis reveals no navigable blocks. Cleared on landing.
    /// </summary>
    public PendingPageSkip? PendingSkip { get; set; }
    public List<OutlineEntry> Outline { get; }

    // Navigation history (back/forward) — per-document so tab switching doesn't cross-pollinate
    private readonly Stack<int> _backStack = new();
    private readonly Stack<int> _forwardStack = new();
    public int BackStackCount => _backStack.Count;
    public int ForwardStackCount => _forwardStack.Count;
    public int PeekBack() => _backStack.Peek();
    public int PeekForward() => _forwardStack.Peek();

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
        BackgroundQueue = new BackgroundAnalysisQueue(PageCount);
    }

    /// <summary>
    /// Renders the current page bitmap. Safe to call from a background thread.
    /// Does NOT submit analysis (which requires UI-thread access to the worker).
    /// Returns false if the page could not be rendered.
    /// Uses prefetched bitmap if available for the current page (seamless auto-scroll transitions).
    /// </summary>
    public bool LoadPageBitmap()
    {
        var oldPage = CachedPage;
        var oldMinimap = MinimapPage;

        try
        {
            // Use prefetched page if available (e.g. from auto-scroll lookahead).
            if (_prefetched is { } pf && pf.PageIndex == CurrentPage)
            {
                CachedPage = pf.Page;
                CachedDpi = pf.Dpi;
                MinimapPage = pf.Minimap;
                PageWidth = pf.PageWidth;
                PageHeight = pf.PageHeight;
                _prefetched = null; // consumed — don't dispose, we're using the bitmaps
                oldPage?.Dispose();
                oldMinimap?.Dispose();
                return true;
            }

            var (w, h) = _pdf.GetPageSize(CurrentPage);
            int dpi = CalculateRenderDpi(Camera.Zoom);
            var newPage = _pdf.RenderPage(CurrentPage, dpi);
            var newMinimap = _pdf.RenderThumbnail(CurrentPage);

            // Commit: swap fields and dispose old bitmaps only after full success
            CachedPage = newPage;
            CachedDpi = dpi;
            MinimapPage = newMinimap;
            PageWidth = w;
            PageHeight = h;
            oldPage?.Dispose();
            oldMinimap?.Dispose();
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error($"Failed to render page {CurrentPage + 1}: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    /// Schedules background rendering of the specified page for seamless auto-scroll
    /// page transitions. The prefetched bitmap is consumed by the next LoadPageBitmap()
    /// call if it targets the same page. No-op if a prefetch is already pending or
    /// the page is out of range.
    /// </summary>
    internal void PrefetchPage(int pageIndex)
    {
        // Serialize with DPI re-render to avoid concurrent PDFium access.
        if (_prefetchPending || _dpiRenderPending) return;
        if (pageIndex < 0 || pageIndex >= PageCount || IsDisposed) return;
        if (_prefetched?.PageIndex == pageIndex) return;

        _prefetchPending = true;
        int dpi = CalculateRenderDpi(Camera.Zoom);
        var ct = _cts.Token;

        Task.Run(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                _logger.Debug($"[PDFium] prefetch pg {pageIndex} @ {dpi}dpi tid={Environment.CurrentManagedThreadId} file={Path.GetFileName(FilePath)}");
                var (w, h) = _pdf.GetPageSize(pageIndex);
                var page = _pdf.RenderPage(pageIndex, dpi);
                var minimap = _pdf.RenderThumbnail(pageIndex);

                _marshaller.Post(() =>
                {
                    if (IsDisposed)
                    {
                        page.Dispose();
                        minimap.Dispose();
                        _prefetchPending = false;
                        return;
                    }

                    _prefetched?.Dispose();
                    _prefetched = new(pageIndex, dpi, page, minimap, w, h);
                    _prefetchPending = false;
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.Error($"Failed to prefetch page {pageIndex + 1}: {ex.Message}", ex);
                try { _marshaller.Post(() => _prefetchPending = false); }
                catch (Exception postEx)
                {
                    _logger.Warn($"Marshaller post failed resetting prefetch flag: {postEx.Message}");
                    _prefetchPending = false;
                }
            }
        }, ct);
    }

    private bool _dpiRenderPending;

    // Page prefetch for seamless auto-scroll page transitions.
    private sealed record PrefetchedPageData(
        int PageIndex, int Dpi, IRenderedPage Page, IRenderedPage Minimap,
        double PageWidth, double PageHeight) : IDisposable
    {
        public void Dispose() { Page.Dispose(); Minimap.Dispose(); }
    }

    private PrefetchedPageData? _prefetched;
    private bool _prefetchPending;

    /// <summary>
    /// Set to true when a DPI re-render completes. The next animation frame
    /// picks this up and invalidates the page layer atomically with the
    /// camera update, avoiding mid-frame bitmap swaps.
    /// </summary>
    public bool DpiRenderReady { get; internal set; }

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
        // Serialize with prefetch to avoid concurrent PDFium access.
        if (_dpiRenderPending || _prefetchPending) return false;

        // Skip DPI re-renders while the user is actively scrolling. PDFium runs
        // under a process-wide gate; a 100-200ms re-render at high zoom blocks
        // any subsequent text/link extraction the scroll path may need and the
        // bitmap-swap defers a frame. Re-attempt fires from the animation tick
        // once scroll velocity drops to zero.
        if (Rail.ScrollSpeed > 0.1 || Rail.AutoScrolling) return false;

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
                    _logger.Debug($"[PDFium] dpi-rerender pg {page} @ {neededDpi}dpi tid={Environment.CurrentManagedThreadId} file={Path.GetFileName(FilePath)}");
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
                    try { _marshaller.Post(() => _dpiRenderPending = false); }
                    catch (Exception postEx)
                    {
                        _logger.Warn($"Marshaller post failed resetting DPI render flag: {postEx.Message}");
                        _dpiRenderPending = false;
                    }
                }
            }, ct);
            return true;
        }
        return false;
    }

    public void SubmitAnalysis(AnalysisWorker? worker, HashSet<int> navigableClasses)
    {
        if (_analysisCache.TryGetValue(CurrentPage, out var cached))
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
            _logger.Debug($"[PDFium] analysis-pixmap pg {page} tid={Environment.CurrentManagedThreadId} file={Path.GetFileName(FilePath)}");
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
        if (_analysisCache.TryGetValue(CurrentPage, out var cached))
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
        if (_analysisCache.Count < PageCount)
            BackgroundQueue.Reset(CurrentPage);
        for (int i = 1; i <= count; i++)
        {
            int page = CurrentPage + i;
            if (page < PageCount && !_analysisCache.ContainsKey(page))
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
            if (_analysisCache.ContainsKey(page) || worker.IsInFlight(FilePath, page)) continue;

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

    /// <summary>
    /// Submits the next background analysis page (outside the lookahead window).
    /// Returns true if a page was submitted, false if exhausted or worker busy.
    /// Renders the pixmap synchronously on the UI thread to avoid concurrent
    /// PDFium access — the pixmap is only 800x800 so this takes ~5ms.
    /// </summary>
    public bool SubmitBackgroundAnalysis(AnalysisWorker worker)
    {
        _marshaller.AssertUIThread();
        if (!worker.IsIdle) return false;
        if (PendingRailSetup) return false;
        if (BackgroundQueue.IsExhausted) return false;

        int? nextPage = BackgroundQueue.TryGetNext(
            _analysisCache, page => worker.IsInFlight(FilePath, page));
        if (nextPage is not { } page) return false;

        try
        {
            var (pageW, pageH) = _pdf.GetPageSize(page);
            var (rgb, pxW, pxH) = _pdf.RenderPagePixmap(page, LayoutConstants.InputSize);
            worker.Submit(new AnalysisRequest(FilePath, page, rgb, pxW, pxH, pageW, pageH));
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"Background analysis prepare failed for page {page + 1}: {ex.Message}", ex);
            return false;
        }
    }

    public bool GoToPage(int page, AnalysisWorker? worker, HashSet<int> navigableClasses, double windowWidth, double windowHeight)
    {
        page = Math.Clamp(page, 0, PageCount - 1);
        if (page == CurrentPage) return true;

        int oldPage = CurrentPage;
        double oldZoom = Camera.Zoom;

        // Clear stale state from the previous page — the background task
        // for the old page will check CurrentPage != page and discard its
        // result, so these flags must not linger.
        ClearPendingState();

        CurrentPage = page;
        if (!LoadPageBitmap())
        {
            CurrentPage = oldPage;
            return false;
        }
        SubmitAnalysis(worker, navigableClasses);
        Camera.Zoom = oldZoom;
        ClampCamera(windowWidth, windowHeight);
        return true;
    }

    /// <summary>
    /// Clears transient state tied to the current page. Call before
    /// navigating away so that stale flags don't leak across pages.
    /// </summary>
    internal void ClearPendingState()
    {
        PendingRailSetup = false;
        PendingSkip = null;
        _prefetched?.Dispose();
        _prefetched = null;
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

    /// <summary>
    /// Generation counter incremented on every annotation mutation.
    /// Used by the UI to detect changes without deep comparison.
    /// </summary>
    public int AnnotationGeneration { get; private set; }

    public void MarkAnnotationsDirty()
    {
        AnnotationGeneration++;
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
    /// Must be called on the UI thread (PDFium is not thread-safe).
    /// </summary>
    public PageText GetOrExtractText(int pageIndex)
    {
        _marshaller.AssertUIThread();
        if (_textCache.TryGetValue(pageIndex, out var cached))
            return cached;
        var text = _pdfText.ExtractPageText(_pdf.PdfBytes, pageIndex);
        _textCache[pageIndex] = text;
        return text;
    }

    /// <summary>
    /// Returns cached links for a page, extracting them on first access.
    /// Must be called on the UI thread (PDFium is not thread-safe).
    /// </summary>
    public List<PdfLink> GetOrExtractLinks(int pageIndex)
    {
        _marshaller.AssertUIThread();
        if (_linkCache.TryGetValue(pageIndex, out var cached))
            return cached;
        var links = PdfLinkService.ExtractPageLinks(_pdf.PdfBytes, pageIndex);
        _linkCache[pageIndex] = links;
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

    // --- Cache mutation methods ---

    internal void SetAnalysis(int page, PageAnalysis analysis)
    {
        _marshaller.AssertUIThread();
        _analysisCache[page] = analysis;
        AnalysisCacheUpdated?.Invoke();
    }

    internal void SetText(int page, PageText text)
    {
        _marshaller.AssertUIThread();
        _textCache[page] = text;
    }

    internal void SetLinks(int page, List<PdfLink> links)
    {
        _marshaller.AssertUIThread();
        _linkCache[page] = links;
    }

    // --- Navigation history mutation ---

    internal void PushHistory(int currentPage)
    {
        _marshaller.AssertUIThread();
        _backStack.Push(currentPage);
        _forwardStack.Clear();
    }

    internal int PopBack(int currentPage)
    {
        _marshaller.AssertUIThread();
        _forwardStack.Push(currentPage);
        return _backStack.Pop();
    }

    internal int PopForward(int currentPage)
    {
        _marshaller.AssertUIThread();
        _backStack.Push(currentPage);
        return _forwardStack.Pop();
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
        if (IsDisposed) return;
        IsDisposed = true;
        _cts.Cancel();
        _annotationManager?.Release(FilePath);
        var page = CachedPage;
        CachedPage = null;
        page?.Dispose();
        var mm = MinimapPage;
        MinimapPage = null;
        mm?.Dispose();
        _prefetched?.Dispose();
        _prefetched = null;
        _cts.Dispose();

        StateChanged = null;
        AnalysisCacheUpdated = null;
        OnDpiRenderComplete = null;
    }
}
