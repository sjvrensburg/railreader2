using System.Diagnostics;
using System.Text.RegularExpressions;
using RailReader.Core.Commands;
using RailReader.Core.Models;
using RailReader.Core.Services;
using SkiaSharp;

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
    private const double ZoomStep = 1.25;
    private const double ZoomScrollSensitivity = 0.003;
    private const double PanStep = 50.0;

    private readonly AppConfig _config;
    private readonly IThreadMarshaller _marshaller;
    private AnalysisWorker? _worker;
    public bool HasWorker => _worker is not null;
    private readonly ColourEffectShaders _colourEffects = new();

    private double _vpWidth = 1200;
    private double _vpHeight = 900;

    // Smooth zoom animation
    private sealed class ZoomAnimation
    {
        public double StartZoom, TargetZoom;
        public double StartOffsetX, StartOffsetY;
        public double TargetOffsetX, TargetOffsetY;
        public double CursorPageX, CursorPageY;
        public Stopwatch Timer = Stopwatch.StartNew();
        public const double DurationMs = 180;
    }
    private ZoomAnimation? _zoomAnim;

    public List<DocumentState> Documents { get; } = [];
    public int ActiveDocumentIndex { get; set; }
    public AppConfig Config => _config;
    public ColourEffectShaders ColourEffects => _colourEffects;
    public ColourEffect ActiveColourEffect { get; private set; }
    public float ActiveColourIntensity { get; private set; } = 1.0f;
    public AnalysisWorker? Worker => _worker;

    public DocumentState? ActiveDocument =>
        ActiveDocumentIndex >= 0 && ActiveDocumentIndex < Documents.Count
            ? Documents[ActiveDocumentIndex]
            : null;

    // Search state
    public List<SearchMatch> SearchMatches { get; private set; } = [];
    private Dictionary<int, List<SearchMatch>> _searchMatchesByPage = [];
    public List<SearchMatch>? CurrentPageSearchMatches { get; private set; }
    public int ActiveMatchIndex { get; set; }

    // Annotation tool state
    public AnnotationTool ActiveTool { get; set; } = AnnotationTool.None;
    public bool IsAnnotating => ActiveTool != AnnotationTool.None;
    public Annotation? SelectedAnnotation { get; set; }
    public Annotation? PreviewAnnotation { get; set; }
    public string ActiveAnnotationColor { get; set; } = "#FFFF00";
    public float ActiveAnnotationOpacity { get; set; } = 0.4f;
    public float ActiveStrokeWidth { get; set; } = 2f;

    // Colour palettes for tools with colour options
    public static readonly (string Color, float Opacity)[] HighlightColors =
    [
        ("#FFFF00", 0.35f),  // Yellow
        ("#90EE90", 0.35f),  // Green
        ("#FFB6C1", 0.35f),  // Pink
    ];

    public static readonly (string Color, float Opacity)[] PenColors =
    [
        ("#FF0000", 0.8f),   // Red
        ("#0000FF", 0.8f),   // Blue
        ("#000000", 0.9f),   // Black
    ];

    private int _highlightColorIndex;
    private int _penColorIndex;

    // In-progress annotation building state
    private List<PointF>? _freehandPoints;
    private float _rectStartX, _rectStartY;
    private int _highlightCharStart = -1;

    // Browse-mode drag state (for moving/resizing annotations)
    private Annotation? _dragAnnotation;
    private float _dragStartPageX, _dragStartPageY;
    private PositionSnapshot? _dragOriginalPosition;
    private ResizeHandle _resizeHandle = ResizeHandle.None;
    private SKRect _resizeStartBounds;
    private List<PointF>? _resizeOriginalPoints;

    // Text selection state
    public string? SelectedText { get; set; }
    public List<HighlightRect>? TextSelectionRects { get; set; }
    private int _textSelectCharStart = -1;

    // Clipboard callback (set by UI)
    public Action<string>? CopyToClipboard { get; set; }

    // Auto-scroll state
    public bool AutoScrollActive { get; private set; }
    public bool JumpMode { get; set; }

    /// <summary>
    /// Fired when a property changes. UI can subscribe to update bindings.
    /// </summary>
    public Action<string>? StateChanged;

    public DocumentController(AppConfig config, IThreadMarshaller marshaller)
    {
        _config = config;
        _marshaller = marshaller;
        ActiveColourEffect = config.ColourEffect;
        ActiveColourIntensity = (float)config.ColourEffectIntensity;
    }

    /// <summary>
    /// Initialize the ONNX analysis worker. Must be called before opening documents.
    /// </summary>
    public void InitializeWorker()
    {
        var modelPath = FindModelPath()
            ?? throw new FileNotFoundException(
                "ONNX model not found (PP-DocLayoutV3.onnx). Run ./scripts/download-model.sh");

        Console.Error.WriteLine($"[ONNX] Starting worker with model: {modelPath}");
        _worker = new AnalysisWorker(modelPath);
        Console.Error.WriteLine("[ONNX] Worker started (ONNX session loading in background)");
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
        => new(path, _config, _marshaller);

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
        ActiveColourEffect = state.ColourEffect;

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
    }

    public void SelectDocument(int index)
    {
        if (index >= 0 && index < Documents.Count)
        {
            ActiveDocumentIndex = index;
            ActiveColourEffect = Documents[index].ColourEffect;
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

    /// <summary>Page the user was on before the last bookmark navigation. -1 = none.</summary>
    public int LastPositionPage { get; private set; } = -1;

    public void NavigateToBookmark(int index)
    {
        if (ActiveDocument is not { Annotations: { } annotations } doc) return;
        if ((uint)index >= (uint)annotations.Bookmarks.Count) return;
        LastPositionPage = doc.CurrentPage;
        GoToPage(annotations.Bookmarks[index].Page);
        FitPage();
    }

    public void NavigateBack()
    {
        if (ActiveDocument is not { } doc) return;
        if (LastPositionPage < 0) return;
        int backPage = LastPositionPage;
        LastPositionPage = doc.CurrentPage;
        GoToPage(backPage);
        FitPage();
    }

    // --- Navigation ---

    public void GoToPage(int page)
    {
        _zoomAnim = null;
        if (ActiveDocument is not { } doc) return;
        var (ww, wh) = GetViewportSize();
        doc.GoToPage(page, _worker, _config.NavigableClasses, ww, wh);
        doc.QueueLookahead(_config.AnalysisLookaheadPages);
        UpdateCurrentPageMatches();
    }

    public void FitPage()
    {
        _zoomAnim = null;
        if (ActiveDocument is not { } doc) return;
        var (ww, wh) = GetViewportSize();
        doc.CenterPage(ww, wh);
        doc.UpdateRailZoom(ww, wh);
    }

    public void FitWidth()
    {
        _zoomAnim = null;
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

        if (ctrlHeld && doc.Rail.Active)
        {
            double step = scrollDelta * 2.0 * doc.Camera.Zoom;
            doc.Camera.OffsetX += step;
            doc.ClampCamera(ww, wh);
        }
        else
        {
            double factor = 1.0 + scrollDelta * ZoomScrollSensitivity;
            double baseZoom = _zoomAnim?.TargetZoom ?? doc.Camera.Zoom;
            double newZoom = Math.Clamp(baseZoom * factor, Camera.ZoomMin, Camera.ZoomMax);
            StartZoomAnimation(doc, newZoom, cursorX, cursorY);
        }
    }

    public void HandlePan(double dx, double dy)
    {
        _zoomAnim = null;
        if (ActiveDocument is not { } doc) return;
        if (AutoScrollActive) StopAutoScroll();
        var (ww, wh) = GetViewportSize();
        doc.Camera.OffsetX += dx;
        doc.Camera.OffsetY += dy;
        doc.ClampCamera(ww, wh);
        if (doc.Rail.Active)
            doc.Rail.CaptureVerticalBias(doc.Camera.OffsetY, doc.Camera.Zoom, wh);
    }

    public void HandleZoomKey(bool zoomIn)
    {
        if (ActiveDocument is not { } doc) return;
        var (ww, wh) = GetViewportSize();

        double baseZoom = _zoomAnim?.TargetZoom ?? doc.Camera.Zoom;
        double newZoom = Math.Clamp(
            zoomIn ? baseZoom * ZoomStep : baseZoom / ZoomStep,
            Camera.ZoomMin, Camera.ZoomMax);

        StartZoomAnimation(doc, newZoom, ww / 2.0, wh / 2.0);
        if (!doc.Rail.Active && AutoScrollActive) StopAutoScroll();
    }

    /// <summary>
    /// Starts a smooth zoom animation toward <paramref name="focusX"/>,<paramref name="focusY"/>
    /// (screen coordinates). Accumulates from any in-progress animation.
    /// </summary>
    private void StartZoomAnimation(DocumentState doc, double newZoom, double focusX, double focusY)
    {
        double baseOx = _zoomAnim?.TargetOffsetX ?? doc.Camera.OffsetX;
        double baseOy = _zoomAnim?.TargetOffsetY ?? doc.Camera.OffsetY;
        double baseZoom = _zoomAnim?.TargetZoom ?? doc.Camera.Zoom;

        double targetOx = focusX - (focusX - baseOx) * (newZoom / baseZoom);
        double targetOy = focusY - (focusY - baseOy) * (newZoom / baseZoom);

        _zoomAnim = new ZoomAnimation
        {
            StartZoom = doc.Camera.Zoom,
            TargetZoom = newZoom,
            StartOffsetX = doc.Camera.OffsetX,
            StartOffsetY = doc.Camera.OffsetY,
            TargetOffsetX = targetOx,
            TargetOffsetY = targetOy,
            CursorPageX = (focusX - targetOx) / newZoom,
            CursorPageY = (focusY - targetOy) / newZoom,
        };
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
            doc.GoToPage(currentPage + (forward ? 1 : -1), _worker, _config.NavigableClasses, ww, wh);
            doc.QueueLookahead(_config.AnalysisLookaheadPages);
            if (doc.Rail.Active)
            {
                if (!forward) doc.Rail.JumpToEnd();
                doc.StartSnap(ww, wh);
                return LineAdvanceResult.PageChanged;
            }
            return LineAdvanceResult.PageChangedRailLost;
        }
        return result == NavResult.Ok ? LineAdvanceResult.LineAdvanced : LineAdvanceResult.NoChange;
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
            doc.Camera.OffsetY += forward ? -PanStep : PanStep;
            doc.ClampCamera(ww, wh);
        }
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

    public void HandleClick(double canvasX, double canvasY)
    {
        if (ActiveDocument is not { } doc || !doc.Rail.Active || !doc.Rail.HasAnalysis) return;

        double pageX = (canvasX - doc.Camera.OffsetX) / doc.Camera.Zoom;
        double pageY = (canvasY - doc.Camera.OffsetY) / doc.Camera.Zoom;

        doc.Rail.FindBlockNearPoint(pageX, pageY);
        var (ww, wh) = GetViewportSize();
        doc.StartSnap(ww, wh);
    }

    // --- Auto-scroll ---

    public void ToggleAutoScroll()
    {
        if (AutoScrollActive)
        {
            StopAutoScroll();
            return;
        }
        if (ActiveDocument is not { } doc || !doc.Rail.Active) return;

        doc.Rail.StartAutoScroll(AutoScrollSpeed);
        AutoScrollActive = true;
        StateChanged?.Invoke(nameof(AutoScrollActive));
    }

    public void StopAutoScroll()
    {
        ActiveDocument?.Rail.StopAutoScroll();
        AutoScrollActive = false;
        StateChanged?.Invoke(nameof(AutoScrollActive));
    }

    public void ToggleAutoScrollExclusive()
    {
        if (JumpMode) JumpMode = false;
        ToggleAutoScroll();
    }

    public void ToggleJumpModeExclusive()
    {
        if (AutoScrollActive) StopAutoScroll();
        JumpMode = !JumpMode;
    }

    private double AutoScrollSpeed =>
        (_config.ScrollSpeedStart + _config.ScrollSpeedMax) / 2.0;

    // --- Colour effects ---

    public void SetColourIntensity(float intensity) => ActiveColourIntensity = intensity;

    public void SetColourEffect(ColourEffect effect)
    {
        if (ActiveDocument is { } doc)
            doc.ColourEffect = effect;
        ActiveColourEffect = effect;
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
        if (ActiveDocument is { } activeDoc)
            ActiveColourEffect = activeDoc.ColourEffect;
        ActiveColourIntensity = (float)_config.ColourEffectIntensity;
        foreach (var doc in Documents)
        {
            doc.Rail.UpdateConfig(_config);
            doc.ReapplyNavigableClasses(_config.NavigableClasses);
            doc.InvalidateBionicCache();
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

        TickAutoScroll(doc, dt, ww, wh, ref cameraChanged, ref pageChanged, ref overlayChanged, ref animating);

        // Decay zoom blur speed
        if (doc.Camera.ZoomSpeed > 0)
        {
            doc.Camera.DecayZoomSpeed(dt);
            animating = true;
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

        if (animating) cameraChanged = true;

        return new TickResult(cameraChanged, pageChanged, overlayChanged, false, false, animating);
    }

    /// <summary>Smooth zoom animation step.</summary>
    private void TickZoomAnimation(DocumentState doc, double ww, double wh,
        ref bool cameraChanged, ref bool animating)
    {
        if (_zoomAnim is { } za)
        {
            double elapsed = za.Timer.Elapsed.TotalMilliseconds;
            double t = Math.Clamp(elapsed / ZoomAnimation.DurationMs, 0, 1);
            // Cubic ease-out: 1 - (1-t)^3
            double ease = 1.0 - (1.0 - t) * (1.0 - t) * (1.0 - t);

            doc.Camera.Zoom = za.StartZoom + (za.TargetZoom - za.StartZoom) * ease;
            doc.Camera.OffsetX = za.StartOffsetX + (za.TargetOffsetX - za.StartOffsetX) * ease;
            doc.Camera.OffsetY = za.StartOffsetY + (za.TargetOffsetY - za.StartOffsetY) * ease;
            doc.Camera.NotifyZoomChange();
            doc.UpdateRailZoom(ww, wh, za.CursorPageX, za.CursorPageY);
            cameraChanged = true;

            if (t >= 1.0)
            {
                _zoomAnim = null;
                doc.ClampCamera(ww, wh);
                if (doc.Rail.Active)
                    doc.StartSnap(ww, wh);
                doc.UpdateRenderDpiIfNeeded();
            }
            else
            {
                animating = true;
            }
        }
    }

    /// <summary>Rail snap animation and edge-hold line advance (skipped while zoom is animating).</summary>
    private void TickRailSnap(DocumentState doc, double dt, double ww, double wh,
        ref bool cameraChanged, ref bool pageChanged, ref bool overlayChanged, ref bool animating)
    {
        if (_zoomAnim is null)
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
                var adv = AdvanceLine(doc, forward: true, ww, wh);
                switch (adv)
                {
                    case LineAdvanceResult.PageChanged:
                        pageChanged = true;
                        doc.Rail.StartAutoScroll(AutoScrollSpeed);
                        doc.Rail.PauseAutoScroll(_config.AutoScrollBlockPauseMs);
                        break;
                    case LineAdvanceResult.PageChangedRailLost:
                        pageChanged = true;
                        StopAutoScroll();
                        break;
                    case LineAdvanceResult.LineAdvanced:
                        doc.StartSnap(ww, wh);
                        doc.Rail.PauseAutoScroll(_config.AutoScrollLinePauseMs);
                        break;
                }
                overlayChanged = true;
            }
        }
    }

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
            Console.Error.WriteLine($"[Analysis] Got result for {Path.GetFileName(result.FilePath)} page {result.Page}: {result.Analysis.Blocks.Count} blocks");
            foreach (var doc in Documents)
            {
                if (doc.FilePath != result.FilePath) continue;

                doc.AnalysisCache[result.Page] = result.Analysis;
                if (doc.CurrentPage == result.Page && doc.PendingRailSetup)
                {
                    doc.Rail.SetAnalysis(result.Analysis, _config.NavigableClasses);
                    doc.PendingRailSetup = false;
                    doc.UpdateRailZoom(ww, wh);
                    Console.Error.WriteLine($"[Analysis] Rail has {doc.Rail.NavigableCount} navigable blocks, Active={doc.Rail.Active}");
                    if (doc.Rail.Active)
                    {
                        doc.StartSnap(ww, wh);
                        needsAnim = true;
                    }
                }
            }
        }
        return (got, needsAnim);
    }

    // --- Annotation tool ---

    public void SetAnnotationTool(AnnotationTool tool)
    {
        ActiveTool = tool;
        SelectedAnnotation = null;
        PreviewAnnotation = null;
        _freehandPoints = null;
        _highlightCharStart = -1;

        if (tool != AnnotationTool.TextSelect)
        {
            SelectedText = null;
            TextSelectionRects = null;
            _textSelectCharStart = -1;
        }

        switch (tool)
        {
            case AnnotationTool.Highlight:
                var hc = HighlightColors[_highlightColorIndex];
                ActiveAnnotationColor = hc.Color;
                ActiveAnnotationOpacity = hc.Opacity;
                break;
            case AnnotationTool.Pen:
                var pc = PenColors[_penColorIndex];
                ActiveAnnotationColor = pc.Color;
                ActiveAnnotationOpacity = pc.Opacity;
                ActiveStrokeWidth = 2f;
                break;
            case AnnotationTool.Rectangle:
                ActiveAnnotationColor = "#0066FF";
                ActiveAnnotationOpacity = 0.5f;
                ActiveStrokeWidth = 2f;
                break;
            case AnnotationTool.TextNote:
                ActiveAnnotationColor = "#FFCC00";
                ActiveAnnotationOpacity = 0.9f;
                break;
            case AnnotationTool.Eraser:
            case AnnotationTool.None:
                break;
        }
    }

    public void SetAnnotationColorIndex(AnnotationTool tool, int index)
    {
        switch (tool)
        {
            case AnnotationTool.Highlight:
                _highlightColorIndex = Math.Clamp(index, 0, HighlightColors.Length - 1);
                break;
            case AnnotationTool.Pen:
                _penColorIndex = Math.Clamp(index, 0, PenColors.Length - 1);
                break;
        }
    }

    public int GetAnnotationColorIndex(AnnotationTool tool) => tool switch
    {
        AnnotationTool.Highlight => _highlightColorIndex,
        AnnotationTool.Pen => _penColorIndex,
        _ => 0,
    };

    public void CancelAnnotationTool()
    {
        PreviewAnnotation = null;
        _freehandPoints = null;
        _highlightCharStart = -1;
        _textSelectCharStart = -1;
        SelectedText = null;
        TextSelectionRects = null;
        ActiveTool = AnnotationTool.None;
        SelectedAnnotation = null;
    }

    /// <summary>
    /// Handle pointer down in annotation mode. Returns true if a text note dialog
    /// is needed (caller should show dialog and call CompleteTextNote/CompleteTextNoteEdit).
    /// </summary>
    public (bool NeedsTextNoteDialog, bool IsEdit, TextNoteAnnotation? ExistingNote, float PageX, float PageY)
        HandleAnnotationPointerDown(double pageX, double pageY)
    {
        if (ActiveDocument is not { } doc) return default;

        switch (ActiveTool)
        {
            case AnnotationTool.TextSelect:
                _textSelectCharStart = FindNearestCharIndex(doc, (float)pageX, (float)pageY);
                SelectedText = null;
                TextSelectionRects = null;
                break;
            case AnnotationTool.Highlight:
                _highlightCharStart = FindNearestCharIndex(doc, (float)pageX, (float)pageY);
                break;
            case AnnotationTool.Pen:
                _freehandPoints = [new PointF((float)pageX, (float)pageY)];
                break;
            case AnnotationTool.Rectangle:
                _rectStartX = (float)pageX;
                _rectStartY = (float)pageY;
                break;
            case AnnotationTool.TextNote:
                var hitNote = FindTextNoteAtPoint(doc, (float)pageX, (float)pageY);
                return (true, hitNote is not null, hitNote, (float)pageX, (float)pageY);
            case AnnotationTool.Eraser:
                EraseAtPoint(doc, (float)pageX, (float)pageY);
                break;
        }
        return default;
    }

    /// <summary>
    /// Complete a text note creation after dialog returns.
    /// </summary>
    public void CompleteTextNote(float pageX, float pageY, string text)
    {
        if (ActiveDocument is not { } doc || string.IsNullOrEmpty(text)) return;
        var note = new TextNoteAnnotation
        {
            X = pageX,
            Y = pageY,
            Color = ActiveAnnotationColor,
            Opacity = ActiveAnnotationOpacity,
            Text = text,
        };
        doc.AddAnnotation(doc.CurrentPage, note);
    }

    /// <summary>
    /// Complete a text note edit after dialog returns.
    /// </summary>
    public void CompleteTextNoteEdit(TextNoteAnnotation note, string newText)
    {
        if (ActiveDocument is not { } doc) return;
        doc.UpdateAnnotationText(doc.CurrentPage, note, newText);
    }

    public bool HandleAnnotationPointerMove(double pageX, double pageY)
    {
        if (ActiveDocument is not { } doc) return false;
        bool changed = false;

        switch (ActiveTool)
        {
            case AnnotationTool.TextSelect when _textSelectCharStart >= 0:
                int tsEnd = FindNearestCharIndex(doc, (float)pageX, (float)pageY);
                if (tsEnd >= 0)
                {
                    int tsStart = Math.Min(_textSelectCharStart, tsEnd);
                    int tsLen = Math.Max(_textSelectCharStart, tsEnd) - tsStart + 1;
                    var pageText = doc.GetOrExtractText(doc.CurrentPage);
                    TextSelectionRects = BuildHighlightRects(pageText, tsStart, tsLen);
                    int textEnd = Math.Min(tsStart + tsLen, pageText.Text.Length);
                    SelectedText = tsStart < pageText.Text.Length
                        ? pageText.Text[tsStart..textEnd]
                        : null;
                    changed = true;
                }
                break;
            case AnnotationTool.Highlight when _highlightCharStart >= 0:
                int endChar = FindNearestCharIndex(doc, (float)pageX, (float)pageY);
                if (endChar >= 0)
                {
                    int start = Math.Min(_highlightCharStart, endChar);
                    int end = Math.Max(_highlightCharStart, endChar);
                    var pageText = doc.GetOrExtractText(doc.CurrentPage);
                    var rects = BuildHighlightRects(pageText, start, end - start + 1);
                    PreviewAnnotation = new HighlightAnnotation
                    {
                        Rects = rects,
                        Color = ActiveAnnotationColor,
                        Opacity = ActiveAnnotationOpacity,
                    };
                    changed = true;
                }
                break;
            case AnnotationTool.Pen when _freehandPoints is not null:
                _freehandPoints.Add(new PointF((float)pageX, (float)pageY));
                PreviewAnnotation = new FreehandAnnotation
                {
                    Points = [.. _freehandPoints],
                    Color = ActiveAnnotationColor,
                    Opacity = ActiveAnnotationOpacity,
                    StrokeWidth = ActiveStrokeWidth,
                };
                changed = true;
                break;
            case AnnotationTool.Rectangle:
                float rx = Math.Min(_rectStartX, (float)pageX);
                float ry = Math.Min(_rectStartY, (float)pageY);
                float rw = Math.Abs((float)pageX - _rectStartX);
                float rh = Math.Abs((float)pageY - _rectStartY);
                PreviewAnnotation = new RectAnnotation
                {
                    X = rx, Y = ry, W = rw, H = rh,
                    Color = ActiveAnnotationColor,
                    Opacity = ActiveAnnotationOpacity,
                    StrokeWidth = ActiveStrokeWidth,
                };
                changed = true;
                break;
        }
        return changed;
    }

    public bool HandleAnnotationPointerUp(double pageX, double pageY)
    {
        if (ActiveDocument is not { } doc) return false;
        bool changed = false;

        switch (ActiveTool)
        {
            case AnnotationTool.TextSelect:
                _textSelectCharStart = -1;
                break;
            case AnnotationTool.Highlight when PreviewAnnotation is HighlightAnnotation h:
                doc.AddAnnotation(doc.CurrentPage, h);
                PreviewAnnotation = null;
                _highlightCharStart = -1;
                changed = true;
                break;
            case AnnotationTool.Pen when PreviewAnnotation is FreehandAnnotation f:
                doc.AddAnnotation(doc.CurrentPage, f);
                PreviewAnnotation = null;
                _freehandPoints = null;
                changed = true;
                break;
            case AnnotationTool.Rectangle when PreviewAnnotation is RectAnnotation r:
                if (r.W > 1 && r.H > 1)
                    doc.AddAnnotation(doc.CurrentPage, r);
                PreviewAnnotation = null;
                changed = true;
                break;
        }
        return changed;
    }

    private void EraseAtPoint(DocumentState doc, float pageX, float pageY)
    {
        var list = GetCurrentPageAnnotations(doc);
        if (list is null) return;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (AnnotationRenderer.HitTest(list[i], pageX, pageY))
            {
                doc.RemoveAnnotation(doc.CurrentPage, list[i]);
                return;
            }
        }
    }

    public void CopySelectedText()
    {
        if (SelectedText is not null)
            CopyToClipboard?.Invoke(SelectedText);
    }

    public void UndoAnnotation() => ActiveDocument?.Undo();

    public void RedoAnnotation() => ActiveDocument?.Redo();

    /// <summary>
    /// Delete the currently selected annotation (if any) in browse mode.
    /// Returns true if an annotation was deleted.
    /// </summary>
    public bool DeleteSelectedAnnotation()
    {
        if (ActiveDocument is not { } doc || SelectedAnnotation is null) return false;
        doc.RemoveAnnotation(doc.CurrentPage, SelectedAnnotation);
        SelectedAnnotation = null;
        return true;
    }

    // --- Browse-mode interaction (select, move, resize) ---

    /// <summary>
    /// Handle pointer down in browse mode. Returns true if an annotation was hit
    /// (caller should not start camera pan).
    /// </summary>
    public bool HandleBrowsePointerDown(float pageX, float pageY)
    {
        if (ActiveDocument is not { } doc) return false;
        var list = GetCurrentPageAnnotations(doc);

        // First check resize handles on selected freehand
        if (SelectedAnnotation is FreehandAnnotation selectedFreehand && list is not null)
        {
            var handle = AnnotationRenderer.HitTestResizeHandle(selectedFreehand, pageX, pageY);
            if (handle != ResizeHandle.None)
            {
                _resizeHandle = handle;
                _dragStartPageX = pageX;
                _dragStartPageY = pageY;
                var bounds = AnnotationRenderer.GetAnnotationBounds(selectedFreehand);
                _resizeStartBounds = bounds ?? SKRect.Empty;
                _resizeOriginalPoints = [.. selectedFreehand.Points];
                return true;
            }
        }

        // Hit-test annotations (top to bottom)
        if (list is not null)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (AnnotationRenderer.HitTest(list[i], pageX, pageY))
                {
                    SelectedAnnotation = list[i];
                    _dragAnnotation = list[i];
                    _dragStartPageX = pageX;
                    _dragStartPageY = pageY;
                    _dragOriginalPosition = PositionSnapshot.Capture(list[i]);
                    _resizeHandle = ResizeHandle.None;
                    return true;
                }
            }
        }

        // Clicked empty space — deselect
        SelectedAnnotation = null;
        _dragAnnotation = null;
        _resizeHandle = ResizeHandle.None;
        return false;
    }

    /// <summary>
    /// Handle pointer move in browse mode (dragging annotation or resizing).
    /// Returns true if annotations changed.
    /// </summary>
    public bool HandleBrowsePointerMove(float pageX, float pageY)
    {
        if (_resizeHandle != ResizeHandle.None && _resizeOriginalPoints is not null
            && SelectedAnnotation is FreehandAnnotation freehand)
        {
            ResizeFreehand(freehand, pageX, pageY);
            return true;
        }

        if (_dragAnnotation is null) return false;

        float dx = pageX - _dragStartPageX;
        float dy = pageY - _dragStartPageY;

        MoveAnnotation(_dragAnnotation, dx, dy, _dragOriginalPosition!);
        return true;
    }

    /// <summary>
    /// Handle pointer up in browse mode. Creates undo action if moved/resized.
    /// Returns true if annotations changed.
    /// </summary>
    public bool HandleBrowsePointerUp(float pageX, float pageY)
    {
        if (ActiveDocument is not { } doc) return false;

        if (_resizeHandle != ResizeHandle.None && _resizeOriginalPoints is not null
            && SelectedAnnotation is FreehandAnnotation freehand)
        {
            List<PointF> newPoints = [.. freehand.Points];
            if (!PointsEqual(_resizeOriginalPoints, newPoints))
                doc.PushUndoAction(new ResizeFreehandAction(freehand, _resizeOriginalPoints, newPoints));
            _resizeHandle = ResizeHandle.None;
            _resizeOriginalPoints = null;
            return true;
        }

        if (_dragAnnotation is not null && _dragOriginalPosition is not null)
        {
            var newPosition = PositionSnapshot.Capture(_dragAnnotation);
            float dx = pageX - _dragStartPageX;
            float dy = pageY - _dragStartPageY;
            if (Math.Abs(dx) > 0.5f || Math.Abs(dy) > 0.5f)
                doc.PushUndoAction(new MoveAnnotationAction(_dragAnnotation, _dragOriginalPosition, newPosition));
            _dragAnnotation = null;
            _dragOriginalPosition = null;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Handle click in browse mode on text notes (toggle expand/collapse).
    /// Returns true if a note was toggled. Sets EditNote if double-click editing needed.
    /// </summary>
    public (bool Handled, TextNoteAnnotation? EditNote) HandleBrowseClick(float pageX, float pageY)
    {
        if (ActiveDocument is not { } doc) return (false, null);
        var list = GetCurrentPageAnnotations(doc);
        if (list is null) return (false, null);

        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i] is TextNoteAnnotation tn && AnnotationRenderer.HitTest(tn, pageX, pageY))
            {
                SelectedAnnotation = tn;
                tn.IsExpanded = !tn.IsExpanded;
                return (true, null);
            }
        }
        return (false, null);
    }

    private static void MoveAnnotation(Annotation annotation, float dx, float dy, PositionSnapshot original)
    {
        switch (annotation)
        {
            case TextNoteAnnotation tn:
                tn.X = original.X + dx;
                tn.Y = original.Y + dy;
                break;
            case FreehandAnnotation f when original.Points is not null:
                for (int i = 0; i < f.Points.Count && i < original.Points.Count; i++)
                    f.Points[i] = new PointF(original.Points[i].X + dx, original.Points[i].Y + dy);
                break;
            case HighlightAnnotation h when original.Rects is not null:
                for (int i = 0; i < h.Rects.Count && i < original.Rects.Count; i++)
                {
                    var or = original.Rects[i];
                    h.Rects[i] = new HighlightRect(or.X + dx, or.Y + dy, or.W, or.H);
                }
                break;
            case RectAnnotation r:
                r.X = original.X + dx;
                r.Y = original.Y + dy;
                break;
        }
    }

    private void ResizeFreehand(FreehandAnnotation freehand, float pageX, float pageY)
    {
        if (_resizeOriginalPoints is null) return;
        var oldBounds = _resizeStartBounds;
        if (oldBounds.Width < 1 || oldBounds.Height < 1) return;

        var newBounds = ComputeNewBounds(oldBounds, _resizeHandle, pageX, pageY, _dragStartPageX, _dragStartPageY);

        // Minimum size constraint
        if (newBounds.Width < 10 || newBounds.Height < 10) return;

        // Scale all points proportionally
        for (int i = 0; i < freehand.Points.Count && i < _resizeOriginalPoints.Count; i++)
        {
            var op = _resizeOriginalPoints[i];
            float nx = (op.X - oldBounds.Left) / oldBounds.Width;
            float ny = (op.Y - oldBounds.Top) / oldBounds.Height;
            freehand.Points[i] = new PointF(newBounds.Left + nx * newBounds.Width, newBounds.Top + ny * newBounds.Height);
        }
    }

    private static SKRect ComputeNewBounds(SKRect old, ResizeHandle handle, float px, float py, float startX, float startY)
    {
        float dx = px - startX;
        float dy = py - startY;
        float l = old.Left, t = old.Top, r = old.Right, b = old.Bottom;

        switch (handle)
        {
            case ResizeHandle.TopLeft:     l += dx; t += dy; break;
            case ResizeHandle.Top:         t += dy; break;
            case ResizeHandle.TopRight:    r += dx; t += dy; break;
            case ResizeHandle.Right:       r += dx; break;
            case ResizeHandle.BottomRight: r += dx; b += dy; break;
            case ResizeHandle.Bottom:      b += dy; break;
            case ResizeHandle.BottomLeft:  l += dx; b += dy; break;
            case ResizeHandle.Left:        l += dx; break;
        }

        return new SKRect(Math.Min(l, r), Math.Min(t, b), Math.Max(l, r), Math.Max(t, b));
    }

    private static bool PointsEqual(List<PointF> a, List<PointF> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (Math.Abs(a[i].X - b[i].X) > 0.1f || Math.Abs(a[i].Y - b[i].Y) > 0.1f)
                return false;
        return true;
    }

    // --- Search ---

    public void CloseSearch()
    {
        SearchMatches = [];
        _searchMatchesByPage = [];
        CurrentPageSearchMatches = null;
        ActiveMatchIndex = 0;
    }

    public void ExecuteSearch(string query, bool caseSensitive, bool useRegex)
    {
        SearchMatches = [];
        _searchMatchesByPage = [];
        CurrentPageSearchMatches = null;
        ActiveMatchIndex = 0;

        if (string.IsNullOrEmpty(query) || ActiveDocument is not { } doc)
            return;

        var (regex, comparison) = PrepareSearchParams(query, caseSensitive, useRegex);
        if (useRegex && regex is null) return; // invalid regex

        var allMatches = new List<SearchMatch>();
        for (int page = 0; page < doc.PageCount; page++)
            SearchPage(doc, page, query, regex, comparison, allMatches);

        FinalizeSearch(doc, allMatches);
    }

    /// <summary>
    /// Prepares search parameters. Returns null regex for invalid regex patterns.
    /// </summary>
    public static (Regex? Regex, StringComparison Comparison) PrepareSearchParams(
        string query, bool caseSensitive, bool useRegex)
    {
        Regex? regex = null;
        if (useRegex)
        {
            try
            {
                var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                regex = new Regex(query, options);
            }
            catch (RegexParseException) { }
        }
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return (regex, comparison);
    }

    /// <summary>
    /// Searches a single page and appends matches to the list.
    /// Uses PDFium's FPDFText_CountRects/GetRect for accurate highlight positioning.
    /// </summary>
    public static void SearchPage(DocumentState doc, int page, string query,
        Regex? regex, StringComparison comparison, List<SearchMatch> results)
    {
        var pageText = doc.GetOrExtractText(page);
        if (string.IsNullOrEmpty(pageText.Text)) return;

        IEnumerable<(int Index, int Length)> hits;
        if (regex is not null)
            hits = regex.Matches(pageText.Text).Select(m => (m.Index, m.Length));
        else
            hits = FindAllOccurrences(pageText.Text, query, comparison);

        // Collect all hits first so we can batch the PDFium rect query
        var hitList = hits.ToList();
        if (hitList.Count == 0) return;

        var allRects = PdfTextService.GetTextRangeRects(doc.Pdf.PdfBytes, page, hitList);

        for (int i = 0; i < hitList.Count; i++)
        {
            var rects = allRects[i];
            if (rects.Count > 0)
                results.Add(new SearchMatch(page, hitList[i].Index, hitList[i].Length, rects));
        }
    }

    /// <summary>
    /// Finalizes search results: sets active match, navigates, updates current page matches.
    /// </summary>
    public void FinalizeSearch(DocumentState doc, List<SearchMatch> allMatches)
    {
        SearchMatches = allMatches;
        _searchMatchesByPage = allMatches
            .GroupBy(m => m.PageIndex)
            .ToDictionary(g => g.Key, g => g.ToList());
        if (allMatches.Count > 0)
        {
            int firstOnCurrentOrAfter = allMatches.FindIndex(m => m.PageIndex >= doc.CurrentPage);
            ActiveMatchIndex = firstOnCurrentOrAfter >= 0 ? firstOnCurrentOrAfter : 0;
            NavigateToActiveMatch();
        }
        UpdateCurrentPageMatches();
    }

    public void NextMatch()
    {
        if (SearchMatches.Count == 0) return;
        ActiveMatchIndex = (ActiveMatchIndex + 1) % SearchMatches.Count;
        NavigateToActiveMatch();
        UpdateCurrentPageMatches();
    }

    public void PreviousMatch()
    {
        if (SearchMatches.Count == 0) return;
        ActiveMatchIndex = (ActiveMatchIndex - 1 + SearchMatches.Count) % SearchMatches.Count;
        NavigateToActiveMatch();
        UpdateCurrentPageMatches();
    }

    public void GoToMatch(int matchIndex)
    {
        if (matchIndex < 0 || matchIndex >= SearchMatches.Count) return;
        ActiveMatchIndex = matchIndex;
        NavigateToActiveMatch();
        UpdateCurrentPageMatches();
    }

    public (string Pre, string Match, string Post) GetMatchSnippet(SearchMatch match, int contextChars = 40)
    {
        var text = ActiveDocument?.GetOrExtractText(match.PageIndex).Text;
        if (text is null) return ("", "", "");

        int start = Math.Max(0, match.CharStart - contextChars);
        int end = Math.Min(text.Length, match.CharStart + match.CharLength + contextChars);
        int matchEnd = Math.Min(match.CharStart + match.CharLength, text.Length);

        string pre = (start > 0 ? "\u2026" : "") + text[start..match.CharStart];
        string matchStr = text[match.CharStart..matchEnd];
        string post = text[matchEnd..end] + (end < text.Length ? "\u2026" : "");

        // Clean up whitespace for display
        pre = pre.Replace('\n', ' ').Replace('\r', ' ');
        matchStr = matchStr.Replace('\n', ' ').Replace('\r', ' ');
        post = post.Replace('\n', ' ').Replace('\r', ' ');

        return (pre, matchStr, post);
    }

    private void NavigateToActiveMatch()
    {
        if (ActiveDocument is not { } doc) return;
        if (ActiveMatchIndex < 0 || ActiveMatchIndex >= SearchMatches.Count) return;
        var match = SearchMatches[ActiveMatchIndex];
        if (match.PageIndex != doc.CurrentPage)
            GoToPage(match.PageIndex);

        if (doc.Rail.Active && doc.Rail.HasAnalysis && match.Rects.Count > 0)
        {
            // Set rail to the block/line containing the match center
            var rect = match.Rects[0];
            double matchCenterY = (rect.Top + rect.Bottom) / 2.0;
            doc.Rail.FindBlockNearPoint((rect.Left + rect.Right) / 2.0, matchCenterY);
            var (ww, wh) = GetViewportSize();
            doc.StartSnap(ww, wh);
        }
        else
        {
            ScrollToMatchRect(doc, match);
        }
    }

    private void ScrollToMatchRect(DocumentState doc, SearchMatch match)
    {
        if (match.Rects.Count == 0) return;
        var (ww, wh) = GetViewportSize();

        // Compute bounding box of all match rects
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        foreach (var r in match.Rects)
        {
            if (r.Left < minX) minX = r.Left;
            if (r.Top < minY) minY = r.Top;
            if (r.Right > maxX) maxX = r.Right;
            if (r.Bottom > maxY) maxY = r.Bottom;
        }

        // Center the match bounding box in the viewport
        double centerX = (minX + maxX) / 2.0;
        double centerY = (minY + maxY) / 2.0;
        doc.Camera.OffsetX = ww / 2.0 - centerX * doc.Camera.Zoom;
        doc.Camera.OffsetY = wh / 2.0 - centerY * doc.Camera.Zoom;
        doc.ClampCamera(ww, wh);
    }

    public void UpdateCurrentPageMatches()
    {
        if (ActiveDocument is not { } doc)
        {
            CurrentPageSearchMatches = null;
            return;
        }
        _searchMatchesByPage.TryGetValue(doc.CurrentPage, out var matches);
        CurrentPageSearchMatches = matches;
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

    public SearchResult GetSearchState()
    {
        var perPage = SearchMatches
            .GroupBy(m => m.PageIndex)
            .ToDictionary(g => g.Key, g => g.Count());
        return new SearchResult(SearchMatches.Count, ActiveMatchIndex, perPage);
    }

    // --- Static helpers ---

    public static string? FindModelPath()
    {
        const string filename = "PP-DocLayoutV3.onnx";
        string?[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "models", filename),
            Environment.GetEnvironmentVariable("APPDIR") is { } appDir
                ? Path.Combine(appDir, "models", filename) : null,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "railreader2", "models", filename),
            Path.Combine("models", filename),
            Path.Combine("..", "models", filename),
            Path.Combine("..", "..", "models", filename),
            Path.Combine("..", "..", "..", "models", filename),
        ];

        foreach (var path in candidates)
        {
            if (path is not null && File.Exists(path))
                return Path.GetFullPath(path);
        }
        return null;
    }

    private static List<Annotation>? GetCurrentPageAnnotations(DocumentState doc)
    {
        return doc.Annotations.Pages.TryGetValue(doc.CurrentPage, out var list) ? list : null;
    }

    private static TextNoteAnnotation? FindTextNoteAtPoint(DocumentState doc, float pageX, float pageY)
    {
        if (GetCurrentPageAnnotations(doc) is not { } list) return null;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i] is TextNoteAnnotation tn && AnnotationRenderer.HitTest(tn, pageX, pageY))
                return tn;
        }
        return null;
    }

    private static int FindNearestCharIndex(DocumentState doc, float pageX, float pageY)
    {
        var pageText = doc.GetOrExtractText(doc.CurrentPage);
        if (pageText.CharBoxes.Count == 0) return -1;

        float bestDist = float.MaxValue;
        int bestIdx = -1;
        for (int i = 0; i < pageText.CharBoxes.Count; i++)
        {
            var cb = pageText.CharBoxes[i];
            if (cb.Left == 0 && cb.Right == 0 && cb.Top == 0 && cb.Bottom == 0) continue;
            float cx = (cb.Left + cb.Right) / 2;
            float cy = (cb.Top + cb.Bottom) / 2;
            float dist = (cx - pageX) * (cx - pageX) + (cy - pageY) * (cy - pageY);
            if (dist < bestDist) { bestDist = dist; bestIdx = i; }
        }
        return bestIdx;
    }

    private static List<HighlightRect> BuildHighlightRects(PageText pageText, int charStart, int charLength)
    {
        var rects = new List<HighlightRect>();
        foreach (var (l, t, r, b) in MergeCharBoxesIntoLines(pageText, charStart, charLength))
            rects.Add(new HighlightRect(l - 1, t, r - l + 2, b - t));
        return rects;
    }

    private static IEnumerable<(int Index, int Length)> FindAllOccurrences(string text, string query, StringComparison comparison)
    {
        int pos = 0;
        while (pos < text.Length)
        {
            int idx = text.IndexOf(query, pos, comparison);
            if (idx < 0) break;
            yield return (idx, query.Length);
            pos = idx + 1;
        }
    }

    private static IEnumerable<(float Left, float Top, float Right, float Bottom)> MergeCharBoxesIntoLines(
        PageText pageText, int charStart, int charLength)
    {
        if (pageText.CharBoxes.Count == 0) yield break;

        int end = Math.Min(charStart + charLength, pageText.CharBoxes.Count);
        float curLeft = 0, curTop = 0, curRight = 0, curBottom = 0;
        bool hasRect = false;
        const float lineThreshold = 4f;

        for (int i = charStart; i < end; i++)
        {
            var cb = pageText.CharBoxes[i];
            if (cb.Left == 0 && cb.Right == 0 && cb.Top == 0 && cb.Bottom == 0) continue;

            if (!hasRect)
            {
                curLeft = cb.Left; curTop = cb.Top; curRight = cb.Right; curBottom = cb.Bottom;
                hasRect = true;
            }
            else if (Math.Abs(cb.Top - curTop) < lineThreshold)
            {
                curLeft = Math.Min(curLeft, cb.Left);
                curRight = Math.Max(curRight, cb.Right);
                curTop = Math.Min(curTop, cb.Top);
                curBottom = Math.Max(curBottom, cb.Bottom);
            }
            else
            {
                yield return (curLeft, curTop, curRight, curBottom);
                curLeft = cb.Left; curTop = cb.Top; curRight = cb.Right; curBottom = cb.Bottom;
            }
        }

        if (hasRect)
            yield return (curLeft, curTop, curRight, curBottom);
    }
}
