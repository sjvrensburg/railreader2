using System.Diagnostics;
using RailReader.Core.Models;

namespace RailReader.Core.Services;

public sealed class RailNav
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
    private ScrollDirection? _scrollDir;
    private Stopwatch? _scrollHoldTimer;
    private double _scrollStartX;
    private double _scrollSeedSecs; // virtual time offset so first frame has visible displacement

    // Auto-scroll state: continuous forward scroll along the line then advance
    public bool AutoScrolling { get; private set; }
    private double _autoScrollSpeed; // pixels/sec in page coordinates
    private bool _autoScrollBoost;   // true while user holds D/Right during auto-scroll
    private Stopwatch? _autoScrollPauseTimer;
    private double _autoScrollPauseDurationMs;
    private bool _autoScrollPauseAdvances; // true = end-of-line pause that triggers advance
    private double _autoScrollPendingPauseMs; // deferred pause that starts after snap completes
    private bool _autoScrollDwelt; // true after dwell pause on a block that fits in viewport

    /// <summary>
    /// Set to true when sustained horizontal scroll triggers auto-scroll.
    /// The controller reads and clears this flag to transition state.
    /// </summary>
    public bool AutoScrollTriggered { get; set; }

    // Edge-hold advance: when the user holds D/Right (or A/Left) against the line boundary
    // for EdgeAdvanceHoldMs, trigger a NextLine/PrevLine as if they had pressed Down/Up.
    private Stopwatch? _edgeHoldTimer;
    private ScrollDirection? _edgeHoldDir;
    private ScrollDirection? _pendingEdgeAdvance;
    private bool _edgeAdvanceJustFired; // suppresses Jump()/StartScroll() so the snap-to-start/end isn't overwritten
    private const double EdgeAdvanceHoldMs = 400.0;

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
        if (!_edgeAdvanceJustFired) return false;
        if (_snap is not null) return true; // snap still running
        _edgeAdvanceJustFired = false;
        return false;
    }

    public RailNav(AppConfig config) => _config = config;

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
            _scrollDir = null;
            _scrollHoldTimer = null;
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
            _scrollDir = null;
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

        if (_scrollDir != dir)
        {
            _scrollDir = dir;
            _scrollHoldTimer = Stopwatch.StartNew();
            // If a snap is in progress, jump to its target and start scrolling from there.
            // This avoids a stale _scrollStartX when key-repeat fires during a line-advance snap
            // (e.g. edge-hold advance), which would otherwise produce a displaced starting position.
            if (_snap is { } activeSnap)
            {
                _scrollStartX = activeSnap.TargetX;
                _snap = null;
            }
            else
            {
                _scrollStartX = currentCameraX;
            }
            // Seed one frame ahead (~16ms) so the first Tick() produces
            // visible displacement instead of near-zero movement.
            _scrollSeedSecs = 1.0 / 60.0;
        }
    }

    public void StopScroll()
    {
        _scrollDir = null;
        _scrollHoldTimer = null;
        _scrollSeedSecs = 0;
        ScrollSpeed = 0.0;
    }

    /// <summary>Clears both scroll and edge-hold state (e.g. on key release or mode change).</summary>
    public void StopScrollAndEdgeHold()
    {
        StopScroll();
        _edgeHoldTimer = null;
        _edgeHoldDir = null;
    }

    /// <summary>
    /// Returns the direction of a pending edge-advance (triggered by holding D/Right or A/Left
    /// against the line boundary) and clears it. Returns null if none pending.
    /// </summary>
    public ScrollDirection? ConsumePendingEdgeAdvance()
    {
        var result = _pendingEdgeAdvance;
        _pendingEdgeAdvance = null;
        return result;
    }

    /// <summary>
    /// Checks whether the camera is at the hard edge for the given direction and, if so,
    /// accumulates hold time. Returns true when the hold threshold is reached and
    /// an edge advance has been triggered (sets <see cref="_pendingEdgeAdvance"/> and
    /// <see cref="_edgeAdvanceJustFired"/>). The caller is responsible for its own
    /// cleanup (e.g. clearing edge-hold state or stopping scroll).
    /// </summary>
    private bool CheckEdgeHoldAdvance(double cameraX, double zoom, double windowWidth, ScrollDirection dir)
    {
        if (IsAtHardEdge(cameraX, zoom, windowWidth, dir))
        {
            if (_edgeHoldDir != dir)
            {
                _edgeHoldTimer = Stopwatch.StartNew();
                _edgeHoldDir = dir;
            }
            else if (_edgeHoldTimer is not null
                && _edgeHoldTimer.Elapsed.TotalMilliseconds >= EdgeAdvanceHoldMs)
            {
                _pendingEdgeAdvance = dir;
                _edgeAdvanceJustFired = true;
                return true;
            }
        }
        else
        {
            _edgeHoldTimer = null;
            _edgeHoldDir = null;
        }
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
        {
            _edgeHoldTimer = null;
            _edgeHoldDir = null;
            return; // controller will advance line and snap
        }

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
        if (_scrollDir is not { } dir || _scrollHoldTimer is null)
        {
            ScrollSpeed = 0.0;
            return false;
        }

        // Compute total displacement from absolute elapsed time (integral of speed curve).
        // This eliminates frame-rate dependent jitter from variable dt.
        // speed(t) = start + (max - start) * (t/ramp)^2
        // integral: start*T + (max-start) * T^3 / (3*ramp^2)  for T <= ramp
        double holdSecs = _scrollHoldTimer.Elapsed.TotalSeconds + _scrollSeedSecs;
        double ramp = _config.ScrollRampTime;
        double sStart = _config.ScrollSpeedStart;
        double sMax = _config.ScrollSpeedMax;

        double totalDisplacement;
        if (holdSecs <= ramp)
        {
            totalDisplacement = sStart * holdSecs
                + (sMax - sStart) * holdSecs * holdSecs * holdSecs / (3.0 * ramp * ramp);
        }
        else
        {
            // Integral up to ramp + constant max speed after ramp
            double rampDisplacement = sStart * ramp + (sMax - sStart) * ramp / 3.0;
            totalDisplacement = rampDisplacement + sMax * (holdSecs - ramp);
        }

        double instantSpeed = holdSecs <= ramp
            ? sStart + (sMax - sStart) * (holdSecs / ramp) * (holdSecs / ramp)
            : sMax;
        ScrollSpeed = sMax > 0 ? instantSpeed / sMax : 0.0;

        double sign = dir == ScrollDirection.Forward ? -1.0 : 1.0;
        // Use rail zoom threshold as reference so screen-space speed is constant
        // regardless of current zoom (matches auto-scroll behaviour).
        cameraX = ClampX(_scrollStartX + sign * totalDisplacement * _config.RailZoomThreshold, zoom, windowWidth);

        // Auto-scroll trigger: if holding forward scroll for longer than the
        // configured delay, transition to auto-scroll mode.
        if (_config.AutoScrollTriggerEnabled
            && dir == ScrollDirection.Forward
            && _scrollHoldTimer.Elapsed.TotalMilliseconds >= _config.AutoScrollTriggerDelayMs)
        {
            StopScrollAndEdgeHold();
            StartAutoScroll(_config.DefaultAutoScrollSpeed);
            AutoScrollTriggered = true;
            return true;
        }

        // Edge-hold advance: if the camera is pinned against the line boundary,
        // accumulate hold time and trigger a line advance when the threshold is reached.
        if (CheckEdgeHoldAdvance(cameraX, zoom, windowWidth, dir))
        {
            StopScrollAndEdgeHold(); // clear scroll state; key-repeat will restart it on the new line
        }

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
        AutoScrolling = true;
        _autoScrollSpeed = speed;
        _autoScrollBoost = false;
        _autoScrollPauseTimer = null;
        _autoScrollPendingPauseMs = 0;
        _autoScrollDwelt = false;
        // Stop any manual scroll
        StopScrollAndEdgeHold();
    }

    public void StopAutoScroll()
    {
        AutoScrolling = false;
        _autoScrollSpeed = 0;
        _autoScrollBoost = false;
        _autoScrollPauseTimer = null;
        _autoScrollPendingPauseMs = 0;
        _autoScrollDwelt = false;
        ScrollSpeed = 0.0;
    }

    /// <summary>Inject a settling pause into auto-scroll (e.g. after advancing to a new line).
    /// The pause is deferred until any snap animation completes, so the full duration
    /// is perceived as stillness after the camera reaches its target.</summary>
    public void PauseAutoScroll(double durationMs)
    {
        if (!AutoScrolling || durationMs <= 0) return;
        _autoScrollPendingPauseMs = durationMs;
        _autoScrollDwelt = false; // reset for the new block
    }

    /// <summary>Set/clear the boost flag (user holding D/Right during auto-scroll).</summary>
    public void SetAutoScrollBoost(bool boost) => _autoScrollBoost = boost;

    /// <summary>
    /// Returns true if auto-scroll has reached the right edge and should advance.
    /// Called from Tick; the caller is responsible for calling NextLine and snapping.
    /// </summary>
    public bool TickAutoScroll(ref double cameraX, double dtSecs, double zoom, double windowWidth)
    {
        if (!AutoScrolling || _navigableIndices.Count == 0) return false;

        // Activate deferred pause once the snap animation has finished
        if (_autoScrollPendingPauseMs > 0)
        {
            if (_snap is null)
            {
                _autoScrollPauseTimer = Stopwatch.StartNew();
                _autoScrollPauseDurationMs = _autoScrollPendingPauseMs;
                _autoScrollPauseAdvances = false;
                _autoScrollPendingPauseMs = 0;
                ScrollSpeed = 0.0;
            }
            else
            {
                return false; // snap still running — don't scroll until pause activates
            }
        }

        // Pause: hold position, count down
        if (_autoScrollPauseTimer is not null)
        {
            ScrollSpeed = 0.0;
            if (_autoScrollPauseTimer.Elapsed.TotalMilliseconds >= _autoScrollPauseDurationMs)
            {
                bool advance = _autoScrollPauseAdvances;
                _autoScrollPauseTimer = null;
                return advance;
            }
            return false; // still pausing
        }

        double speed = _autoScrollBoost ? _autoScrollSpeed * 2.0 : _autoScrollSpeed;
        // Expose normalized speed (0–1) so motion blur can react to auto-scroll.
        double maxSpeed = _config.ScrollSpeedMax;
        ScrollSpeed = maxSpeed > 0 ? Math.Clamp(speed / maxSpeed, 0.0, 1.0) : 0.0;
        // Move camera left (negative X) to scroll content right.
        // Use the rail zoom threshold as reference so screen-space speed is
        // constant regardless of current zoom (avoids text rushing at high mag).
        cameraX -= speed * _config.RailZoomThreshold * dtSecs;
        cameraX = ClampX(cameraX, zoom, windowWidth);

        // Check if we've reached the right edge of the block
        var (_, blockRight, blockWidthPx) = GetBlockBounds(zoom);
        double visibleRight = (-cameraX + windowWidth) / zoom;

        if (visibleRight >= blockRight)
        {
            // When the block fits entirely in the viewport there is no scroll
            // distance, so without a dwell pause the block would be skipped
            // instantly after the entry pause. Add a per-line dwell pause so
            // narrow blocks (equations, headings) get proportional viewing time.
            bool fitsInViewport = blockWidthPx <= windowWidth;
            if (fitsInViewport && !_autoScrollDwelt && _config.AutoScrollLinePauseMs > 0)
            {
                _autoScrollDwelt = true;
                _autoScrollPauseTimer = Stopwatch.StartNew();
                _autoScrollPauseDurationMs = _config.AutoScrollLinePauseMs * CurrentNavigableBlock.Lines.Count;
                _autoScrollPauseAdvances = true;
                return false; // dwell pause
            }

            bool isBlockEnd = CurrentLine + 1 >= CurrentNavigableBlock.Lines.Count;
            // Block-end pauses are handled by the controller (which knows the
            // destination block type).  Only pause here for mid-block line ends.
            if (!isBlockEnd && _config.AutoScrollLinePauseMs > 0)
            {
                _autoScrollDwelt = false; // reset for the next line
                _autoScrollPauseTimer = Stopwatch.StartNew();
                _autoScrollPauseDurationMs = _config.AutoScrollLinePauseMs;
                _autoScrollPauseAdvances = true;
                return false; // start pause
            }
            return true; // advance immediately (block end or no pause)
        }
        return false;
    }

    public void UpdateConfig(AppConfig config)
    {
        _config = config;
        // If autoscroll is running, apply the updated speed immediately so
        // [ / ] key adjustments take effect without stopping and restarting.
        if (AutoScrolling)
            _autoScrollSpeed = config.DefaultAutoScrollSpeed;
    }

    private sealed class SnapAnimation
    {
        public double StartX, StartY, TargetX, TargetY;
        public required Stopwatch Timer;
        public double DurationMs;
    }
}
