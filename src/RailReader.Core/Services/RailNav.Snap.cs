using System.Diagnostics;

namespace RailReader.Core.Services;

public sealed partial class RailNav
{
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

        BeginSnap(cameraX, cameraY, SnapX(targetX, zoom), SnapY(targetY));
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

        BeginSnap(cameraX, cameraY, SnapX(targetX, zoom), SnapY(targetY));
    }

    /// <summary>
    /// Computes horizontal scroll fraction (0=line start, 1=line end) for the current
    /// camera position. Used to preserve reading position across zoom changes.
    /// </summary>
    public double ComputeHorizontalFraction(double cameraX, double zoom, double windowWidth)
    {
        if (!CanNavigate) return 0;
        var (blockLeft, blockRight, blockWidthPx) = GetBlockBounds(zoom);
        if (blockWidthPx <= windowWidth) return 0;

        double maxX = -blockLeft * zoom;
        double minX = windowWidth - blockRight * zoom;
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

        double targetY = lineScreenY - line.Y * zoom;
        double centeredY = windowHeight / 2.0 - line.Y * zoom;
        VerticalBias = targetY - centeredY;

        var (blockLeft, blockRight, blockWidthPx) = GetBlockBounds(zoom);
        double targetX;
        if (blockWidthPx <= windowWidth)
        {
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
        BeginSnap(cameraX, cameraY, SnapX(targetX, zoom), SnapY(targetY));
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

    /// <summary>
    /// Snap X to a grid that guarantees at least 1 screen pixel of precision.
    /// At high zoom the grid is finer (smooth feel); at low zoom it coarsens
    /// to prevent sub-pixel text shimmer. Internal so the scroll/auto-scroll
    /// ticks (in sibling partials) can apply the same snapping every frame.
    /// </summary>
    internal double SnapX(double x, double zoom)
    {
        if (!_config.PixelSnapping) return x;
        double grid = Math.Max(4.0, zoom);
        return Math.Round(x * grid) / grid;
    }

    private double SnapY(double y) => _config.PixelSnapping ? Math.Round(y) : y;

    private (double X, double Y) ComputeTargetCamera(double zoom, double windowWidth, double windowHeight)
    {
        var block = CurrentNavigableBlock;
        var line = CurrentLineInfo;
        double targetY = windowHeight / 2.0 - line.Y * zoom + VerticalBias;

        double blockWidthPx = block.BBox.W * zoom;
        double targetX;
        if (blockWidthPx < windowWidth * CoreTuning.CenterBlockThreshold && ShouldCenterBlock())
        {
            double blockCenterX = block.BBox.X + block.BBox.W / 2.0;
            targetX = windowWidth / 2.0 - blockCenterX * zoom;
        }
        else
        {
            targetX = windowWidth * 0.05 - block.BBox.X * zoom;
        }

        if (_config.PixelSnapping)
        {
            targetY = Math.Round(targetY);
            targetX = SnapX(targetX, zoom);
        }

        return (targetX, targetY);
    }

    private bool TickSnapAnimation(ref double cameraX, ref double cameraY)
    {
        if (_snap is not { } snap)
            return false;

        double elapsed = snap.Timer.Elapsed.TotalMilliseconds;
        double t = Math.Min(elapsed / snap.DurationMs, 1.0);
        double eased = 1.0 - Math.Pow(1.0 - t, 3);

        cameraX = snap.StartX + (snap.TargetX - snap.StartX) * eased;
        cameraY = snap.StartY + (snap.TargetY - snap.StartY) * eased;

        if (t >= 1.0)
        {
            _snap = null;
            return false;
        }
        return true;
    }

    private sealed class SnapAnimation
    {
        public double StartX, StartY, TargetX, TargetY;
        public required Stopwatch Timer;
        public double DurationMs;
    }
}
