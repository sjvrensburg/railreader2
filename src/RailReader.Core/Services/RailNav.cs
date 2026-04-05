using System.Diagnostics;
using RailReader.Core.Models;

namespace RailReader.Core.Services;

public sealed class RailNav : ICameraClamp
{
    private AppConfig _config;
    private PageAnalysis? _analysis;
    private readonly List<int> _navigableIndices = [];

    public int CurrentBlock { get; set; }
    public int CurrentLine { get; set; }
    public bool Active { get; set; }
    public double ScrollSpeed { get; private set; }

    /// <summary>
    /// Vertical offset from center (in pixels). Positive = line drawn above center.
    /// Set by user panning in rail mode; preserved across line navigation.
    /// </summary>
    public double VerticalBias { get; set; }

    private SnapAnimation? _snap;
    private readonly HorizontalScrollState _scroll = new();

    // Auto-scroll state machine: explicit states replace the previous implicit flag combinations
    private readonly AutoScrollStateMachine _autoScrollState;
    public bool AutoScrolling => _autoScrollState.IsActive;

    /// <summary>
    /// Set to true when sustained horizontal scroll triggers auto-scroll.
    /// The controller consumes this signal to transition state.
    /// </summary>
    private bool _autoScrollTriggered;
    public bool ConsumeAutoScrollTrigger()
    {
        if (!_autoScrollTriggered) return false;
        _autoScrollTriggered = false;
        return true;
    }

    // Edge-hold advance: when the user holds D/Right (or A/Left) against the line boundary,
    // the state machine fires a line advance after a hold threshold.
    private readonly EdgeHoldStateMachine _lineEdgeHold = new();


    /// <summary>
    /// When a block's width is less than this fraction of the viewport,
    /// center it horizontally instead of left-aligning (if the block type allows centering).
    /// </summary>
    private const double CenterBlockThreshold = 0.75;

    /// <summary>
    /// Whether the current navigable block's type is in the centering class set.
    /// </summary>
    private bool ShouldCenterBlock() =>
        _config.CenteringClasses.Contains(CurrentNavigableBlock.ClassId);

    /// <summary>
    /// Returns true if input should be suppressed because an edge-hold advance
    /// snap animation is still in progress.
    /// </summary>
    private bool ShouldSuppressAfterEdgeAdvance()
    {
        if (!_lineEdgeHold.AdvanceJustFired) return false;
        if (_snap is not null) return true; // snap still running
        _lineEdgeHold.ClearAdvanceFlag();
        return false;
    }

    public RailNav(AppConfig config)
    {
        _config = config;
        _autoScrollState = new AutoScrollStateMachine(this);
    }

    double ICameraClamp.ClampX(double cameraX, double zoom, double windowWidth)
        => ClampX(cameraX, zoom, windowWidth);

    public void SetAnalysis(PageAnalysis analysis, HashSet<int> navigable)
    {
        // If re-applying the same analysis (e.g. config change that didn't affect
        // navigable classes), preserve the current navigation position.
        bool sameAnalysis = ReferenceEquals(_analysis, analysis);

        _navigableIndices.Clear();
        for (int i = 0; i < analysis.Blocks.Count; i++)
        {
            if (navigable.Contains(analysis.Blocks[i].ClassId))
                _navigableIndices.Add(i);
        }
        _analysis = analysis;

        if (!sameAnalysis)
        {
            CurrentBlock = 0;
            CurrentLine = 0;
            VerticalBias = 0;
            _snap = null;
            _scroll.Stop();
            ScrollSpeed = 0.0;
        }
        else
        {
            // Clamp in case navigable set changed and current block is out of range
            if (CurrentBlock >= _navigableIndices.Count)
                CurrentBlock = Math.Max(0, _navigableIndices.Count - 1);
            if (_navigableIndices.Count > 0 && CurrentLine >= CurrentNavigableBlock.Lines.Count)
                CurrentLine = Math.Max(0, CurrentNavigableBlock.Lines.Count - 1);
        }
    }

    public bool HasAnalysis => _analysis is not null && _navigableIndices.Count > 0;
    public PageAnalysis? Analysis => _analysis;
    public int NavigableCount => _navigableIndices.Count;

    /// <summary>True when rail mode is active and has navigable blocks.</summary>
    private bool CanNavigate => Active && _navigableIndices.Count > 0;

    public int CurrentLineCount =>
        _navigableIndices.Count == 0 ? 0 : CurrentNavigableBlock.Lines.Count;

    public void UpdateZoom(double zoom, double cameraX, double cameraY, double windowWidth, double windowHeight,
        double? cursorPageX = null, double? cursorPageY = null)
    {
        bool shouldBeActive = zoom >= _config.RailZoomThreshold && HasAnalysis;

        if (shouldBeActive && !Active)
        {
            Active = true;
            if (cursorPageX.HasValue && cursorPageY.HasValue)
                FindBlockNearPoint(cursorPageX.Value, cursorPageY.Value);
            else
                FindNearestBlock(cameraX, cameraY, zoom, windowWidth, windowHeight);
        }
        else if (!shouldBeActive && Active)
        {
            Active = false;
            _snap = null;
            _scroll.Stop();
            ScrollSpeed = 0.0;
        }
    }

    /// <summary>
    /// Scale VerticalBias proportionally so the active line stays at the
    /// same screen position when zoom changes incrementally.
    /// </summary>
    public void ScaleVerticalBias(double previousZoom, double newZoom)
    {
        if (Active && previousZoom > 0 && Math.Abs(previousZoom - newZoom) > 0.001)
            VerticalBias *= newZoom / previousZoom;
    }

    public void FindNearestBlock(double cameraX, double cameraY, double zoom, double windowWidth, double windowHeight)
    {
        if (_analysis is null) return;

        double centerX = (windowWidth / 2.0 - cameraX) / zoom;
        double centerY = (windowHeight / 2.0 - cameraY) / zoom;

        CurrentBlock = FindNearestNavigableIndex(centerX, centerY);
        CurrentLine = 0;
    }

    /// <summary>
    /// Finds the navigable block nearest to a point in page coordinates.
    /// Tries a direct bounding-box hit first; falls back to nearest-center distance.
    /// </summary>
    public void FindBlockNearPoint(double pageX, double pageY)
    {
        if (_analysis is null || _navigableIndices.Count == 0) return;

        int? hit = FindBlockAtPoint(pageX, pageY);
        CurrentBlock = hit ?? FindNearestNavigableIndex(pageX, pageY);
        CurrentLine = FindNearestLine(CurrentNavigableBlock, pageY);
    }

    private int FindNearestNavigableIndex(double pageX, double pageY)
    {
        double bestDist = double.MaxValue;
        int bestIdx = 0;
        for (int i = 0; i < _navigableIndices.Count; i++)
        {
            var block = _analysis!.Blocks[_navigableIndices[i]];
            double dx = block.BBox.X + block.BBox.W / 2.0 - pageX;
            double dy = block.BBox.Y + block.BBox.H / 2.0 - pageY;
            double dist = dx * dx + dy * dy;
            if (dist < bestDist) { bestDist = dist; bestIdx = i; }
        }
        return bestIdx;
    }

    private static int FindNearestLine(LayoutBlock block, double pageY)
    {
        double bestDist = double.MaxValue;
        int bestLine = 0;
        for (int j = 0; j < block.Lines.Count; j++)
        {
            double lineMid = block.Lines[j].Y + block.Lines[j].Height / 2.0;
            double d = Math.Abs(lineMid - pageY);
            if (d < bestDist) { bestDist = d; bestLine = j; }
        }
        return bestLine;
    }

    public NavResult NextLine()
    {
        if (!CanNavigate) return NavResult.Ok;

        var block = CurrentNavigableBlock;
        if (CurrentLine + 1 < block.Lines.Count)
        {
            CurrentLine++;
            return NavResult.Ok;
        }
        if (CurrentBlock + 1 < _navigableIndices.Count)
        {
            CurrentBlock++;
            CurrentLine = 0;
            return NavResult.Ok;
        }
        return NavResult.PageBoundaryNext;
    }

    public NavResult PrevLine()
    {
        if (!CanNavigate) return NavResult.Ok;

        if (CurrentLine > 0)
        {
            CurrentLine--;
            return NavResult.Ok;
        }
        if (CurrentBlock > 0)
        {
            CurrentBlock--;
            CurrentLine = CurrentNavigableBlock.Lines.Count - 1;
            return NavResult.Ok;
        }
        return NavResult.PageBoundaryPrev;
    }

    public void StartScroll(ScrollDirection dir, double currentCameraX)
    {
        if (!CanNavigate) return;

        if (ShouldSuppressAfterEdgeAdvance()) return;

        if (_scroll.Direction != dir)
        {
            // If a snap is in progress, jump to its target and start scrolling from there.
            double startX = _snap is { } activeSnap ? activeSnap.TargetX : currentCameraX;
            _snap = null;
            _scroll.Start(dir, startX, _config.ScrollSpeedStart, _config.ScrollSpeedMax);
        }
    }

    public void StopScroll()
    {
        _scroll.Stop();
        ScrollSpeed = 0.0;
    }

    /// <summary>Clears both scroll and edge-hold state (e.g. on key release or mode change).</summary>
    public void StopScrollAndEdgeHold()
    {
        StopScroll();
        _lineEdgeHold.Reset();
    }

    /// <summary>
    /// Returns the direction of a pending edge-advance (triggered by holding D/Right or A/Left
    /// against the line boundary) and clears it. Returns null if none pending.
    /// </summary>
    public ScrollDirection? ConsumePendingEdgeAdvance()
        => _lineEdgeHold.ConsumePendingAdvance();

    /// <summary>
    /// Checks whether the camera is at the hard edge for the given direction and, if so,
    /// accumulates hold time via the edge-hold state machine. Returns true when the hold
    /// threshold is reached and an edge advance has been triggered.
    /// </summary>
    private bool CheckEdgeHoldAdvance(double cameraX, double zoom, double windowWidth, ScrollDirection dir)
    {
        if (IsAtHardEdge(cameraX, zoom, windowWidth, dir))
            return _lineEdgeHold.OnEdgeHit(forward: dir == ScrollDirection.Forward);

        _lineEdgeHold.OnMoved();
        return false;
    }

    /// <summary>
    /// Returns true when the camera is effectively at the hard scroll boundary for the
    /// given direction (i.e. there is nowhere left to scroll that way).
    /// </summary>
    private bool IsAtHardEdge(double cameraX, double zoom, double windowWidth, ScrollDirection dir)
    {
        if (_navigableIndices.Count == 0) return false;
        var (blockLeft, blockRight, blockWidthPx) = GetBlockBounds(zoom);

        // If the whole block fits in the window it is centred and cannot scroll at all.
        if (blockWidthPx <= windowWidth) return true;

        const double epsilon = 2.0; // pixels of tolerance
        double maxX = -blockLeft * zoom;          // left boundary (scrolled all the way left)
        double minX = windowWidth - blockRight * zoom; // right boundary (scrolled all the way right)

        return dir == ScrollDirection.Forward
            ? cameraX <= minX + epsilon   // can't scroll further right (content end)
            : cameraX >= maxX - epsilon;  // can't scroll further left (content start)
    }

    /// <summary>
    /// Saccade-style jump: moves camera forward/backward by a percentage of visible width.
    /// When <paramref name="half"/> is true, the jump distance is halved (short jump).
    /// </summary>
    public void Jump(bool forward, double zoom, double windowWidth, double windowHeight,
                     double cameraX, double cameraY, bool half = false)
    {
        if (!CanNavigate) return;

        if (ShouldSuppressAfterEdgeAdvance()) return;

        double jumpPx = windowWidth * (_config.JumpPercentage / 100.0);
        if (half) jumpPx *= 0.5;
        double newX = forward ? cameraX - jumpPx : cameraX + jumpPx;
        newX = ClampX(newX, zoom, windowWidth);

        // Edge-hold advance: if the jump can't move the camera (at boundary),
        // accumulate hold time across repeated key-press events and trigger
        // a line advance when the threshold is reached.
        var dir = forward ? ScrollDirection.Forward : ScrollDirection.Backward;
        if (CheckEdgeHoldAdvance(newX, zoom, windowWidth, dir))
            return; // controller will advance line and snap

        var (_, targetY) = ComputeTargetCamera(zoom, windowWidth, windowHeight);

        _snap = new SnapAnimation
        {
            StartX = cameraX,
            StartY = cameraY,
            TargetX = newX,
            TargetY = targetY,
            Timer = Stopwatch.StartNew(),
            DurationMs = 120, // crisp, fast snap
        };
        StopScroll();
    }

    public void StartSnapToCurrent(double cameraX, double cameraY, double zoom, double windowWidth, double windowHeight)
    {
        if (!CanNavigate) return;
        var (targetX, targetY) = ComputeTargetCamera(zoom, windowWidth, windowHeight);
        BeginSnap(cameraX, cameraY, targetX, targetY);
    }

    /// <summary>
    /// Snap to the right (end) edge of the current line. Used when navigating backward
    /// via edge-hold so the user lands at the end of the previous line and can continue scrolling.
    /// </summary>
    public void StartSnapToCurrentEnd(double cameraX, double cameraY, double zoom, double windowWidth, double windowHeight)
    {
        if (!CanNavigate) return;

        var (blockLeft, blockRight, blockWidthPx) = GetBlockBounds(zoom);
        double targetX;
        if (blockWidthPx <= windowWidth && ShouldCenterBlock())
            targetX = windowWidth / 2.0 - (blockLeft + blockRight) / 2.0 * zoom;
        else
            targetX = windowWidth - blockRight * zoom;
        var (_, targetY) = ComputeTargetCamera(zoom, windowWidth, windowHeight);

        BeginSnap(cameraX, cameraY, SnapX(targetX), SnapY(targetY));
    }

    /// <summary>
    /// Snap to the current line, centering a specific page X coordinate horizontally.
    /// Used for search result navigation so the match is visible rather than
    /// snapping to the block's left edge.
    /// </summary>
    public void StartSnapToPoint(double cameraX, double cameraY, double zoom,
        double windowWidth, double windowHeight, double pageX)
    {
        if (!CanNavigate) return;

        var (_, targetY) = ComputeTargetCamera(zoom, windowWidth, windowHeight);
        double targetX = ClampX(windowWidth / 2.0 - pageX * zoom, zoom, windowWidth);

        BeginSnap(cameraX, cameraY, SnapX(targetX), SnapY(targetY));
    }

    /// <summary>
    /// Computes horizontal scroll fraction (0=line start, 1=line end) for the current
    /// camera position. Used to preserve reading position across zoom changes.
    /// </summary>
    public double ComputeHorizontalFraction(double cameraX, double zoom, double windowWidth)
    {
        if (!CanNavigate) return 0;
        var (blockLeft, blockRight, blockWidthPx) = GetBlockBounds(zoom);
        if (blockWidthPx <= windowWidth) return 0; // block fits in viewport, no scrolling

        double maxX = -blockLeft * zoom;           // camera X at line start (left edge visible)
        double minX = windowWidth - blockRight * zoom; // camera X at line end (right edge visible)
        if (Math.Abs(maxX - minX) < 1) return 0;

        return Math.Clamp((maxX - cameraX) / (maxX - minX), 0, 1);
    }

    /// <summary>
    /// Snap to the current line, preserving horizontal fraction and vertical screen position.
    /// Used after zoom changes to maintain the user's reading position.
    /// </summary>
    public void StartSnapPreservingPosition(double cameraX, double cameraY, double zoom,
        double windowWidth, double windowHeight, double horizontalFraction, double lineScreenY)
    {
        if (!CanNavigate) return;

        var line = CurrentLineInfo;

        // Compute target Y so the line stays at lineScreenY on screen
        // lineScreenY = line.Y * zoom + targetY  →  targetY = lineScreenY - line.Y * zoom
        double targetY = lineScreenY - line.Y * zoom;
        // Update VerticalBias to match this position (so future snaps preserve it)
        double centeredY = windowHeight / 2.0 - line.Y * zoom;
        VerticalBias = targetY - centeredY;

        // Compute target X from horizontal fraction
        var (blockLeft, blockRight, blockWidthPx) = GetBlockBounds(zoom);
        double targetX;
        if (blockWidthPx <= windowWidth)
        {
            // Block fits — use standard positioning
            var (stdX, _) = ComputeTargetCamera(zoom, windowWidth, windowHeight);
            targetX = stdX;
        }
        else
        {
            double maxX = -blockLeft * zoom;
            double minX = windowWidth - blockRight * zoom;
            targetX = maxX - horizontalFraction * (maxX - minX);
        }

        targetX = ClampX(targetX, zoom, windowWidth);
        BeginSnap(cameraX, cameraY, SnapX(targetX), SnapY(targetY));
    }

    private void BeginSnap(double startX, double startY, double targetX, double targetY)
    {
        _snap = new SnapAnimation
        {
            StartX = startX, StartY = startY,
            TargetX = targetX, TargetY = targetY,
            Timer = Stopwatch.StartNew(),
            DurationMs = _config.SnapDurationMs,
        };
    }

    private double SnapX(double x) => _config.PixelSnapping ? Math.Round(x * 4.0) / 4.0 : x;
    private double SnapY(double y) => _config.PixelSnapping ? Math.Round(y) : y;

    private (double X, double Y) ComputeTargetCamera(double zoom, double windowWidth, double windowHeight)
    {
        var block = CurrentNavigableBlock;
        var line = CurrentLineInfo;
        double targetY = windowHeight / 2.0 - line.Y * zoom + VerticalBias;

        double blockWidthPx = block.BBox.W * zoom;
        double targetX;
        if (blockWidthPx < windowWidth * CenterBlockThreshold && ShouldCenterBlock())
        {
            // Block is narrow relative to viewport — center it horizontally
            double blockCenterX = block.BBox.X + block.BBox.W / 2.0;
            targetX = windowWidth / 2.0 - blockCenterX * zoom;
        }
        else
        {
            // Block fills most of the viewport — left-align with 5% margin
            targetX = windowWidth * 0.05 - block.BBox.X * zoom;
        }

        if (_config.PixelSnapping)
        {
            targetY = Math.Round(targetY);               // integer Y = baseline on pixel grid
            targetX = Math.Round(targetX * 4.0) / 4.0;   // 1/4 pixel X for smooth feel
        }

        return (targetX, targetY);
    }

    /// <summary>
    /// Captures the vertical bias from the current camera position relative to
    /// where the current line's center-aligned position would be.
    /// Call this when the user manually pans while in rail mode.
    /// </summary>
    public void CaptureVerticalBias(double cameraY, double zoom, double windowHeight)
    {
        if (!CanNavigate) return;
        var line = CurrentLineInfo;
        double centeredY = windowHeight / 2.0 - line.Y * zoom;
        VerticalBias = cameraY - centeredY;
    }

    public double? ComputeLineStartX(double zoom, double windowWidth)
        => ComputeLineEdgeX(zoom, windowWidth, start: true);

    public double? ComputeLineEndX(double zoom, double windowWidth)
        => ComputeLineEdgeX(zoom, windowWidth, start: false);

    private double? ComputeLineEdgeX(double zoom, double windowWidth, bool start)
    {
        if (!CanNavigate) return null;
        var block = CurrentNavigableBlock;
        double x = start
            ? windowWidth * 0.05 - block.BBox.X * zoom
            : windowWidth * 0.95 - (block.BBox.X + block.BBox.W) * zoom;
        _snap = null;
        return ClampX(x, zoom, windowWidth);
    }

    private double ClampX(double cameraX, double zoom, double windowWidth)
    {
        if (_navigableIndices.Count == 0) return cameraX;

        var (blockLeft, blockRight, blockWidthPx) = GetBlockBounds(zoom);

        double result;
        if (blockWidthPx <= windowWidth)
        {
            if (ShouldCenterBlock())
            {
                double center = (blockLeft + blockRight) / 2.0;
                result = windowWidth / 2.0 - center * zoom;
            }
            else
            {
                // Left-align with 5% margin (block fully visible, no scroll needed)
                result = windowWidth * 0.05 - blockLeft * zoom;
            }
        }
        else
        {
            double maxX = -blockLeft * zoom;
            double minX = windowWidth - blockRight * zoom;

            // Soft clamp: ease into the boundary using an asymptotic curve
            // instead of a hard stop, which eliminates visual judder.
            // SoftEase(over) = over * k / (k + over) — approaches k as over → ∞.
            const double k = 20.0; // pixels of easing zone
            if (cameraX > maxX)
                result = maxX + SoftEase(cameraX - maxX, k);
            else if (cameraX < minX)
                result = minX - SoftEase(minX - cameraX, k);
            else
                result = cameraX;
        }

        return _config.PixelSnapping ? Math.Round(result * 4.0) / 4.0 : result;
    }

    private (double Left, double Right, double WidthPx) GetBlockBounds(double zoom)
    {
        var block = CurrentNavigableBlock;
        double margin = block.BBox.W * 0.05;
        double left = block.BBox.X - margin;
        double right = block.BBox.X + block.BBox.W + margin;
        return (left, right, (right - left) * zoom);
    }

    /// <summary>Asymptotic ease: approaches <paramref name="limit"/> as overshoot grows.</summary>
    private static double SoftEase(double overshoot, double limit)
        => overshoot * limit / (limit + overshoot);

    public bool Tick(ref double cameraX, ref double cameraY, double dtSecs, double zoom, double windowWidth)
    {
        bool animating = TickSnapAnimation(ref cameraX, ref cameraY);

        if (TickScrollHold(ref cameraX, zoom, windowWidth))
            animating = true;

        return animating;
    }

    private bool TickSnapAnimation(ref double cameraX, ref double cameraY)
    {
        if (_snap is not { } snap)
            return false;

        double elapsed = snap.Timer.Elapsed.TotalMilliseconds;
        double t = Math.Min(elapsed / snap.DurationMs, 1.0);
        double eased = 1.0 - Math.Pow(1.0 - t, 3); // cubic ease-out

        cameraX = snap.StartX + (snap.TargetX - snap.StartX) * eased;
        cameraY = snap.StartY + (snap.TargetY - snap.StartY) * eased;

        if (t >= 1.0)
        {
            _snap = null;
            return false;
        }
        return true;
    }

    private bool TickScrollHold(ref double cameraX, double zoom, double windowWidth)
    {
        if (_scroll.Direction is not { } dir)
        {
            ScrollSpeed = 0.0;
            return false;
        }

        double ramp = _config.ScrollRampTime;
        double sStart = _config.ScrollSpeedStart;
        double sMax = _config.ScrollSpeedMax;

        double totalDisplacement = _scroll.ComputeDisplacement(sStart, sMax, ramp);
        double holdSecs = _scroll.ElapsedSecs;

        double instantSpeed = holdSecs <= ramp
            ? sStart + (sMax - sStart) * (holdSecs / ramp) * (holdSecs / ramp)
            : sMax;
        ScrollSpeed = sMax > 0 ? instantSpeed / sMax : 0.0;

        double sign = dir == ScrollDirection.Forward ? -1.0 : 1.0;
        cameraX = ClampX(_scroll.StartX + sign * totalDisplacement * zoom, zoom, windowWidth);

        // Auto-scroll trigger: if holding forward scroll for longer than the
        // configured delay, transition to auto-scroll mode.
        if (_config.AutoScrollTriggerEnabled
            && dir == ScrollDirection.Forward
            && _scroll.ElapsedMs >= _config.AutoScrollTriggerDelayMs)
        {
            StopScrollAndEdgeHold();
            StartAutoScroll(_config.DefaultAutoScrollSpeed);
            _autoScrollTriggered = true;
            return true;
        }

        // Edge-hold advance: if the camera is pinned against the line boundary,
        // accumulate hold time and trigger a line advance when the threshold is reached.
        if (CheckEdgeHoldAdvance(cameraX, zoom, windowWidth, dir))
            StopScroll(); // clear scroll only; output signals are consumed by the controller

        return true;
    }

    public void JumpToEnd()
    {
        if (_navigableIndices.Count == 0) return;
        CurrentBlock = _navigableIndices.Count - 1;
        CurrentLine = CurrentNavigableBlock.Lines.Count - 1;
    }

    public LayoutBlock CurrentNavigableBlock
    {
        get
        {
            int idx = Math.Min(CurrentBlock, _navigableIndices.Count - 1);
            return _analysis!.Blocks[_navigableIndices[idx]];
        }
    }

    public LineInfo CurrentLineInfo
    {
        get
        {
            var block = CurrentNavigableBlock;
            return block.Lines[Math.Min(CurrentLine, block.Lines.Count - 1)];
        }
    }

    public int? FindBlockAtPoint(double pageX, double pageY)
    {
        if (_analysis is null) return null;
        for (int i = 0; i < _navigableIndices.Count; i++)
        {
            var b = _analysis.Blocks[_navigableIndices[i]].BBox;
            if (pageX >= b.X && pageX <= b.X + b.W && pageY >= b.Y && pageY <= b.Y + b.H)
                return i;
        }
        return null;
    }

    /// <summary>
    /// Starts auto-scroll at the given speed (page-coordinate pixels/sec).
    /// </summary>
    public void StartAutoScroll(double speed)
    {
        if (!CanNavigate) return;
        _autoScrollState.Start(speed);
        StopScrollAndEdgeHold();
    }

    public void StopAutoScroll()
    {
        _autoScrollState.Stop();
        ScrollSpeed = 0.0;
    }

    /// <summary>Inject a settling pause into auto-scroll (e.g. after advancing to a new block).
    /// The pause is deferred until any snap animation completes, so the full duration
    /// is perceived as stillness after the camera reaches its target.</summary>
    public void PauseAutoScroll(double durationMs)
    {
        _autoScrollState.RequestDeferredPause(durationMs);
    }

    /// <summary>Set/clear the boost flag (user holding D/Right during auto-scroll).</summary>
    public void SetAutoScrollBoost(bool boost) => _autoScrollState.SetBoost(boost);

    /// <summary>
    /// Inject a controlled elapsed-seconds source for unit tests.
    /// Forwarded to the underlying <see cref="AutoScrollStateMachine"/>.
    /// </summary>
    internal Func<double>? AutoScrollElapsedSecondsOverride
    {
        set => _autoScrollState.GetScrollElapsedSeconds = value;
    }

    /// <summary>
    /// Returns true if auto-scroll has reached the right edge and should advance.
    /// Called from Tick; the caller is responsible for calling NextLine and snapping.
    /// </summary>
    public bool TickAutoScroll(ref double cameraX, double dtSecs, double zoom, double windowWidth)
    {
        if (!_autoScrollState.IsActive || _navigableIndices.Count == 0) return false;

        var (_, blockRight, _) = GetBlockBounds(zoom);
        var block = CurrentNavigableBlock;

        var ctx = new AutoScrollContext
        {
            SnapInProgress = _snap is not null,
            BlockRight = blockRight,
            RawBlockWidthPx = block.BBox.W * zoom,
            CurrentLine = CurrentLine,
            BlockLineCount = block.Lines.Count,
            LinePauseMs = _config.AutoScrollLinePauseMs,
            WindowWidth = windowWidth,
            Zoom = zoom,
            MaxSpeed = _config.ScrollSpeedMax,
        };

        bool reachedEnd = _autoScrollState.Tick(ref cameraX, dtSecs, in ctx);
        ScrollSpeed = _autoScrollState.NormalizedSpeed;
        return reachedEnd;
    }

    public void UpdateConfig(AppConfig config)
    {
        _config = config;
        // If autoscroll is running, apply the updated speed immediately so
        // [ / ] key adjustments take effect without stopping and restarting.
        _autoScrollState.UpdateSpeed(config.DefaultAutoScrollSpeed);
    }

    private sealed class SnapAnimation
    {
        public double StartX, StartY, TargetX, TargetY;
        public required Stopwatch Timer;
        public double DurationMs;
    }
}
