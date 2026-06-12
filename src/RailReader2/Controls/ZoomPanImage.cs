using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace RailReader2.Controls;

/// <summary>
/// An image presenter with mouse zoom and pan, for inspecting portal crops (dense tables, small
/// figure text) without leaving the reading position: wheel zooms around the cursor, left-drag pans,
/// double-click resets to fit. A new <see cref="Source"/> resets the view to fit, so each newly
/// pinned target starts fully visible like the plain Image it replaces.
/// </summary>
public sealed class ZoomPanImage : Control
{
    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<ZoomPanImage, IImage?>(nameof(Source));

    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    // Zoom relative to the fit-to-bounds scale (1 = fit) and pan offset of the image centre from the
    // viewport centre, in viewport pixels. Pan is re-clamped on every change so the image can never
    // be lost off-screen.
    private double _zoom = 1.0;
    private Point _offset;
    private Point? _dragLast;

    private const double MaxZoom = 16.0;
    private const double WheelZoomStep = 1.2;

    private static readonly Cursor s_panCursor = new(StandardCursorType.SizeAll);

    static ZoomPanImage()
    {
        AffectsRender<ZoomPanImage>(SourceProperty);
        SourceProperty.Changed.AddClassHandler<ZoomPanImage>((c, _) => c.ResetView());
    }

    public ZoomPanImage()
    {
        ClipToBounds = true;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
    }

    private void ResetView()
    {
        _zoom = 1.0;
        _offset = default;
        EndDrag();   // a Source swap mid-drag must also restore the cursor
        InvalidateVisual();
    }

    private double FitScale(Size img)
        => img.Width <= 0 || img.Height <= 0
            ? 1.0
            : Math.Min(Bounds.Width / img.Width, Bounds.Height / img.Height);

    /// <summary>The on-screen rectangle the image currently occupies (centred + offset).</summary>
    private Rect DestRect(Size img)
    {
        double scale = FitScale(img) * _zoom;
        double w = img.Width * scale, h = img.Height * scale;
        return new Rect(
            (Bounds.Width - w) / 2 + _offset.X,
            (Bounds.Height - h) / 2 + _offset.Y,
            w, h);
    }

    /// <summary>Keep the image on-screen: axes where it is smaller than the viewport stay centred;
    /// larger axes may pan only until the image edge reaches the viewport edge.</summary>
    private void ClampOffset(Size img)
    {
        double scale = FitScale(img) * _zoom;
        double maxX = Math.Max(0, (img.Width * scale - Bounds.Width) / 2);
        double maxY = Math.Max(0, (img.Height * scale - Bounds.Height) / 2);
        _offset = new Point(Math.Clamp(_offset.X, -maxX, maxX), Math.Clamp(_offset.Y, -maxY, maxY));
    }

    public override void Render(DrawingContext context)
    {
        if (Source is not { } src) return;
        var img = src.Size;
        if (img.Width <= 0 || img.Height <= 0 || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        context.DrawImage(src, new Rect(img), DestRect(img));
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (Source is not { } src)
        {
            base.OnPointerWheelChanged(e);
            return;
        }

        double factor = Math.Pow(WheelZoomStep, e.Delta.Y);
        double newZoom = Math.Clamp(_zoom * factor, 1.0, MaxZoom);
        if (newZoom != _zoom)
        {
            // Zoom around the cursor: keep the image point under it stationary by scaling the
            // cursor→image-centre vector with the zoom ratio.
            var viewportCentre = new Point(Bounds.Width / 2, Bounds.Height / 2);
            var p = e.GetPosition(this);
            var toCentre = viewportCentre + _offset - p;
            _offset = p + toCentre * (newZoom / _zoom) - viewportCentre;
            _zoom = newZoom;
            ClampOffset(src.Size);
            InvalidateVisual();
            // Handle only wheel events that actually zoomed — no-ops (already at fit, horizontal
            // deltas) stay available to a scrollable ancestor if the control is ever hosted in one.
            e.Handled = true;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Source is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        if (e.ClickCount == 2)
        {
            ResetView();
            e.Handled = true;
            return;
        }
        if (_zoom > 1.0)
        {
            _dragLast = e.GetPosition(this);
            Cursor = s_panCursor;
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragLast is not { } last || Source is not { } src) return;
        // The button can be released without us seeing PointerReleased (capture stolen by a popup,
        // window deactivation) — cancel the drag rather than panning on plain hover.
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            EndDrag();
            return;
        }
        var p = e.GetPosition(this);
        _offset += p - last;
        _dragLast = p;
        ClampOffset(src.Size);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragLast is null) return;
        EndDrag();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    private void EndDrag()
    {
        if (_dragLast is null) return;
        _dragLast = null;
        Cursor = Cursor.Default;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (Source is { } src)
        {
            ClampOffset(src.Size);
            InvalidateVisual();
        }
    }
}
