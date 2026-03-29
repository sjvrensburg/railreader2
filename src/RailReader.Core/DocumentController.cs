using System.Diagnostics;
using RailReader.Core.Commands;
using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Core;

/// <summary>
/// Result of a single animation tick. Tells the UI what to repaint.
/// </summary>
public record struct TickResult(
    bool CameraChanged,
    bool PageChanged,
    bool OverlayChanged,
    bool SearchChanged,
    bool AnnotationsChanged,
    bool StillAnimating);

/// <summary>
/// Headless controller that owns all document business logic.
/// No Avalonia dependency — can be driven by AI agent, tests, or UI.
/// </summary>
public sealed class DocumentController
{
    private const double PanStep = 50.0;

    private readonly AppConfig _config;
    private readonly IThreadMarshaller _marshaller;
    private readonly IPdfServiceFactory _pdfFactory;
    private readonly ILogger _logger;
    private readonly ZoomAnimationController _zoom;
    private readonly AutoScrollController _autoScroll;
    private readonly AnnotationFileManager _annotationManager;
    private AnalysisWorker? _worker;
    public bool HasWorker => _worker is not null;

    private double _vpWidth = 1200;
    private double _vpHeight = 900;

    public List<DocumentState> Documents { get; } = [];
    public int ActiveDocumentIndex { get; set; }
    public AppConfig Config => _config;
    public ColourEffect ActiveColourEffect => ActiveDocument?.ColourEffect ?? _config.ColourEffect;
    public float ActiveColourIntensity => (float)_config.ColourEffectIntensity;
    public AnalysisWorker? Worker => _worker;
    public AnnotationFileManager AnnotationManager => _annotationManager;

    public DocumentState? ActiveDocument =>
        ActiveDocumentIndex >= 0 && ActiveDocumentIndex < Documents.Count
            ? Documents[ActiveDocumentIndex]
            : null;

    // Annotation and search subsystems
    public AnnotationInteractionHandler Annotations { get; }
    public SearchService Search { get; }

    // Auto-scroll state (delegated to AutoScrollController)
    public bool AutoScrollActive => _autoScroll.AutoScrollActive;
    public bool JumpMode { get => _autoScroll.JumpMode; set => _autoScroll.JumpMode = value; }

    // Rail pause (Ctrl+drag free pan) state
    public bool RailPaused { get; private set; }
    private int _pausedBlock;
    private int _pausedLine;
    private double _pausedVerticalBias;
    private double _pausedZoom;

    // Non-rail edge-hold page advance
    private Stopwatch? _nonRailEdgeHoldTimer;
    private bool _nonRailEdgeForward;
    private const double NonRailEdgeHoldMs = 400.0;
    private Stopwatch? _nonRailEdgeCooldown;
    private const double NonRailEdgeCooldownMs = 300.0;

    /// <summary>
    /// Fired when a property changes. UI can subscribe to update bindings.
    /// </summary>
    public Action<string>? StateChanged;

    /// <summary>
    /// Fired when a transient status message should be shown to the user.
    /// </summary>
    public Action<string>? StatusMessage;

    public DocumentController(AppConfig config, IThreadMarshaller marshaller, IPdfServiceFactory pdfFactory,
        ILogger? logger = null)
    {
        _config = config;
        _marshaller = marshaller;
        _pdfFactory = pdfFactory;
        _logger = logger ?? NullLogger.Instance;
        _zoom = new ZoomAnimationController(config);
        _autoScroll = new AutoScrollController(config);
        _autoScroll.StateChanged = name => StateChanged?.Invoke(name);
        _annotationManager = new AnnotationFileManager(marshaller);
        Annotations = new AnnotationInteractionHandler();
        Search = new SearchService(
            () => ActiveDocument,
            () => GetViewportSize(),
            GoToPage);
    }

    /// <summary>
    /// Initialize the ONNX analysis worker. Must be called before opening documents.
    /// </summary>
    public void InitializeWorker()
    {
        var modelPath = FindModelPath()
            ?? throw new FileNotFoundException(
                "ONNX model not found (PP-DocLayoutV3.onnx). Run ./scripts/download-model.sh");

        _logger.Debug($"[ONNX] Starting worker with model: {modelPath}");
        _worker = new AnalysisWorker(modelPath, logger: _logger);
        _logger.Debug("[ONNX] Worker started (ONNX session loading in background)");
    }

    public void SetViewportSize(double w, double h)
    {
        if (w > 0) _vpWidth = w;
        if (h > 0) _vpHeight = h;
    }

    public (double Width, double Height) GetViewportSize() => (_vpWidth, _vpHeight);

    // --- Document management ---

    /// <summary>
    /// Creates a DocumentState for the given path (synchronous). Call LoadDocumentAsync for bitmap loading.
    /// </summary>
    public DocumentState CreateDocument(string path)
        => new(path, _pdfFactory.CreatePdfService(path), _pdfFactory.CreatePdfTextService(),
            _config, _marshaller, _logger);

    /// <summary>
    /// Adds a document to the tab list, restores reading position, and submits analysis.
    /// Call after bitmap is loaded.
    /// </summary>
    public void AddDocument(DocumentState state)
    {
        var (ww, wh) = GetViewportSize();

        var saved = _config.GetReadingPosition(state.FilePath);
        if (saved is not null && saved.Page > 0)
            state.GoToPage(Math.Clamp(saved.Page, 0, state.PageCount - 1), _worker, _config.NavigableClasses, ww, wh);
        if (saved?.ColourEffect is { } savedEffect)
            state.ColourEffect = savedEffect;

        state.CenterPage(ww, wh);
        state.UpdateRailZoom(ww, wh);

        Documents.Add(state);
        _config.AddRecentFile(state.FilePath);
        ActiveDocumentIndex = Documents.Count - 1;

        state.SubmitAnalysis(_worker, _config.NavigableClasses);
        state.QueueLookahead(_config.AnalysisLookaheadPages);
    }

    public void CloseDocument(int index)
    {
        if (index < 0 || index >= Documents.Count) return;
        var doc = Documents[index];
        _config.SaveReadingPosition(doc.FilePath, doc.CurrentPage,
            doc.Camera.Zoom, doc.Camera.OffsetX, doc.Camera.OffsetY, doc.ColourEffect);

        // Unlink before removing so the group cleanup can find remaining members
        if (doc.LinkGroupId.HasValue)
            UnlinkDocument(doc);

        Documents.RemoveAt(index);
        doc.Dispose();
        ActiveDocumentIndex = Math.Clamp(ActiveDocumentIndex, 0, Math.Max(Documents.Count - 1, 0));
        RailPaused = false;
        Search.CloseSearch();
    }

    public void SelectDocument(int index)
    {
        if (index >= 0 && index < Documents.Count)
        {
            RailPaused = false;
            ActiveDocumentIndex = index;
        }
    }

    public void MoveDocument(int fromIndex, int toIndex)
    {
        if (fromIndex == toIndex
            || fromIndex < 0 || fromIndex >= Documents.Count
            || toIndex < 0 || toIndex >= Documents.Count)
            return;

        var selected = ActiveDocument;
        var doc = Documents[fromIndex];
        Documents.RemoveAt(fromIndex);
        Documents.Insert(toIndex, doc);

        if (selected is not null)
            ActiveDocumentIndex = Documents.IndexOf(selected);
    }

    public void SaveAllReadingPositions()
    {
        foreach (var doc in Documents)
            _config.SaveReadingPosition(doc.FilePath, doc.CurrentPage,
                doc.Camera.Zoom, doc.Camera.OffsetX, doc.Camera.OffsetY, doc.ColourEffect);
        _annotationManager.FlushAll();
    }

    // --- Tab linking ---

    /// <summary>Link two documents so they stay on the same page.</summary>
    public void LinkDocuments(DocumentState a, DocumentState b)
    {
        var groupId = a.LinkGroupId ?? b.LinkGroupId ?? Guid.NewGuid();
        a.LinkGroupId = groupId;
        b.LinkGroupId = groupId;
        // Sync b to a's page
        if (b.CurrentPage != a.CurrentPage)
        {
            var (ww, wh) = GetViewportSize();
            b.GoToPage(a.CurrentPage, _worker, _config.NavigableClasses, ww, wh);
        }
    }

    /// <summary>Remove a document from its link group.</summary>
    public void UnlinkDocument(DocumentState doc)
    {
        if (doc.LinkGroupId is not { } groupId) return;
        doc.LinkGroupId = null;

        // If only one document remains in the group, unlink it too
        DocumentState? lastInGroup = null;
        int count = 0;
        foreach (var d in Documents)
        {
            if (d.LinkGroupId == groupId) { lastInGroup = d; count++; }
            if (count > 1) break;
        }
        if (count == 1 && lastInGroup is not null)
            lastInGroup.LinkGroupId = null;
    }

    /// <summary>
    /// Returns a list of documents that could be linked with the given document
    /// (same file, different document, not already in the same group).
    /// </summary>
    public List<DocumentState> GetLinkCandidates(DocumentState doc)
    {
        var result = new List<DocumentState>();
        foreach (var d in Documents)
        {
            if (d == doc) continue;
            if (d.FilePath != doc.FilePath) continue;
            if (doc.LinkGroupId.HasValue && d.LinkGroupId == doc.LinkGroupId) continue;
            result.Add(d);
        }
        return result;
    }

    // --- Bookmarks ---

    /// <summary>
    /// Adds a bookmark for the current page. If a bookmark already exists
    /// for this page, its name is updated instead of creating a duplicate.
    /// Returns true if a new bookmark was added, false if an existing one was renamed.
    /// </summary>
    public bool AddBookmark(string name)
    {
        if (ActiveDocument is not { Annotations: { } annotations } doc) return false;

        var existing = annotations.Bookmarks.FindIndex(b => b.Page == doc.CurrentPage);
        if (existing >= 0)
        {
            doc.RenameBookmark(existing, name);
            return false;
        }

        doc.AddBookmark(name, doc.CurrentPage);
        return true;
    }

    public void RemoveBookmark(int index)
    {
        ActiveDocument?.RemoveBookmark(index);
    }

    public void RenameBookmark(int index, string newName)
    {
        ActiveDocument?.RenameBookmark(index, newName);
    }

    // --- Navigation history (back/forward) ---
    // Stacks live on DocumentState so each tab has independent history.

    public bool CanGoBack => ActiveDocument is { } d && d.BackStack.Count > 0;
    public bool CanGoForward => ActiveDocument is { } d && d.ForwardStack.Count > 0;

    /// <summary>Pushes the current page onto the back stack and clears forward history.</summary>
    private void PushHistory()
    {
        if (ActiveDocument is not { } doc) return;
        doc.BackStack.Push(doc.CurrentPage);
        doc.ForwardStack.Clear();
    }

    public void NavigateToBookmark(int index)
    {
        if (ActiveDocument is not { Annotations: { } annotations } doc) return;
        if ((uint)index >= (uint)annotations.Bookmarks.Count) return;
        PushHistory();
        GoToPage(annotations.Bookmarks[index].Page);
        FitPage();
    }

    public void NavigateBack()
    {
        if (ActiveDocument is not { } doc) return;
        if (doc.BackStack.Count == 0) return;
        doc.ForwardStack.Push(doc.CurrentPage);
        GoToPage(doc.BackStack.Pop());
    }

    public void NavigateForward()
    {
        if (ActiveDocument is not { } doc) return;
        if (doc.ForwardStack.Count == 0) return;
        doc.BackStack.Push(doc.CurrentPage);
        GoToPage(doc.ForwardStack.Pop());
    }

    // --- Navigation ---

    private bool _syncingLinks;

    public void GoToPage(int page)
    {
        _zoom.Cancel();
        if (ActiveDocument is not { } doc) return;
        var (ww, wh) = GetViewportSize();
        doc.GoToPage(page, _worker, _config.NavigableClasses, ww, wh);
        doc.QueueLookahead(_config.AnalysisLookaheadPages);
        Search.UpdateCurrentPageMatches();
        SyncLinkedTabs(doc, page);
    }

    /// <summary>
    /// Sync all documents in the same link group to the given page.
    /// </summary>
    private void SyncLinkedTabs(DocumentState source, int page)
    {
        if (_syncingLinks || source.LinkGroupId is not { } groupId) return;
        _syncingLinks = true;
        try
        {
            var (ww, wh) = GetViewportSize();
            foreach (var doc in Documents)
            {
                if (doc == source || doc.LinkGroupId != groupId) continue;
                if (doc.CurrentPage == page) continue;
                doc.GoToPage(page, _worker, _config.NavigableClasses, ww, wh);
                doc.QueueLookahead(_config.AnalysisLookaheadPages);
            }
        }
        finally
        {
            _syncingLinks = false;
        }
    }

    public void FitPage()
    {
        _zoom.Cancel();
        if (ActiveDocument is not { } doc) return;
        var (ww, wh) = GetViewportSize();
        doc.CenterPage(ww, wh);
        doc.UpdateRailZoom(ww, wh);
    }

    public void FitWidth()
    {
        _zoom.Cancel();
        if (ActiveDocument is not { } doc) return;
        var (ww, wh) = GetViewportSize();
        doc.FitWidth(ww, wh);
        doc.UpdateRailZoom(ww, wh);
    }

    // --- Camera ---

    public void HandleZoom(double scrollDelta, double cursorX, double cursorY, bool ctrlHeld)
    {
        if (AutoScrollActive) StopAutoScroll();
        if (ActiveDocument is not { } doc) return;
        var (ww, wh) = GetViewportSize();

        if (ctrlHeld && doc.Rail.Active && !RailPaused)
        {
            double step = scrollDelta * 2.0 * doc.Camera.Zoom;
            doc.Camera.OffsetX += step;
            doc.ClampCamera(ww, wh);
        }
        else
        {
            double factor = 1.0 + scrollDelta * ZoomAnimationController.ZoomScrollSensitivity;
            double baseZoom = _zoom.PendingTargetZoom ?? doc.Camera.Zoom;
            double newZoom = Math.Clamp(baseZoom * factor, Camera.ZoomMin, Camera.ZoomMax);
            _zoom.Start(doc, newZoom, cursorX, cursorY, _vpWidth);
        }
    }

    public void HandlePan(double dx, double dy, bool ctrlHeld = false)
    {
        _zoom.Cancel();
        if (ActiveDocument is not { } doc) return;
        if (AutoScrollActive) StopAutoScroll();
        var (ww, wh) = GetViewportSize();

        if (ctrlHeld && doc.Rail.Active && !RailPaused)
        {
            RailPaused = true;
            _pausedBlock = doc.Rail.CurrentBlock;
            _pausedLine = doc.Rail.CurrentLine;
            _pausedVerticalBias = doc.Rail.VerticalBias;
            _pausedZoom = doc.Camera.Zoom;
            StatusMessage?.Invoke("Free pan — release Ctrl to return");
        }

        doc.Camera.OffsetX += dx;
        doc.Camera.OffsetY += dy;
        doc.ClampCamera(ww, wh);
        if (doc.Rail.Active && !RailPaused)
            doc.Rail.CaptureVerticalBias(doc.Camera.OffsetY, doc.Camera.Zoom, wh);
    }

    /// <summary>
    /// End rail pause: restore block/line/bias/zoom from before the free pan and snap back.
    /// </summary>
    public void ResumeRailFromPause()
    {
        if (!RailPaused) return;
        RailPaused = false;

        if (ActiveDocument is not { } doc) return;
        var (ww, wh) = GetViewportSize();

        // Restore zoom if it changed during free pan (may re-enter rail mode)
        if (Math.Abs(doc.Camera.Zoom - _pausedZoom) > 0.001)
        {
            doc.Camera.Zoom = _pausedZoom;
            doc.Camera.NotifyZoomChange();
            doc.UpdateRailZoom(ww, wh);
            doc.UpdateRenderDpiIfNeeded();
        }

        if (!doc.Rail.Active) return;

        // Clamp indices in case analysis changed while paused
        doc.Rail.CurrentBlock = Math.Clamp(_pausedBlock, 0, Math.Max(doc.Rail.NavigableCount - 1, 0));
        doc.Rail.CurrentLine = Math.Clamp(_pausedLine, 0, Math.Max(doc.Rail.CurrentLineCount - 1, 0));
        doc.Rail.VerticalBias = _pausedVerticalBias;

        doc.StartSnap(ww, wh);
        StatusMessage?.Invoke("");
    }

    public void HandleZoomKey(bool zoomIn)
    {
        if (ActiveDocument is not { } doc) return;
        var (ww, wh) = GetViewportSize();

        double baseZoom = _zoom.PendingTargetZoom ?? doc.Camera.Zoom;
        double newZoom = Math.Clamp(
            zoomIn ? baseZoom * ZoomAnimationController.ZoomStep : baseZoom / ZoomAnimationController.ZoomStep,
            Camera.ZoomMin, Camera.ZoomMax);

        _zoom.Start(doc, newZoom, ww / 2.0, wh / 2.0, _vpWidth);
        if (!doc.Rail.Active && AutoScrollActive) StopAutoScroll();
    }

    // --- Rail navigation ---

    private enum LineAdvanceResult { NoChange, LineAdvanced, PageChanged, PageChangedRailLost }

    private LineAdvanceResult AdvanceLine(DocumentState doc, bool forward, double ww, double wh)
    {
        int currentPage = doc.CurrentPage;
        var result = forward ? doc.Rail.NextLine() : doc.Rail.PrevLine();
        var boundary = forward ? NavResult.PageBoundaryNext : NavResult.PageBoundaryPrev;
        if (result == boundary)
        {
            return SkipToNavigablePage(doc, forward, 0, ww, wh) switch
            {
                SkipResult.FoundNavigable => LineAdvanceResult.PageChanged,
                _ => LineAdvanceResult.PageChangedRailLost,
            };
        }
        return result == NavResult.Ok ? LineAdvanceResult.LineAdvanced : LineAdvanceResult.NoChange;
    }

    private enum SkipResult { FoundNavigable, Deferred, Exhausted }

    /// <summary>
    /// Advance through pages in the given direction, skipping pages with no
    /// navigable blocks. Cached analysis is checked without rasterizing.
    /// If analysis is pending (async), stores skip state on the document for
    /// deferred continuation via <see cref="TryResumeSkip"/>.
    /// </summary>
    private SkipResult SkipToNavigablePage(DocumentState doc, bool forward, int skipped, double ww, double wh)
    {
        int step = forward ? 1 : -1;
        int targetPage = doc.CurrentPage + step;

        while (targetPage >= 0 && targetPage < doc.PageCount)
        {
            // Fast path: skip cached pages with no navigable blocks without rasterizing
            if (doc.AnalysisCache.TryGetValue(targetPage, out var cached)
                && !HasNavigableBlocks(cached))
            {
                skipped++;
                targetPage += step;
                continue;
            }

            // Either has navigable blocks (land on it) or needs async analysis
            doc.GoToPage(targetPage, _worker, _config.NavigableClasses, ww, wh);
            SyncLinkedTabs(doc, targetPage);
            doc.UpdateRailZoom(ww, wh);

            if (doc.Rail.Active)
            {
                doc.PendingSkipDirection = 0;
                doc.PendingSkipCount = 0;
                doc.QueueLookahead(_config.AnalysisLookaheadPages);
                if (!forward) doc.Rail.JumpToEnd();
                doc.StartSnap(ww, wh);
                if (skipped > 0) NotifyPagesSkipped(skipped);
                return SkipResult.FoundNavigable;
            }

            if (doc.PendingRailSetup)
            {
                doc.PendingSkipDirection = step;
                doc.PendingSkipCount = skipped;
                doc.QueueLookahead(_config.AnalysisLookaheadPages);
                return SkipResult.Deferred;
            }

            skipped++;
            targetPage += step;
        }

        doc.PendingSkipDirection = 0;
        doc.PendingSkipCount = 0;
        return SkipResult.Exhausted;
    }

    private bool HasNavigableBlocks(PageAnalysis analysis)
    {
        foreach (var block in analysis.Blocks)
            if (_config.NavigableClasses.Contains(block.ClassId))
                return true;
        return false;
    }

    private void NotifyPagesSkipped(int count)
    {
        StatusMessage?.Invoke(count == 1
            ? "Skipped 1 page (no text blocks)"
            : $"Skipped {count} pages (no text blocks)");
    }

    /// <summary>
    /// Resume a deferred skip after analysis arrived with no navigable blocks.
    /// Called from <see cref="PollAnalysisResults"/>.
    /// </summary>
    private bool TryResumeSkip(DocumentState doc, double ww, double wh)
    {
        // The current page (whose analysis just arrived with no blocks) counts as skipped
        int skipped = doc.PendingSkipCount + 1;
        bool forward = doc.PendingSkipDirection > 0;
        return SkipToNavigablePage(doc, forward, skipped, ww, wh) == SkipResult.FoundNavigable;
    }

    public void HandleArrowDown() => HandleVerticalNav(forward: true);
    public void HandleArrowUp() => HandleVerticalNav(forward: false);

    private void HandleVerticalNav(bool forward)
    {
        if (!forward && AutoScrollActive) StopAutoScroll();
        if (ActiveDocument is not { } doc) return;
        var (ww, wh) = GetViewportSize();

        if (doc.Rail.Active)
        {
            var adv = AdvanceLine(doc, forward, ww, wh);
            if (adv == LineAdvanceResult.LineAdvanced)
                doc.StartSnap(ww, wh);
        }
        else
        {
            // After an edge-hold page jump, ignore input briefly so key-repeat
            // doesn't immediately pan away from the landing position.
            if (_nonRailEdgeCooldown is not null)
            {
                if (_nonRailEdgeCooldown.Elapsed.TotalMilliseconds < NonRailEdgeCooldownMs)
                    return;
                _nonRailEdgeCooldown = null;
            }

            double prevY = doc.Camera.OffsetY;
            doc.Camera.OffsetY += forward ? -PanStep : PanStep;
            doc.ClampCamera(ww, wh);

            // Check if we're at the page edge (camera didn't move after clamping)
            bool atEdge = Math.Abs(doc.Camera.OffsetY - prevY) < 1.0;
            if (atEdge)
            {
                if (_nonRailEdgeHoldTimer is null || _nonRailEdgeForward != forward)
                {
                    _nonRailEdgeHoldTimer = Stopwatch.StartNew();
                    _nonRailEdgeForward = forward;
                }
                else if (_nonRailEdgeHoldTimer.Elapsed.TotalMilliseconds >= NonRailEdgeHoldMs)
                {
                    int targetPage = doc.CurrentPage + (forward ? 1 : -1);
                    if (targetPage >= 0 && targetPage < doc.PageCount)
                    {
                        GoToPage(targetPage);
                        double scaledH = doc.PageHeight * doc.Camera.Zoom;
                        doc.Camera.OffsetY = forward ? 0 : Math.Min(wh - scaledH, 0);
                        doc.ClampCamera(ww, wh);
                        _nonRailEdgeHoldTimer = null;
                        _nonRailEdgeCooldown = Stopwatch.StartNew();
                    }
                }
            }
            else
            {
                _nonRailEdgeHoldTimer = null;
            }
        }
    }

    /// <summary>Clear non-rail edge-hold state (call on key release).</summary>
    public void ClearNonRailEdgeHold()
    {
        _nonRailEdgeHoldTimer = null;
        _nonRailEdgeCooldown = null;
    }

    public void HandleArrowRight(bool shortJump = false)
    {
        if (AutoScrollActive && ActiveDocument is { } d && d.Rail.Active)
        {
            d.Rail.SetAutoScrollBoost(true);
            return;
        }
        if (TryJump(forward: true, half: shortJump)) return;
        HandleHorizontalArrow(ScrollDirection.Forward, -PanStep);
    }

    public void HandleArrowLeft(bool shortJump = false)
    {
        if (AutoScrollActive) StopAutoScroll();
        if (TryJump(forward: false, half: shortJump)) return;
        HandleHorizontalArrow(ScrollDirection.Backward, PanStep);
    }

    private bool TryJump(bool forward, bool half = false)
    {
        if (!JumpMode || ActiveDocument is not { } doc || !doc.Rail.Active) return false;
        var (ww, wh) = GetViewportSize();
        doc.Rail.Jump(forward, doc.Camera.Zoom, ww, wh, doc.Camera.OffsetX, doc.Camera.OffsetY, half);
        return true;
    }

    private void HandleHorizontalArrow(ScrollDirection direction, double panDelta)
    {
        if (ActiveDocument is not { } doc) return;
        if (doc.Rail.Active)
            doc.Rail.StartScroll(direction, doc.Camera.OffsetX);
        else
        {
            var (ww, wh) = GetViewportSize();
            doc.Camera.OffsetX += panDelta;
            doc.ClampCamera(ww, wh);
        }
    }

    public void HandleLineHome() => SnapToLineEdge(start: true);
    public void HandleLineEnd() => SnapToLineEdge(start: false);

    private void SnapToLineEdge(bool start)
    {
        if (ActiveDocument is not { } doc || !doc.Rail.Active) return;
        var (ww, _) = GetViewportSize();
        var x = start
            ? doc.Rail.ComputeLineStartX(doc.Camera.Zoom, ww)
            : doc.Rail.ComputeLineEndX(doc.Camera.Zoom, ww);
        if (x is { } val)
            doc.Camera.OffsetX = val;
    }

    public void HandleArrowRelease(bool isHorizontal)
    {
        if (isHorizontal)
        {
            ActiveDocument?.Rail.StopScrollAndEdgeHold();
            if (AutoScrollActive)
                ActiveDocument?.Rail.SetAutoScrollBoost(false);
        }
    }

    /// <summary>
    /// Handles a click on the viewport. Returns link destination if a link was clicked,
    /// otherwise falls through to rail-mode block snapping.
    /// </summary>
    public (bool Handled, PdfLinkDestination? Link) HandleClick(double canvasX, double canvasY)
    {
        if (ActiveDocument is not { } doc) return (false, null);

        double pageX = (canvasX - doc.Camera.OffsetX) / doc.Camera.Zoom;
        double pageY = (canvasY - doc.Camera.OffsetY) / doc.Camera.Zoom;

        // Check for PDF links first (takes priority over rail-mode snap)
        var link = doc.HitTestLink(pageX, pageY);
        if (link is not null)
        {
            if (link.Destination is PageDestination pageDest)
            {
                PushHistory();
                GoToPage(pageDest.PageIndex);
                return (true, link.Destination);
            }
            return (true, link.Destination);
        }

        // Fall through to rail-mode block snapping
        if (!doc.Rail.Active || !doc.Rail.HasAnalysis) return (false, null);

        doc.Rail.FindBlockNearPoint(pageX, pageY);
        var (ww, wh) = GetViewportSize();
        doc.StartSnap(ww, wh);
        return (true, null);
    }

    /// <summary>
    /// Hit-tests a point (in page-point space) against PDF links on the active document.
    /// </summary>
    public PdfLink? HitTestLink(double pageX, double pageY)
        => ActiveDocument?.HitTestLink(pageX, pageY);

    // --- Auto-scroll (delegated to AutoScrollController) ---

    public void ToggleAutoScroll() => _autoScroll.ToggleAutoScroll(ActiveDocument);

    public void StopAutoScroll() => _autoScroll.StopAutoScroll(ActiveDocument);

    public void ToggleAutoScrollExclusive() => _autoScroll.ToggleAutoScrollExclusive(ActiveDocument);

    public void ToggleJumpModeExclusive() => _autoScroll.ToggleJumpModeExclusive(ActiveDocument);

    // --- Colour effects ---

    public void SetColourEffect(ColourEffect effect)
    {
        if (ActiveDocument is { } doc)
            doc.ColourEffect = effect;
    }

    public void SetGlobalColourEffect(ColourEffect effect)
    {
        _config.ColourEffect = effect;
        _config.Save();
        SetColourEffect(effect);
    }

    public ColourEffect CycleColourEffect()
    {
        var values = Enum.GetValues<ColourEffect>();
        var current = ActiveDocument?.ColourEffect ?? ActiveColourEffect;
        int idx = (Array.IndexOf(values, current) + 1) % values.Length;
        var next = values[idx];
        SetColourEffect(next);
        return next;
    }

    // --- Config ---

    public void OnConfigChanged()
    {
        foreach (var doc in Documents)
        {
            doc.Rail.UpdateConfig(_config);
            doc.ReapplyNavigableClasses(_config.NavigableClasses);
        }
        _config.Save();
    }

    public void OnSliderChanged()
    {
        foreach (var doc in Documents)
            doc.Rail.UpdateConfig(_config);
    }

    // --- Tick (animation frame logic) ---

    /// <summary>
    /// Advance one animation frame. Returns what needs repainting.
    /// </summary>
    public TickResult Tick(double dt)
    {
        dt = Math.Min(dt, 0.05);

        var doc = ActiveDocument;
        if (doc is null) return default;

        var (ww, wh) = GetViewportSize();
        bool cameraChanged = false;
        bool pageChanged = false;
        bool overlayChanged = false;
        bool animating = false;

        TickZoomAnimation(doc, ww, wh, ref cameraChanged, ref animating);

        if (!RailPaused)
            TickRailSnap(doc, dt, ww, wh, ref cameraChanged, ref pageChanged, ref overlayChanged, ref animating);

        // Snap Y to integer pixel when rail mode is stable
        if (_config.PixelSnapping && doc.Rail.Active && !animating)
        {
            double snapped = Math.Round(doc.Camera.OffsetY);
            if (snapped != doc.Camera.OffsetY)
            {
                doc.Camera.OffsetY = snapped;
                cameraChanged = true;
            }
        }

        if (!RailPaused)
            TickAutoScroll(doc, dt, ww, wh, ref cameraChanged, ref pageChanged, ref overlayChanged, ref animating);

        // Decay zoom blur speed
        if (doc.Camera.ZoomSpeed > 0)
        {
            doc.Camera.DecayZoomSpeed(dt);
            animating = true;
            // Only mark camera changed if zoom speed is still perceptible
            // (drives motion blur updates without forcing full compositor work)
            if (doc.Camera.ZoomSpeed > 0)
                cameraChanged = true;
        }

        // Poll analysis results
        var (gotResults, needsAnim) = PollAnalysisResults();
        animating |= needsAnim;
        overlayChanged |= gotResults;

        if (!animating)
            doc.SubmitPendingLookahead(_worker);

        // DPI bitmap swap
        if (doc.DpiRenderReady)
        {
            doc.DpiRenderReady = false;
            pageChanged = true;
        }

        // Only force camera invalidation if no tick method already set it.
        // During rail scroll, TickRailSnap/TickAutoScroll set cameraChanged
        // when camera actually moves — no need to redundantly force it here.
        // This avoids unnecessary compositor MatrixTransform updates on frames
        // where the camera position hasn't changed (e.g. auto-scroll pause).

        return new TickResult(cameraChanged, pageChanged, overlayChanged, false, false, animating);
    }

    /// <summary>Smooth zoom animation step (delegated to ZoomAnimationController).</summary>
    private void TickZoomAnimation(DocumentState doc, double ww, double wh,
        ref bool cameraChanged, ref bool animating)
    {
        _zoom.Tick(doc, ww, wh, ref cameraChanged, ref animating);
    }

    /// <summary>Rail snap animation and edge-hold line advance (skipped while zoom is animating).</summary>
    private void TickRailSnap(DocumentState doc, double dt, double ww, double wh,
        ref bool cameraChanged, ref bool pageChanged, ref bool overlayChanged, ref bool animating)
    {
        if (!_zoom.IsAnimating)
        {
            double cx = doc.Camera.OffsetX, cy = doc.Camera.OffsetY;
            bool railAnimating = doc.Rail.Tick(ref cx, ref cy, dt, doc.Camera.Zoom, ww);
            if (cx != doc.Camera.OffsetX || cy != doc.Camera.OffsetY)
            {
                doc.Camera.OffsetX = cx;
                doc.Camera.OffsetY = cy;
                cameraChanged = true;
            }
            animating |= railAnimating;

            if (doc.Rail.AutoScrollTriggered)
            {
                doc.Rail.AutoScrollTriggered = false;
                _autoScroll.ActivateAutoScroll();
                StatusMessage?.Invoke("Auto-scroll activated");
            }

            // Edge-hold advance: D/Right held at line end → NextLine; A/Left held at line start → PrevLine
            if (!doc.Rail.AutoScrolling && doc.Rail.ConsumePendingEdgeAdvance() is { } edgeDir)
            {
                bool forward = edgeDir == ScrollDirection.Forward;
                var adv = AdvanceLine(doc, forward, ww, wh);
                if (adv is LineAdvanceResult.PageChanged or LineAdvanceResult.PageChangedRailLost)
                {
                    pageChanged = true;
                    // AdvanceLine already snaps to start; override with snap-to-end for backward
                    if (!forward && adv == LineAdvanceResult.PageChanged)
                        doc.StartSnapToEnd(ww, wh);
                }
                else if (adv == LineAdvanceResult.LineAdvanced)
                {
                    if (forward) doc.StartSnap(ww, wh);
                    else doc.StartSnapToEnd(ww, wh);
                }
                overlayChanged = true;
                cameraChanged = true;
            }
        }
    }

    /// <summary>Auto-scroll tick: advance along the current line, then advance to the next line/page.</summary>
    private void TickAutoScroll(DocumentState doc, double dt, double ww, double wh,
        ref bool cameraChanged, ref bool pageChanged, ref bool overlayChanged, ref bool animating)
    {
        if (doc.Rail.AutoScrolling)
        {
            double cx = doc.Camera.OffsetX;
            bool reachedEnd = doc.Rail.TickAutoScroll(ref cx, dt, doc.Camera.Zoom, ww);
            if (cx != doc.Camera.OffsetX)
            {
                doc.Camera.OffsetX = cx;
                cameraChanged = true;
            }
            animating = true;

            if (reachedEnd)
            {
                int prevBlock = doc.Rail.CurrentBlock;
                var adv = AdvanceLine(doc, forward: true, ww, wh);
                switch (adv)
                {
                    case LineAdvanceResult.PageChanged:
                        pageChanged = true;
                        doc.Rail.StartAutoScroll(_autoScroll.AutoScrollSpeed);
                        doc.Rail.PauseAutoScroll(GetBlockEntryPause(doc));
                        break;
                    case LineAdvanceResult.PageChangedRailLost:
                        pageChanged = true;
                        StopAutoScroll();
                        break;
                    case LineAdvanceResult.LineAdvanced:
                        doc.StartSnap(ww, wh);
                        bool enteredNewBlock = doc.Rail.CurrentBlock != prevBlock;
                        doc.Rail.PauseAutoScroll(enteredNewBlock
                            ? GetBlockEntryPause(doc)
                            : _config.AutoScrollLinePauseMs);
                        break;
                }
                overlayChanged = true;
            }
        }
    }

    /// <summary>
    /// Returns the auto-scroll pause duration for entering the current block,
    /// based on its class (equation, header, or default).
    /// </summary>
    private double GetBlockEntryPause(DocumentState doc) =>
        doc.Rail.CurrentNavigableBlock.ClassId switch
        {
            LayoutConstants.ClassDisplayFormula => _config.AutoScrollEquationPauseMs,
            LayoutConstants.ClassDocTitle or LayoutConstants.ClassParagraphTitle => _config.AutoScrollHeaderPauseMs,
            _ => _config.AutoScrollLinePauseMs,
        };

    /// <summary>
    /// Poll the analysis worker for completed results. Can also be called
    /// from a low-frequency timer when not animating.
    /// </summary>
    public (bool GotResults, bool NeedsAnimation) PollAnalysisResults()
    {
        bool got = false;
        bool needsAnim = false;
        if (_worker is null) return (false, false);
        var (ww, wh) = GetViewportSize();
        while (_worker.Poll() is { } result)
        {
            got = true;
            _logger.Debug($"[Analysis] Got result for {Path.GetFileName(result.FilePath)} page {result.Page}: {result.Analysis.Blocks.Count} blocks");
            foreach (var doc in Documents)
            {
                if (doc.FilePath != result.FilePath) continue;

                doc.AnalysisCache[result.Page] = result.Analysis;
                if (doc.CurrentPage == result.Page && doc.PendingRailSetup)
                {
                    doc.Rail.SetAnalysis(result.Analysis, _config.NavigableClasses);
                    doc.PendingRailSetup = false;
                    doc.UpdateRailZoom(ww, wh);
                    _logger.Debug($"[Analysis] Rail has {doc.Rail.NavigableCount} navigable blocks, Active={doc.Rail.Active}");
                    if (doc.Rail.Active)
                    {
                        doc.PendingSkipDirection = 0;
                        doc.StartSnap(ww, wh);
                        needsAnim = true;
                    }
                    else if (doc.PendingSkipDirection != 0)
                    {
                        // Analysis arrived but no navigable blocks — skip to next page.
                        // Only resume if this is the active document; clear stale state otherwise.
                        if (doc == ActiveDocument)
                            needsAnim |= TryResumeSkip(doc, ww, wh);
                        else
                        {
                            doc.PendingSkipDirection = 0;
                            doc.PendingSkipCount = 0;
                        }
                    }
                }
            }
        }
        return (got, needsAnim);
    }

    // --- Query methods (for agent / headless use) ---

    public DocumentList ListDocuments()
    {
        var summaries = Documents.Select((d, i) => new DocumentSummary(
            i, d.FilePath, d.Title, d.PageCount, d.CurrentPage)).ToList();
        return new DocumentList(ActiveDocumentIndex, summaries);
    }

    public DocumentInfo? GetDocumentInfo(int? index = null)
    {
        var doc = index.HasValue && index.Value >= 0 && index.Value < Documents.Count
            ? Documents[index.Value]
            : ActiveDocument;
        if (doc is null) return null;

        return new DocumentInfo(
            doc.FilePath, doc.Title, doc.PageCount, doc.CurrentPage,
            doc.Camera.Zoom, doc.Camera.OffsetX, doc.Camera.OffsetY,
            doc.Rail.Active, doc.Rail.HasAnalysis, doc.Rail.NavigableCount,
            AutoScrollActive, JumpMode);
    }

    public TextContent? GetPageText(int? page = null)
    {
        var doc = ActiveDocument;
        if (doc is null) return null;
        int p = page ?? doc.CurrentPage;
        if (p < 0 || p >= doc.PageCount) return null;
        var text = doc.GetOrExtractText(p);
        return new TextContent(p, text.Text);
    }

    public LayoutInfo? GetLayoutInfo(int? page = null)
    {
        var doc = ActiveDocument;
        if (doc is null) return null;
        int p = page ?? doc.CurrentPage;
        if (!doc.AnalysisCache.TryGetValue(p, out var analysis)) return null;

        var navigableSet = Config.NavigableClasses;
        var blocks = analysis.Blocks.Select(b =>
        {
            var className = b.ClassId >= 0 && b.ClassId < LayoutConstants.LayoutClasses.Length
                ? LayoutConstants.LayoutClasses[b.ClassId]
                : $"class_{b.ClassId}";
            return new BlockInfo(
                className, b.BBox.X, b.BBox.Y, b.BBox.W, b.BBox.H,
                b.Confidence, b.Order, b.Lines.Count,
                navigableSet.Contains(b.ClassId));
        }).ToList();

        return new LayoutInfo(p, blocks);
    }

    public SearchResult GetSearchState() => Search.GetSearchState();

    // --- Static helpers ---

    public static string? FindModelPath()
    {
        const string filename = "PP-DocLayoutV3.onnx";
        var candidates = new List<string?>
        {
            Path.Combine(AppContext.BaseDirectory, "models", filename),
            Environment.GetEnvironmentVariable("APPDIR") is { } appDir
                ? Path.Combine(appDir, "models", filename) : null,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "railreader2", "models", filename),
            Path.Combine("models", filename),
        };

        // Walk up from CWD
        for (int i = 1; i <= 3; i++)
        {
            var walkUp = string.Concat(Enumerable.Repeat("../", i));
            candidates.Add(Path.Combine(walkUp, "models", filename));
        }

        foreach (var path in candidates)
        {
            if (path is not null && File.Exists(path))
                return Path.GetFullPath(path);
        }
        return null;
    }

}
