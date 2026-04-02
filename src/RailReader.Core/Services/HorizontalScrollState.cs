using System.Diagnostics;
using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Manages horizontal scroll hold state with quadratic speed ramping.
/// Extracted from RailNav to consolidate the 7 previously-scattered fields.
/// </summary>
internal sealed class HorizontalScrollState
{
    private ScrollDirection? _direction;
    private Stopwatch? _holdTimer;
    private double _startX;
    private double _seedSecs;
    private double _lastSpeedStart;
    private double _lastSpeedMax;
    private double _displacementOffset;

    public bool Active => _direction is not null;
    public ScrollDirection? Direction => _direction;
    public double StartX => _startX;

    /// <summary>Elapsed hold time in seconds, including the seed offset for first-frame displacement.</summary>
    public double ElapsedSecs => _seedSecs + (_holdTimer?.Elapsed.TotalSeconds ?? 0);

    /// <summary>Elapsed hold time in milliseconds.</summary>
    public double ElapsedMs => ElapsedSecs * 1000;

    public void Start(ScrollDirection dir, double startX, double speedStart, double speedMax)
    {
        _direction = dir;
        _holdTimer = Stopwatch.StartNew();
        _startX = startX;
        _seedSecs = 1.0 / 60.0;
        _lastSpeedStart = speedStart;
        _lastSpeedMax = speedMax;
        _displacementOffset = 0;
    }

    public void Stop()
    {
        _direction = null;
        _holdTimer = null;
        _seedSecs = 0;
    }

    /// <summary>
    /// Computes total displacement from the scroll start position, accounting for
    /// any mid-scroll speed parameter changes.
    /// </summary>
    public double ComputeDisplacement(double speedStart, double speedMax, double ramp)
    {
        // When speed params change mid-scroll, absorb the displacement difference
        // so the camera doesn't jump. The timer keeps running, so the velocity
        // transitions naturally along the new ramp curve from the current elapsed time.
        if (speedStart != _lastSpeedStart || speedMax != _lastSpeedMax)
        {
            double holdSecs = ElapsedSecs;
            double oldDisp = DisplacementIntegral(_lastSpeedStart, _lastSpeedMax, ramp, holdSecs);
            double newDisp = DisplacementIntegral(speedStart, speedMax, ramp, holdSecs);
            _displacementOffset += oldDisp - newDisp;
            _lastSpeedStart = speedStart;
            _lastSpeedMax = speedMax;
        }

        return DisplacementIntegral(speedStart, speedMax, ramp, ElapsedSecs) + _displacementOffset;
    }

    /// <summary>
    /// Integral of the quadratic speed ramp: speed(t) = start + (max-start)*(t/ramp)².
    /// Returns total displacement in page-coordinate pixels.
    /// </summary>
    internal static double DisplacementIntegral(double speedStart, double speedMax, double ramp, double t)
    {
        if (t <= ramp)
            return speedStart * t + (speedMax - speedStart) * t * t * t / (3.0 * ramp * ramp);
        double rampDisp = speedStart * ramp + (speedMax - speedStart) * ramp / 3.0;
        return rampDisp + speedMax * (t - ramp);
    }
}
