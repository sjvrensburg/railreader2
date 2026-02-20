using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using RailReader2.ViewModels;
using SkiaSharp;

namespace RailReader2.Views;

public partial class MinimapControl : UserControl
{
    public MainWindowViewModel? ViewModel { get; set; }

    public MinimapControl()
    {
        InitializeComponent();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.Custom(new MinimapDrawOperation(new Rect(0, 0, Bounds.Width, Bounds.Height), ViewModel));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (ViewModel?.ActiveTab is not { } tab) return;
        NavigateToPoint(e.GetPosition(this), tab);
        TopLevel.GetTopLevel(this)?.Focus();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (ViewModel?.ActiveTab is not { } tab) return;
        NavigateToPoint(e.GetPosition(this), tab);
        e.Handled = true;
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

        var window = TopLevel.GetTopLevel(this);
        double winW = window?.ClientSize.Width ?? 1200;
        double winH = window?.ClientSize.Height ?? 900;

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

        public MinimapDrawOperation(Rect bounds, MainWindowViewModel? vm)
        {
            _bounds = bounds;
            _vm = vm;
        }

        public Rect Bounds => _bounds;
        public void Dispose() { }
        public bool Equals(ICustomDrawOperation? other) => false;
        public bool HitTest(Point p) => _bounds.Contains(p);

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature leaseFeature)
                return;

            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;

            var tab = _vm?.ActiveTab;
            if (tab?.MinimapBitmap is not { } bitmap) return;

            float controlW = (float)_bounds.Width;
            float controlH = (float)_bounds.Height;
            var controlRect = new SKRoundRect(SKRect.Create(0, 0, controlW, controlH), 4);

            using var bgPaint = new SKPaint { Color = new SKColor(40, 40, 40, 200) };
            canvas.DrawRoundRect(controlRect, bgPaint);

            var thumb = ThumbnailGeometry.Compute(controlW, controlH, tab.PageWidth, tab.PageHeight);
            if (thumb is null) return;
            var t = thumb.Value;

            var destRect = SKRect.Create((float)t.X, (float)t.Y, (float)t.W, (float)t.H);
            canvas.DrawBitmap(bitmap, destRect);

            // Viewport indicator
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

            using var vpFill = new SKPaint { Color = new SKColor(100, 180, 255, 120) };
            canvas.DrawRect(vpRect, vpFill);

            using var vpStroke = new SKPaint
            {
                Color = new SKColor(100, 180, 255, 220),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.5f,
                IsAntialias = true,
            };
            canvas.DrawRect(vpRect, vpStroke);

            using var borderPaint = new SKPaint
            {
                Color = new SKColor(100, 100, 100),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1,
            };
            canvas.DrawRoundRect(controlRect, borderPaint);
        }
    }
}
