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

    public void FindNearestBlock(double cameraX, double cameraY, double zoom, double windowWidth, double windowHeight)
    {
        if (_analysis is null) return;

        double centerX = (windowWidth / 2.0 - cameraX) / zoom;
        double centerY = (windowHeight / 2.0 - cameraY) / zoom;

        double bestDist = double.MaxValue;
        int bestIdx = 0;

        for (int i = 0; i < _navigableIndices.Count; i++)
        {
            var block = _analysis.Blocks[_navigableIndices[i]];
            double bx = block.BBox.X + block.BBox.W / 2.0;
            double by = block.BBox.Y + block.BBox.H / 2.0;
            double dist = (bx - centerX) * (bx - centerX) + (by - centerY) * (by - centerY);
            if (dist < bestDist) { bestDist = dist; bestIdx = i; }
        }

        CurrentBlock = bestIdx;
        CurrentLine = 0;
    }

    /// <summary>
    /// Finds the navigable block nearest to a point in page coordinates.
    /// Tries a direct bounding-box hit first; falls back to nearest-center distance.
    /// </summary>
    public void FindBlockNearPoint(double pageX, double pageY)
    {
        if (_analysis is null || _navigableIndices.Count == 0) return;

        // Try direct hit-test first
        int? hit = FindBlockAtPoint(pageX, pageY);
        if (hit.HasValue)
        {
            CurrentBlock = hit.Value;
            CurrentLine = FindNearestLine(CurrentNavigableBlock, pageY);
            return;
        }

        // Fall back to nearest block center
        double bestDist = double.MaxValue;
        int bestIdx = 0;
        for (int i = 0; i < _navigableIndices.Count; i++)
        {
            var block = _analysis.Blocks[_navigableIndices[i]];
            double bx = block.BBox.X + block.BBox.W / 2.0;
            double by = block.BBox.Y + block.BBox.H / 2.0;
            double dist = (bx - pageX) * (bx - pageX) + (by - pageY) * (by - pageY);
            if (dist < bestDist) { bestDist = dist; bestIdx = i; }
        }
        CurrentBlock = bestIdx;
        CurrentLine = FindNearestLine(CurrentNavigableBlock, pageY);
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
        if (_scrollDir != dir)
        {
            _scrollDir = dir;
            _scrollHoldTimer = Stopwatch.StartNew();
            _scrollStartX = currentCameraX;
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

    /// <summary>
    /// Saccade-style jump: moves camera forward/backward by a percentage of visible width.
    /// When <paramref name="half"/> is true, the jump distance is halved (short jump).
    /// </summary>
    public void Jump(bool forward, double zoom, double windowWidth, double windowHeight,
                     double cameraX, double cameraY, bool half = false)
    {
        if (!CanNavigate) return;

        double jumpPx = windowWidth * (_config.JumpPercentage / 100.0);
        if (half) jumpPx *= 0.5;
        double newX = forward ? cameraX - jumpPx : cameraX + jumpPx;
        newX = ClampX(newX, zoom, windowWidth);

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
        _snap = new SnapAnimation
        {
            StartX = cameraX,
            StartY = cameraY,
            TargetX = targetX,
            TargetY = targetY,
            Timer = Stopwatch.StartNew(),
            DurationMs = _config.SnapDurationMs,
        };
    }

    private (double X, double Y) ComputeTargetCamera(double zoom, double windowWidth, double windowHeight)
    {
        var block = CurrentNavigableBlock;
        var line = CurrentLineInfo;
        double targetY = windowHeight / 2.0 - line.Y * zoom + VerticalBias;
        double targetX = windowWidth * 0.05 - block.BBox.X * zoom;

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

        var block = CurrentNavigableBlock;
        double margin = block.BBox.W * 0.05;
        double blockLeft = block.BBox.X - margin;
        double blockRight = block.BBox.X + block.BBox.W + margin;
        double blockWidthPx = (blockRight - blockLeft) * zoom;

        double result;
        if (blockWidthPx <= windowWidth)
        {
            double center = (blockLeft + blockRight) / 2.0;
            result = windowWidth / 2.0 - center * zoom;
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

    /// <summary>Asymptotic ease: approaches <paramref name="limit"/> as overshoot grows.</summary>
    private static double SoftEase(double overshoot, double limit)
        => overshoot * limit / (limit + overshoot);

    public bool Tick(ref double cameraX, ref double cameraY, double dtSecs, double zoom, double windowWidth)
    {
        bool animating = false;

        if (_snap is { } snap)
        {
            double elapsed = snap.Timer.Elapsed.TotalMilliseconds;
            double t = Math.Min(elapsed / snap.DurationMs, 1.0);
            double eased = 1.0 - Math.Pow(1.0 - t, 3); // cubic ease-out

            cameraX = snap.StartX + (snap.TargetX - snap.StartX) * eased;
            cameraY = snap.StartY + (snap.TargetY - snap.StartY) * eased;

            if (t >= 1.0)
                _snap = null;
            else
                animating = true;
        }

        if (_scrollDir is { } dir && _scrollHoldTimer is not null)
        {
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
            cameraX = ClampX(_scrollStartX + sign * totalDisplacement * zoom, zoom, windowWidth);
            animating = true;
        }
        else
        {
            ScrollSpeed = 0.0;
        }

        return animating;
    }

    public void JumpToEnd()
    {
        if (_navigableIndices.Count == 0) return;
        CurrentBlock = _navigableIndices.Count - 1;
        CurrentLine = CurrentNavigableBlock.Lines.Count - 1;
    }

    public LayoutBlock CurrentNavigableBlock =>
        _analysis!.Blocks[_navigableIndices[CurrentBlock]];

    public LineInfo CurrentLineInfo =>
        CurrentNavigableBlock.Lines[Math.Min(CurrentLine, CurrentNavigableBlock.Lines.Count - 1)];

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
        // Stop any manual scroll
        StopScroll();
    }

    public void StopAutoScroll()
    {
        AutoScrolling = false;
        _autoScrollSpeed = 0;
        _autoScrollBoost = false;
        _autoScrollPauseTimer = null;
        _autoScrollPendingPauseMs = 0;
    }

    /// <summary>Inject a settling pause into auto-scroll (e.g. after advancing to a new line).
    /// The pause is deferred until any snap animation completes, so the full duration
    /// is perceived as stillness after the camera reaches its target.</summary>
    public void PauseAutoScroll(double durationMs)
    {
        if (!AutoScrolling || durationMs <= 0) return;
        _autoScrollPendingPauseMs = durationMs;
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
        if (_autoScrollPendingPauseMs > 0 && _snap is null)
        {
            _autoScrollPauseTimer = Stopwatch.StartNew();
            _autoScrollPauseDurationMs = _autoScrollPendingPauseMs;
            _autoScrollPauseAdvances = false;
            _autoScrollPendingPauseMs = 0;
        }

        // Pause: hold position, count down
        if (_autoScrollPauseTimer is not null)
        {
            if (_autoScrollPauseTimer.Elapsed.TotalMilliseconds >= _autoScrollPauseDurationMs)
            {
                bool advance = _autoScrollPauseAdvances;
                _autoScrollPauseTimer = null;
                return advance;
            }
            return false; // still pausing
        }

        double speed = _autoScrollBoost ? _autoScrollSpeed * 2.0 : _autoScrollSpeed;
        // Move camera left (negative X) to scroll content right
        cameraX -= speed * zoom * dtSecs;
        cameraX = ClampX(cameraX, zoom, windowWidth);

        // Check if we've reached the right edge of the block
        var block = CurrentNavigableBlock;
        double blockRight = block.BBox.X + block.BBox.W + block.BBox.W * 0.05;
        double visibleRight = (-cameraX + windowWidth) / zoom;

        if (visibleRight >= blockRight)
        {
            // Determine pause: longer for block/page boundaries
            bool isBlockEnd = CurrentLine + 1 >= block.Lines.Count;
            double pauseMs = isBlockEnd ? _config.AutoScrollBlockPauseMs : _config.AutoScrollLinePauseMs;

            if (pauseMs > 0)
            {
                _autoScrollPauseTimer = Stopwatch.StartNew();
                _autoScrollPauseDurationMs = pauseMs;
                _autoScrollPauseAdvances = true;
                return false; // start pause
            }
            return true; // no pause, advance immediately
        }
        return false;
    }

    public void UpdateConfig(AppConfig config) => _config = config;

    private sealed class SnapAnimation
    {
        public double StartX, StartY, TargetX, TargetY;
        public required Stopwatch Timer;
        public double DurationMs;
    }
}
