using System.Diagnostics;
using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Core;

/// <summary>
/// Manages smooth zoom animations (cubic ease-out) toward a focus point.
/// Extracted from DocumentController for testability and separation of concerns.
/// </summary>
internal sealed class ZoomAnimationController
{
    internal const double ZoomStep = 1.25;
    internal const double ZoomScrollSensitivity = 0.003;

    private readonly AppConfig _config;

    private sealed class ZoomAnimation
    {
        public double StartZoom, TargetZoom;
        public double StartOffsetX, StartOffsetY;
        public double TargetOffsetX, TargetOffsetY;
        public double CursorPageX, CursorPageY;
        public Stopwatch Timer = Stopwatch.StartNew();
        public const double DurationMs = 180;
        // Rail position preservation: captured when zoom starts in rail mode
        public double HorizontalFraction = -1; // 0=line start, 1=line end; <0 means not in rail
        public double LineScreenY;              // Y position of active line on screen
    }

    private ZoomAnimation? _zoomAnim;

    public ZoomAnimationController(AppConfig config)
    {
        _config = config;
    }

    /// <summary>Whether a zoom animation is currently in progress.</summary>
    public bool IsAnimating => _zoomAnim is not null;

    /// <summary>The target zoom level of the current animation, or null if not animating.</summary>
    public double? PendingTargetZoom => _zoomAnim?.TargetZoom;

    /// <summary>The target X offset of the current animation, or null if not animating.</summary>
    public double? PendingTargetOffsetX => _zoomAnim?.TargetOffsetX;

    /// <summary>The target Y offset of the current animation, or null if not animating.</summary>
    public double? PendingTargetOffsetY => _zoomAnim?.TargetOffsetY;

    /// <summary>Cancel any in-progress zoom animation.</summary>
    public void Cancel()
    {
        _zoomAnim = null;
    }

    /// <summary>
    /// Starts a smooth zoom animation toward <paramref name="focusX"/>,<paramref name="focusY"/>
    /// (screen coordinates). Accumulates from any in-progress animation.
    /// </summary>
    public void Start(DocumentState doc, double newZoom, double focusX, double focusY, double vpWidth)
    {
        double baseOx = _zoomAnim?.TargetOffsetX ?? doc.Camera.OffsetX;
        double baseOy = _zoomAnim?.TargetOffsetY ?? doc.Camera.OffsetY;
        double baseZoom = _zoomAnim?.TargetZoom ?? doc.Camera.Zoom;

        double targetOx = focusX - (focusX - baseOx) * (newZoom / baseZoom);
        double targetOy = focusY - (focusY - baseOy) * (newZoom / baseZoom);

        // Capture rail reading position before zoom so we can restore it on completion
        double hFraction = -1;
        double lineScreenY = 0;
        if (doc.Rail.Active && doc.Rail.HasAnalysis)
        {
            hFraction = doc.Rail.ComputeHorizontalFraction(doc.Camera.OffsetX, doc.Camera.Zoom, vpWidth);
            lineScreenY = doc.Rail.CurrentLineInfo.Y * doc.Camera.Zoom + doc.Camera.OffsetY;
        }

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
            HorizontalFraction = hFraction,
            LineScreenY = lineScreenY,
        };
    }

    /// <summary>Smooth zoom animation step.</summary>
    public void Tick(DocumentState doc, double ww, double wh,
        ref bool cameraChanged, ref bool animating)
    {
        if (_zoomAnim is { } za)
        {
            double elapsed = za.Timer.Elapsed.TotalMilliseconds;
            double t = Math.Clamp(elapsed / ZoomAnimation.DurationMs, 0, 1);
            // Cubic ease-out: 1 - (1-t)^3
            double ease = 1.0 - (1.0 - t) * (1.0 - t) * (1.0 - t);

            double prevZoom = doc.Camera.Zoom;
            doc.Camera.Zoom = za.StartZoom + (za.TargetZoom - za.StartZoom) * ease;
            doc.Camera.OffsetX = za.StartOffsetX + (za.TargetOffsetX - za.StartOffsetX) * ease;
            doc.Camera.OffsetY = za.StartOffsetY + (za.TargetOffsetY - za.StartOffsetY) * ease;
            doc.Camera.NotifyZoomChange();
            doc.Rail.ScaleVerticalBias(prevZoom, doc.Camera.Zoom);
            doc.UpdateRailZoom(ww, wh, za.CursorPageX, za.CursorPageY);
            cameraChanged = true;

            if (t >= 1.0)
            {
                double hFrac = za.HorizontalFraction;
                double lineY = za.LineScreenY;
                _zoomAnim = null;
                doc.ClampCamera(ww, wh);
                if (doc.Rail.Active)
                {
                    if (hFrac >= 0)
                        doc.StartSnapPreservingPosition(ww, wh, hFrac, lineY);
                    else
                        doc.StartSnap(ww, wh);
                }
                doc.UpdateRenderDpiIfNeeded();
            }
            else
            {
                animating = true;
            }
        }
    }
}
