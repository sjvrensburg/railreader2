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
public sealed partial class DocumentController : IDisposable
{
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
    private RailPauseState? _railPause;
    public bool RailPaused => _railPause is not null;

    // Edge-hold page advance (non-rail vertical scrolling)
    private readonly EdgeHoldStateMachine _pageEdgeHold = new();

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
        _annotationManager.OnSaveFailure = msg => StatusMessage?.Invoke(msg);
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
        _worker = new AnalysisWorker(modelPath, _marshaller, _logger);
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
        bool restoredPage = saved is not null && saved.Page > 0;
        if (restoredPage)
            state.GoToPage(Math.Clamp(saved!.Page, 0, state.PageCount - 1), _worker, _config.NavigableClasses, ww, wh);
        if (saved?.ColourEffect is { } savedEffect)
            state.ColourEffect = savedEffect;

        state.CenterPage(ww, wh);
        state.UpdateRailZoom(ww, wh);

        Documents.Add(state);
        _config.AddRecentFile(state.FilePath);
        ActiveDocumentIndex = Documents.Count - 1;

        // GoToPage already submitted analysis for the restored page;
        // only submit here for new documents starting at page 0.
        if (!restoredPage)
            state.SubmitAnalysis(_worker, _config.NavigableClasses);
        state.QueueLookahead(_config.AnalysisLookaheadPages);
    }

    public void CloseDocument(int index)
    {
        if (index < 0 || index >= Documents.Count) return;
        var doc = Documents[index];
        _config.SaveReadingPosition(doc.FilePath, doc.CurrentPage,
            doc.Camera.Zoom, doc.Camera.OffsetX, doc.Camera.OffsetY, doc.ColourEffect);

        Documents.RemoveAt(index);
        doc.Dispose();
        ActiveDocumentIndex = Math.Clamp(ActiveDocumentIndex, 0, Math.Max(Documents.Count - 1, 0));
        _railPause = null;
        Search.CloseSearch();
    }

    public void SelectDocument(int index)
    {
        if (index >= 0 && index < Documents.Count)
        {
            _railPause = null;
            ActiveDocumentIndex = index;

            // Sync the global auto-scroll flag with the newly active document.
            // Without this, switching away from a tab with active auto-scroll leaves
            // the flag stale, which prevents hold-scroll (and its trigger) on other tabs.
            if (AutoScrollActive && !(ActiveDocument?.Rail.AutoScrolling ?? false))
                _autoScroll.StopAutoScroll(null);
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

    public void Dispose()
    {
        SaveAllReadingPositions();
        foreach (var doc in Documents)
            doc.Dispose();
        Documents.Clear();
        _worker?.Dispose();
        _worker = null;
        _annotationManager.Dispose();
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

    public bool CanGoBack => ActiveDocument is { } d && d.BackStackCount > 0;
    public bool CanGoForward => ActiveDocument is { } d && d.ForwardStackCount > 0;

    /// <summary>Pushes the current page onto the back stack and clears forward history.</summary>
    private void PushHistory()
    {
        if (ActiveDocument is not { } doc) return;
        doc.PushHistory(doc.CurrentPage);
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
        if (doc.BackStackCount == 0) return;
        GoToPage(doc.PopBack(doc.CurrentPage));
    }

    public void NavigateForward()
    {
        if (ActiveDocument is not { } doc) return;
        if (doc.ForwardStackCount == 0) return;
        GoToPage(doc.PopForward(doc.CurrentPage));
    }

    /// <summary>
    /// Scrolls the camera so the destination position is visible.
    /// Coordinates are in PDF user space; converted using the target page dimensions.
    /// </summary>
    private void ScrollToDestination(PageDestination dest)
    {
        if (ActiveDocument is not { } doc) return;
        if (dest.PdfX is null && dest.PdfY is null) return;

        var (ww, wh) = GetViewportSize();

        if (dest.PdfY is { } pdfY)
        {
            double pageY = doc.PageHeight - pdfY;
            doc.Camera.OffsetY = -pageY * doc.Camera.Zoom + wh * CoreTuning.DestMarginTop;
        }

        if (dest.PdfX is { } pdfX)
        {
            doc.Camera.OffsetX = -pdfX * doc.Camera.Zoom + ww * CoreTuning.DestMarginLeft;
        }

        doc.ClampCamera(ww, wh);
        doc.UpdateRailZoom(ww, wh);
    }

    // --- Navigation ---

    public void GoToPage(int page)
    {
        _zoom.Cancel();
        if (ActiveDocument is not { } doc) return;
        var (ww, wh) = GetViewportSize();
        if (!doc.GoToPage(page, _worker, _config.NavigableClasses, ww, wh))
        {
            NotifyRenderFailed(page);
            return;
        }
        doc.QueueLookahead(_config.AnalysisLookaheadPages);
        Search.UpdateCurrentPageMatches();
    }

    private void NotifyRenderFailed(int page)
        => StatusMessage?.Invoke($"Page {page + 1} could not be rendered (corrupted?)");

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
            StartRailPause(doc);

        doc.Camera.OffsetX += dx;
        doc.Camera.OffsetY += dy;
        doc.ClampCamera(ww, wh);
        if (doc.Rail.Active && !RailPaused)
            doc.Rail.CaptureVerticalBias(doc.Camera.OffsetY, doc.Camera.Zoom, wh);
    }

    private void StartRailPause(DocumentState doc)
    {
        _railPause = new(doc.Rail.CurrentBlock, doc.Rail.CurrentLine, doc.Rail.VerticalBias, doc.Camera.Zoom);
        StatusMessage?.Invoke("Free pan — release Ctrl to return");
    }

    /// <summary>
    /// End rail pause: restore block/line/bias/zoom from before the free pan and snap back.
    /// </summary>
    public void ResumeRailFromPause()
    {
        if (_railPause is not { } pause) return;
        _railPause = null;

        if (ActiveDocument is not { } doc) return;
        var (ww, wh) = GetViewportSize();

        // Restore zoom if it changed during free pan (may re-enter rail mode)
        if (Math.Abs(doc.Camera.Zoom - pause.Zoom) > 0.001)
        {
            doc.Camera.Zoom = pause.Zoom;
            doc.Camera.NotifyZoomChange();
            doc.UpdateRailZoom(ww, wh);
            doc.UpdateRenderDpiIfNeeded();
        }

        if (!doc.Rail.Active) return;

        // Clamp indices in case analysis changed while paused
        doc.Rail.CurrentBlock = Math.Clamp(pause.Block, 0, Math.Max(doc.Rail.NavigableCount - 1, 0));
        doc.Rail.CurrentLine = Math.Clamp(pause.Line, 0, Math.Max(doc.Rail.CurrentLineCount - 1, 0));
        doc.Rail.VerticalBias = pause.VerticalBias;

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
            // Use the same base directory as AppConfig.ConfigDir so the model
            // is found wherever the app stored it (%APPDATA% on Windows, ~/.config on Linux).
            Path.Combine(AppConfig.ConfigDir, "models", filename),
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
