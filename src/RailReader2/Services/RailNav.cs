using System.Diagnostics;
using RailReader2.Models;

namespace RailReader2.Services;

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

    public int CurrentLineCount =>
        _navigableIndices.Count == 0 ? 0 : CurrentNavigableBlock.Lines.Count;

    public void UpdateZoom(double zoom, double cameraX, double cameraY, double windowWidth, double windowHeight)
    {
        bool shouldBeActive = zoom >= _config.RailZoomThreshold && HasAnalysis;

        if (shouldBeActive && !Active)
        {
            Active = true;
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

    public NavResult NextLine()
    {
        if (!Active || _navigableIndices.Count == 0) return NavResult.Ok;

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
        if (!Active || _navigableIndices.Count == 0) return NavResult.Ok;

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
        if (!Active || _navigableIndices.Count == 0) return;
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

    public void StartSnapToCurrent(double cameraX, double cameraY, double zoom, double windowWidth, double windowHeight)
    {
        if (!Active || _navigableIndices.Count == 0) return;

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
        return (targetX, targetY);
    }

    /// <summary>
    /// Captures the vertical bias from the current camera position relative to
    /// where the current line's center-aligned position would be.
    /// Call this when the user manually pans while in rail mode.
    /// </summary>
    public void CaptureVerticalBias(double cameraY, double zoom, double windowHeight)
    {
        if (!Active || _navigableIndices.Count == 0) return;
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
        if (!Active || _navigableIndices.Count == 0) return null;
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

        if (blockWidthPx <= windowWidth)
        {
            double center = (blockLeft + blockRight) / 2.0;
            return windowWidth / 2.0 - center * zoom;
        }

        double maxX = -blockLeft * zoom;
        double minX = windowWidth - blockRight * zoom;

        // Soft clamp: ease into the boundary using an asymptotic curve
        // instead of a hard stop, which eliminates visual judder.
        // SoftEase(over) = over * k / (k + over) — approaches k as over → ∞.
        const double k = 20.0; // pixels of easing zone
        if (cameraX > maxX)
            return maxX + SoftEase(cameraX - maxX, k);
        if (cameraX < minX)
            return minX - SoftEase(minX - cameraX, k);
        return cameraX;
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
        if (!Active || _navigableIndices.Count == 0) return;
        AutoScrolling = true;
        _autoScrollSpeed = speed;
        _autoScrollBoost = false;
        // Stop any manual scroll
        StopScroll();
    }

    public void StopAutoScroll()
    {
        AutoScrolling = false;
        _autoScrollSpeed = 0;
        _autoScrollBoost = false;
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

        double speed = _autoScrollBoost ? _autoScrollSpeed * 2.0 : _autoScrollSpeed;
        // Move camera left (negative X) to scroll content right
        cameraX -= speed * zoom * dtSecs;
        cameraX = ClampX(cameraX, zoom, windowWidth);

        // Check if we've reached the right edge of the block
        var block = CurrentNavigableBlock;
        double blockRight = block.BBox.X + block.BBox.W + block.BBox.W * 0.05;
        double visibleRight = (-cameraX + windowWidth) / zoom;

        return visibleRight >= blockRight;
    }

    public void UpdateConfig(AppConfig config) => _config = config;

    private sealed class SnapAnimation
    {
        public double StartX, StartY, TargetX, TargetY;
        public required Stopwatch Timer;
        public double DurationMs;
    }
}
