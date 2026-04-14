using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using RailReader2.ViewModels;
using SkiaSharp;

namespace RailReader2.Views;

public partial class MinimapControl : UserControl
{
    private MainWindowViewModel? _vm;
    public MainWindowViewModel? ViewModel
    {
        get => _vm;
        set { _vm = value; ApplyConfig(); }
    }

    private const double MoveStripeHeight = 12;
    private const double ResizeHandleSize = 18;
    private const double NavigateDragThreshold = 4;
    private const double MinSize = 120;

    private enum DragMode { None, Pending, Move, Resize, Navigate }
    private DragMode _drag = DragMode.None;
    private Point _dragStart;
    private double _dragStartW, _dragStartH, _dragStartMR, _dragStartMB;
    private bool _hover;

    public MinimapControl()
    {
        InitializeComponent();
        PointerEntered += (_, _) => { _hover = true; InvalidateVisual(); };
        PointerExited += (_, _) =>
        {
            _hover = false;
            // Reset cursor when the pointer leaves so a stale resize/move
            // cursor doesn't linger if the control was hovered then exited.
            Cursor = new Cursor(StandardCursorType.Hand);
            InvalidateVisual();
        };
    }

    private void ApplyConfig()
    {
        if (_vm?.Config is not { } c) return;
        Width = c.MinimapWidth;
        Height = c.MinimapHeight;
        Margin = new Thickness(0, 0, c.MinimapMarginRight, c.MinimapMarginBottom);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        bool dragging = _drag is DragMode.Move or DragMode.Resize;
        context.Custom(new MinimapDrawOperation(
            new Rect(0, 0, Bounds.Width, Bounds.Height),
            _vm,
            showChrome: _hover || dragging,
            drag: dragging,
            resizeCorner: ResizeCornerInside()));
    }

    /// <summary>
    /// The corner pointing into the visible viewport, opposite the screen
    /// edge the minimap is docked against. Default bottom-right docking
    /// places the resize handle at the top-left.
    /// </summary>
    private Corner ResizeCornerInside()
    {
        bool nearRight = HorizontalAlignment != Avalonia.Layout.HorizontalAlignment.Left;
        bool nearBottom = VerticalAlignment != Avalonia.Layout.VerticalAlignment.Top;
        return (nearRight, nearBottom) switch
        {
            (true, true)   => Corner.TopLeft,
            (true, false)  => Corner.BottomLeft,
            (false, true)  => Corner.TopRight,
            (false, false) => Corner.BottomRight,
        };
    }

    internal enum Corner { TopLeft, TopRight, BottomLeft, BottomRight }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        _dragStart = e.GetPosition(this);
        _dragStartW = Bounds.Width;
        _dragStartH = Bounds.Height;
        _dragStartMR = Margin.Right;
        _dragStartMB = Margin.Bottom;

        _drag = HitResizeHandle(_dragStart) ? DragMode.Resize
              : HitMoveStripe(_dragStart)   ? DragMode.Move
              :                               DragMode.Pending;

        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pt = e.GetPosition(this);

        if (_drag == DragMode.None)
        {
            UpdateCursor(pt);
            return;
        }

        double dx = pt.X - _dragStart.X;
        double dy = pt.Y - _dragStart.Y;

        if (_drag == DragMode.Pending)
        {
            if (Math.Abs(dx) < NavigateDragThreshold && Math.Abs(dy) < NavigateDragThreshold)
                return;
            _drag = DragMode.Navigate;
            if (_vm?.ActiveTab is { } t0) NavigateToPoint(_dragStart, t0);
        }

        switch (_drag)
        {
            case DragMode.Resize:
                ApplyResize(dx, dy);
                break;
            case DragMode.Move:
                ApplyMove(dx, dy);
                break;
            case DragMode.Navigate:
                if (_vm?.ActiveTab is { } tab) NavigateToPoint(pt, tab);
                break;
        }
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var pt = e.GetPosition(this);
        var prev = _drag;
        _drag = DragMode.None;

        if (prev == DragMode.Pending)
        {
            if (_vm?.ActiveTab is { } tab) NavigateToPoint(pt, tab);
            TopLevel.GetTopLevel(this)?.Focus();
        }
        else if (prev is DragMode.Move or DragMode.Resize)
        {
            PersistLayout();
            InvalidateVisual();
        }
        else if (prev == DragMode.Navigate)
        {
            TopLevel.GetTopLevel(this)?.Focus();
        }

        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void UpdateCursor(Point p)
    {
        Cursor = HitResizeHandle(p) ? new Cursor(CursorForResizeCorner())
              : HitMoveStripe(p)    ? new Cursor(StandardCursorType.SizeAll)
              :                       new Cursor(StandardCursorType.Hand);
    }

    private StandardCursorType CursorForResizeCorner() => ResizeCornerInside() switch
    {
        Corner.TopLeft     => StandardCursorType.TopLeftCorner,
        Corner.TopRight    => StandardCursorType.TopRightCorner,
        Corner.BottomLeft  => StandardCursorType.BottomLeftCorner,
        Corner.BottomRight => StandardCursorType.BottomRightCorner,
        _ => StandardCursorType.Arrow,
    };

    private bool HitResizeHandle(Point p) => ResizeCornerInside() switch
    {
        Corner.TopLeft     => p.X < ResizeHandleSize && p.Y < ResizeHandleSize,
        Corner.TopRight    => p.X > Bounds.Width - ResizeHandleSize && p.Y < ResizeHandleSize,
        Corner.BottomLeft  => p.X < ResizeHandleSize && p.Y > Bounds.Height - ResizeHandleSize,
        Corner.BottomRight => p.X > Bounds.Width - ResizeHandleSize && p.Y > Bounds.Height - ResizeHandleSize,
        _ => false,
    };

    private bool HitMoveStripe(Point p) => p.Y < MoveStripeHeight;

    private void ApplyMove(double dx, double dy)
    {
        var (winW, winH) = WindowClientSize();
        double newMR = Math.Clamp(_dragStartMR - dx, 0, Math.Max(0, winW - Bounds.Width));
        double newMB = Math.Clamp(_dragStartMB - dy, 0, Math.Max(0, winH - Bounds.Height));
        Margin = new Thickness(0, 0, newMR, newMB);
    }

    private void ApplyResize(double dx, double dy)
    {
        var c = ResizeCornerInside();
        double signX = c is Corner.TopLeft or Corner.BottomLeft ? -1 : 1;
        double signY = c is Corner.TopLeft or Corner.TopRight ? -1 : 1;
        double rawW = _dragStartW + signX * dx;
        double rawH = _dragStartH + signY * dy;

        var (winW, winH) = WindowClientSize();
        double maxW = Math.Max(MinSize, winW * 0.8);
        double maxH = Math.Max(MinSize, winH * 0.8);

        // Lock to page aspect: whichever drag axis is proportionally larger
        // drives the resize, and the other dimension follows.
        double newW = rawW, newH = rawH;
        if (_vm?.ActiveTab is { } tab && tab.PageWidth > 0 && tab.PageHeight > 0)
        {
            double aspect = tab.PageWidth / tab.PageHeight;
            double propX = Math.Abs(dx) / Math.Max(1, _dragStartW);
            double propY = Math.Abs(dy) / Math.Max(1, _dragStartH);
            if (propX >= propY) newH = rawW / aspect;
            else                newW = rawH * aspect;

            if (newW > maxW)   { newW = maxW;   newH = newW / aspect; }
            if (newH > maxH)   { newH = maxH;   newW = newH * aspect; }
            if (newW < MinSize) { newW = MinSize; newH = newW / aspect; }
            if (newH < MinSize) { newH = MinSize; newW = newH * aspect; }
        }
        else
        {
            newW = Math.Clamp(rawW, MinSize, maxW);
            newH = Math.Clamp(rawH, MinSize, maxH);
        }

        // For "into the screen" handles (top-left when docked bottom-right),
        // the right/bottom margins stay anchored — Avalonia layout grows the
        // control toward the top-left automatically.
        Width = newW;
        Height = newH;
        InvalidateVisual();
    }

    private void PersistLayout()
    {
        if (_vm?.Config is not { } c) return;
        c.MinimapWidth = Bounds.Width;
        c.MinimapHeight = Bounds.Height;
        c.MinimapMarginRight = Margin.Right;
        c.MinimapMarginBottom = Margin.Bottom;
        c.Save();
    }

    private (double W, double H) WindowClientSize()
    {
        var top = TopLevel.GetTopLevel(this);
        return top is null ? (1200, 900) : (top.ClientSize.Width, top.ClientSize.Height);
    }

    private void NavigateToPoint(Point pos, TabViewModel tab)
    {
        double controlW = Bounds.Width;
        double controlH = Bounds.Height;
        var thumb = ThumbnailGeometry.Compute(controlW, controlH, tab.PageWidth, tab.PageHeight);
        if (thumb is null) return;
        var t = thumb.Value;

        double pageX = (pos.X - t.X) / t.Scale;
        double pageY = (pos.Y - t.Y) / t.Scale;

        var (winW, winH) = WindowClientSize();

        tab.Camera.OffsetX = winW / 2.0 - pageX * tab.Camera.Zoom;
        tab.Camera.OffsetY = winH / 2.0 - pageY * tab.Camera.Zoom;
        tab.ClampCamera(winW, winH);

        tab.UpdateRailZoom(winW, winH);
        if (tab.Rail.Active)
        {
            tab.Rail.FindNearestBlock(tab.Camera.OffsetX, tab.Camera.OffsetY,
                tab.Camera.Zoom, winW, winH);
            tab.StartSnap(winW, winH);
            ViewModel?.RequestAnimationFrame();
        }

        ViewModel?.RequestCameraUpdate();
    }

    private readonly record struct ThumbnailGeometry(double X, double Y, double W, double H, double Scale)
    {
        public static ThumbnailGeometry? Compute(double controlW, double controlH, double pageW, double pageH)
        {
            if (controlW <= 0 || controlH <= 0 || pageW <= 0 || pageH <= 0) return null;
            double scale = Math.Min(controlW / pageW, controlH / pageH);
            double w = pageW * scale;
            double h = pageH * scale;
            return new ThumbnailGeometry((controlW - w) / 2, (controlH - h) / 2, w, h, scale);
        }
    }

    private sealed class MinimapDrawOperation : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly MainWindowViewModel? _vm;
        private readonly bool _showChrome;
        private readonly bool _drag;
        private readonly Corner _resizeCorner;

        // Snapshot for Equals — quantised to avoid per-pixel redraws.
        private readonly SKImage? _thumbImage;
        private readonly SKImage? _primaryImage;
        private readonly int _oxQ, _oyQ, _zoomQ;

        [ThreadStatic] private static SKPaint? s_bgPaint;
        [ThreadStatic] private static SKPaint? s_vpFill;
        [ThreadStatic] private static SKPaint? s_vpStroke;
        [ThreadStatic] private static SKPaint? s_borderPaint;
        [ThreadStatic] private static SKPaint? s_chromeFill;
        [ThreadStatic] private static SKPaint? s_gripDot;

        // Mitchell at rest for crisp thumbnails; Linear during drag for cheapness.
        private static readonly SKSamplingOptions s_samplingRest =
            new(SKCubicResampler.Mitchell);
        private static readonly SKSamplingOptions s_samplingDrag =
            new(SKFilterMode.Linear, SKMipmapMode.Linear);

        public MinimapDrawOperation(Rect bounds, MainWindowViewModel? vm,
            bool showChrome, bool drag, Corner resizeCorner)
        {
            _bounds = bounds;
            _vm = vm;
            _showChrome = showChrome;
            _drag = drag;
            _resizeCorner = resizeCorner;
            var tab = vm?.ActiveTab;
            _thumbImage = tab?.MinimapImage;
            _primaryImage = tab?.CachedImage;
            _oxQ = (int)(tab?.Camera.OffsetX ?? 0) / 16;
            _oyQ = (int)(tab?.Camera.OffsetY ?? 0) / 16;
            _zoomQ = (int)((tab?.Camera.Zoom ?? 1.0) * 50);
        }

        public Rect Bounds => _bounds;
        public void Dispose() { }

        public bool Equals(ICustomDrawOperation? other)
            => other is MinimapDrawOperation op
            && _bounds == op._bounds
            && _showChrome == op._showChrome
            && _drag == op._drag
            && _resizeCorner == op._resizeCorner
            && ReferenceEquals(_thumbImage, op._thumbImage)
            && ReferenceEquals(_primaryImage, op._primaryImage)
            && _oxQ == op._oxQ
            && _oyQ == op._oyQ
            && _zoomQ == op._zoomQ;
        public bool HitTest(Point p) => _bounds.Contains(p);

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature leaseFeature)
                return;

            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;

            var tab = _vm?.ActiveTab;
            if (tab is null) return;

            float controlW = (float)_bounds.Width;
            float controlH = (float)_bounds.Height;
            var controlRect = new SKRoundRect(SKRect.Create(0, 0, controlW, controlH), 4);

            var bgPaint = s_bgPaint ??= new SKPaint { Color = new SKColor(40, 40, 40, 200) };
            canvas.DrawRoundRect(controlRect, bgPaint);

            var thumb = ThumbnailGeometry.Compute(controlW, controlH, tab.PageWidth, tab.PageHeight);
            if (thumb is null) return;
            var t = thumb.Value;
            var destRect = SKRect.Create((float)t.X, (float)t.Y, (float)t.W, (float)t.H);

            // Tier 1 source switching: when the drawn thumbnail size meaningfully
            // exceeds the cached thumbnail bitmap's resolution, render from the
            // primary's high-DPI bitmap instead.
            var sampling = _drag ? s_samplingDrag : s_samplingRest;
            int displayLong = (int)Math.Max(t.W, t.H);
            int thumbLong = _thumbImage is { } th ? Math.Max(th.Width, th.Height) : 0;
            bool preferPrimary = _primaryImage is not null && displayLong > thumbLong * 1.1;

            var source = preferPrimary ? _primaryImage : _thumbImage;
            if (source is not null)
                canvas.DrawImage(source, destRect, sampling);

            // Viewport indicator.
            var window = Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow : null;
            double winW = window?.ClientSize.Width ?? 1200;
            double winH = window?.ClientSize.Height ?? 900;
            float scale = (float)t.Scale;

            var vpRect = SKRect.Create(
                (float)(-tab.Camera.OffsetX / tab.Camera.Zoom) * scale + (float)t.X,
                (float)(-tab.Camera.OffsetY / tab.Camera.Zoom) * scale + (float)t.Y,
                (float)(winW / tab.Camera.Zoom) * scale,
                (float)(winH / tab.Camera.Zoom) * scale);

            var vpFill = s_vpFill ??= new SKPaint { Color = new SKColor(100, 180, 255, 120) };
            canvas.DrawRect(vpRect, vpFill);

            var vpStroke = s_vpStroke ??= new SKPaint
            {
                Color = new SKColor(100, 180, 255, 220),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.5f,
                IsAntialias = true,
            };
            canvas.DrawRect(vpRect, vpStroke);

            if (_showChrome)
            {
                DrawMoveStripe(canvas, controlW);
                DrawResizeHandle(canvas, controlW, controlH);
            }

            var borderPaint = s_borderPaint ??= new SKPaint
            {
                Color = new SKColor(100, 100, 100),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1,
            };
            canvas.DrawRoundRect(controlRect, borderPaint);
        }

        private static void DrawMoveStripe(SKCanvas canvas, float controlW)
        {
            const float h = (float)MoveStripeHeight;
            var fill = s_chromeFill ??= new SKPaint { Color = new SKColor(255, 255, 255, 30) };
            canvas.DrawRect(SKRect.Create(0, 0, controlW, h), fill);

            var dot = s_gripDot ??= new SKPaint { Color = new SKColor(220, 220, 220, 220), IsAntialias = true };
            float cy = h / 2f;
            float cx = controlW / 2f;
            const float spacing = 4f;
            const float r = 1.2f;
            for (int i = -2; i <= 2; i++)
                canvas.DrawCircle(cx + i * spacing, cy, r, dot);
        }

        private void DrawResizeHandle(SKCanvas canvas, float controlW, float controlH)
        {
            const float s = (float)ResizeHandleSize;
            var fill = s_chromeFill ??= new SKPaint { Color = new SKColor(255, 255, 255, 30) };
            var (x, y) = _resizeCorner switch
            {
                Corner.TopLeft     => (0f, 0f),
                Corner.TopRight    => (controlW - s, 0f),
                Corner.BottomLeft  => (0f, controlH - s),
                Corner.BottomRight => (controlW - s, controlH - s),
                _ => (0f, 0f),
            };
            canvas.DrawRect(SKRect.Create(x, y, s, s), fill);

            // Diagonal hash lines, oriented toward the screen edge.
            var line = s_gripDot ??= new SKPaint { Color = new SKColor(220, 220, 220, 220), IsAntialias = true };
            line.Style = SKPaintStyle.Stroke;
            line.StrokeWidth = 1f;
            for (int i = 1; i <= 3; i++)
            {
                float off = i * 4f;
                switch (_resizeCorner)
                {
                    case Corner.TopLeft:
                        canvas.DrawLine(x + off, y + s, x + s, y + off, line);
                        break;
                    case Corner.TopRight:
                        canvas.DrawLine(x + s - off, y + s, x, y + off, line);
                        break;
                    case Corner.BottomLeft:
                        canvas.DrawLine(x + off, y, x + s, y + s - off, line);
                        break;
                    case Corner.BottomRight:
                        canvas.DrawLine(x + s - off, y, x, y + s - off, line);
                        break;
                }
            }
            line.Style = SKPaintStyle.Fill;
            line.StrokeWidth = 0;
        }
    }
}
