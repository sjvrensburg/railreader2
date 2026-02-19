namespace RailReader2.Models;

public sealed class Camera
{
    public const double ZoomMin = 0.1;
    public const double ZoomMax = 20.0;

    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double Zoom { get; set; } = 1.0;
}
