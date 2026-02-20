namespace RailReader2.Models;

public sealed class Camera
{
    public const double ZoomMin = 0.1;
    public const double ZoomMax = 20.0;

    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double Zoom { get; set; } = 1.0;

    /// <summary>
    /// Normalized zoom speed (0.0–1.0) for motion blur during zoom.
    /// Decays each frame; set via <see cref="NotifyZoomChange"/>.
    /// </summary>
    public double ZoomSpeed { get; private set; }

    private double _prevZoom = 1.0;

    /// <summary>
    /// Call after changing Zoom to record the delta for motion blur.
    /// </summary>
    public void NotifyZoomChange()
    {
        double delta = Math.Abs(Zoom - _prevZoom) / Math.Max(_prevZoom, 0.1);
        // Normalize: a typical scroll wheel step is ~0.3% (factor 1.003),
        // key zoom step is ~25% (factor 1.25). Map 0–0.3 → 0–1.
        ZoomSpeed = Math.Clamp(delta / 0.3, 0.0, 1.0);
        _prevZoom = Zoom;
    }

    /// <summary>
    /// Decay zoom speed each frame. Call from the animation loop.
    /// </summary>
    public void DecayZoomSpeed(double dt)
    {
        // Exponential decay with ~80ms half-life
        ZoomSpeed *= Math.Exp(-dt / 0.08);
        if (ZoomSpeed < 0.01) ZoomSpeed = 0.0;
    }
}
