using Avalonia;
using Avalonia.Controls;
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
        ClipToBounds = true;
        Width = 180;
        Height = 240;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom;
        Margin = new Thickness(0, 0, 10, 10);
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
        // Return focus to the main window so keyboard shortcuts continue working
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
        if (controlW <= 0 || controlH <= 0 || tab.PageWidth <= 0 || tab.PageHeight <= 0) return;

        double scale = Math.Min(controlW / tab.PageWidth, controlH / tab.PageHeight);
        double thumbW = tab.PageWidth * scale;
        double thumbH = tab.PageHeight * scale;
        double thumbX = (controlW - thumbW) / 2;
        double thumbY = (controlH - thumbH) / 2;

        // Convert click position to page coordinates
        double pageX = (pos.X - thumbX) / scale;
        double pageY = (pos.Y - thumbY) / scale;

        // Center the window on that page point
        var window = TopLevel.GetTopLevel(this);
        double winW = window?.ClientSize.Width ?? 1200;
        double winH = window?.ClientSize.Height ?? 900;

        tab.Camera.OffsetX = winW / 2.0 - pageX * tab.Camera.Zoom;
        tab.Camera.OffsetY = winH / 2.0 - pageY * tab.Camera.Zoom;
        tab.ClampCamera(winW, winH);

        // Update rail state: find nearest block at new camera position
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
            var leaseFeature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
            if (leaseFeature is null) return;

            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;

            var tab = _vm?.ActiveTab;
            if (tab?.MinimapBitmap is not { } bitmap) return;

            float controlW = (float)_bounds.Width;
            float controlH = (float)_bounds.Height;

            // Background
            using var bgPaint = new SKPaint { Color = new SKColor(40, 40, 40, 200) };
            canvas.DrawRoundRect(new SKRoundRect(SKRect.Create(0, 0, controlW, controlH), 4), bgPaint);

            // Scale bitmap to fit
            float scale = Math.Min(controlW / (float)tab.PageWidth, controlH / (float)tab.PageHeight);
            float thumbW = (float)tab.PageWidth * scale;
            float thumbH = (float)tab.PageHeight * scale;
            float thumbX = (controlW - thumbW) / 2;
            float thumbY = (controlH - thumbH) / 2;

            var destRect = SKRect.Create(thumbX, thumbY, thumbW, thumbH);
            canvas.DrawBitmap(bitmap, destRect);

            // Viewport rectangle
            var window = Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow : null;
            double winW = window?.ClientSize.Width ?? 1200;
            double winH = window?.ClientSize.Height ?? 900;

            float vpLeft = (float)(-tab.Camera.OffsetX / tab.Camera.Zoom) * scale + thumbX;
            float vpTop = (float)(-tab.Camera.OffsetY / tab.Camera.Zoom) * scale + thumbY;
            float vpW = (float)(winW / tab.Camera.Zoom) * scale;
            float vpH = (float)(winH / tab.Camera.Zoom) * scale;

            using var vpPaint = new SKPaint
            {
                Color = new SKColor(100, 180, 255, 120),
                Style = SKPaintStyle.Fill,
            };
            canvas.DrawRect(SKRect.Create(vpLeft, vpTop, vpW, vpH), vpPaint);

            using var vpStroke = new SKPaint
            {
                Color = new SKColor(100, 180, 255, 220),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.5f,
                IsAntialias = true,
            };
            canvas.DrawRect(SKRect.Create(vpLeft, vpTop, vpW, vpH), vpStroke);

            // Border
            using var borderPaint = new SKPaint
            {
                Color = new SKColor(100, 100, 100),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1,
            };
            canvas.DrawRoundRect(new SKRoundRect(SKRect.Create(0, 0, controlW, controlH), 4), borderPaint);
        }
    }
}
