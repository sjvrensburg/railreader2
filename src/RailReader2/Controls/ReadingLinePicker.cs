using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using RailReader.Core.Models;

namespace RailReader2.Controls;

/// <summary>
/// A small picture of the reading window with the rail's line drawn across it. The reader drags the
/// line up or down (or clicks where it should go) to choose where the rail places the line it reads
/// (<see cref="CoreSettings.RailReadingLineFraction"/>). <see cref="RangeBase.Value"/> is the line's
/// height as a fraction of the window from the top. Keyboard: Up/Down nudge it, Page Up/Down move it
/// further, Home/End go to the limits; a double-click returns it to the middle. Exposed to
/// accessibility tools as a slider.
/// </summary>
public sealed class ReadingLinePicker : RangeBase
{
    private const double Centre = 0.5;
    private const double CentreSnap = 0.015;   // a drag this close to the middle lands on it exactly
    private const double BandHeight = 11;
    private const double TextLinePitch = 9;
    private const double TextLineThickness = 3;
    private const double Inset = 10;

    // Relative widths of the pretend text lines; 0 is a paragraph break. Fixed so the picture never shifts.
    private static readonly double[] s_lineWidths =
        [0.96, 0.92, 0.98, 0.9, 0.55, 0, 0.94, 0.97, 0.89, 0.95, 0.62, 0, 0.93, 0.98, 0.91, 0.96, 0.7];

    private static readonly IBrush s_pageBrush = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xF7));
    private static readonly IBrush s_textBrush = new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x70));
    private static readonly IBrush s_dimBrush = new SolidColorBrush(Color.FromArgb(0x70, 0x30, 0x30, 0x30));

    private static readonly IPen s_tickHaloPen = new Pen(Brushes.White, 3);
    private static readonly IPen s_tickPen = new Pen(s_textBrush, 1.5);
    private static readonly IPen s_gripOutlinePen = new Pen(Brushes.White, 1.5);
    private static readonly IPen s_framePen = new Pen(s_textBrush, 1);

    private bool _dragging;

    // Accent-coloured brushes and pens, rebuilt only when the accent changes (theme switch), not per frame.
    private Color _cachedAccent;
    private IBrush? _accentBrush, _bandBrush;
    private IPen? _accentPen, _focusPen;

    static ReadingLinePicker()
    {
        FocusableProperty.OverrideDefaultValue<ReadingLinePicker>(true);
        AffectsRender<ReadingLinePicker>(ValueProperty, MinimumProperty, MaximumProperty, IsFocusedProperty);
    }

    public ReadingLinePicker()
    {
        Minimum = CoreSettings.MinRailReadingLineFraction;
        Maximum = CoreSettings.MaxRailReadingLineFraction;
        Value = Centre;
        SmallChange = 0.02;
        LargeChange = 0.1;
        Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        ClipToBounds = true;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(200, 130);

    protected override AutomationPeer OnCreateAutomationPeer() => new PickerAutomationPeer(this);

    private sealed class PickerAutomationPeer(ReadingLinePicker owner) : RangeBaseAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
    }

    private Color Accent =>
        this.TryFindResource("SystemAccentColor", ActualThemeVariant, out var c) && c is Color color
            ? color
            : Color.FromRgb(0x1E, 0x78, 0xD7);

    private double LineY => Math.Clamp(Value, 0, 1) * Bounds.Height;

    private void EnsureAccentResources()
    {
        var accent = Accent;
        if (_accentBrush is not null && accent == _cachedAccent) return;
        _cachedAccent = accent;
        _accentBrush = new SolidColorBrush(accent);
        _bandBrush = new SolidColorBrush(accent, 0.18);
        _accentPen = new Pen(_accentBrush, 1.5);
        _focusPen = new Pen(_accentBrush, 2);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        EnsureAccentResources();
        var accentPen = _accentPen!;

        // The window, showing a page at rail zoom: the page fills the width, with lines of text.
        context.DrawRectangle(s_pageBrush, null, bounds, 4, 4);
        double y = Inset;
        foreach (var w in s_lineWidths)
        {
            if (y + TextLineThickness > bounds.Height - Inset / 2) break;
            if (w > 0)
                context.DrawRectangle(s_textBrush, null,
                    new Rect(Inset, y, (bounds.Width - 2 * Inset) * w, TextLineThickness), 1.5, 1.5);
            y += TextLinePitch;
        }

        // The rail's look: everything but the reading band dimmed, the band outlined in the accent.
        double lineY = LineY;
        var band = new Rect(0, lineY - BandHeight / 2, bounds.Width, BandHeight);
        context.DrawRectangle(s_dimBrush, null, new Rect(0, 0, bounds.Width, Math.Max(0, band.Top)));
        context.DrawRectangle(s_dimBrush, null, new Rect(0, band.Bottom, bounds.Width, Math.Max(0, bounds.Height - band.Bottom)));
        context.DrawRectangle(_bandBrush, null, band);
        context.DrawLine(accentPen, band.TopLeft, band.TopRight);
        context.DrawLine(accentPen, band.BottomLeft, band.BottomRight);

        // A tick on the left edge marks the middle (the default).
        double midY = Centre * bounds.Height;
        context.DrawLine(s_tickHaloPen, new Point(0, midY), new Point(6, midY));
        context.DrawLine(s_tickPen, new Point(0, midY), new Point(6, midY));

        // A grip on the right says "drag me up or down".
        var grip = new Rect(bounds.Width - 26, lineY - 9, 20, 18);
        context.DrawRectangle(_accentBrush, s_gripOutlinePen, grip, 4, 4);
        double cx = grip.Center.X;
        DrawTriangle(context, Brushes.White, cx, grip.Top + 3, grip.Top + 7.5);      // up
        DrawTriangle(context, Brushes.White, cx, grip.Bottom - 3, grip.Bottom - 7.5); // down

        // Frame; the accent, thicker, while the control has keyboard focus.
        var framePen = IsFocused ? _focusPen! : s_framePen;
        context.DrawRectangle(null, framePen, bounds.Deflate(framePen.Thickness / 2), 4, 4);
    }

    /// <summary>A small isosceles triangle with its tip at <paramref name="tipY"/> and its base at <paramref name="baseY"/>.</summary>
    private static void DrawTriangle(DrawingContext context, IBrush brush, double cx, double tipY, double baseY)
    {
        // One shared unit triangle (tip at the origin, base 1 below), placed with a transform so a
        // drag doesn't allocate two geometries per frame.
        using (context.PushTransform(Matrix.CreateScale(1, baseY - tipY) * Matrix.CreateTranslation(cx, tipY)))
            context.DrawGeometry(brush, null, s_unitTriangle);
    }

    private static readonly StreamGeometry s_unitTriangle = CreateUnitTriangle();

    private static StreamGeometry CreateUnitTriangle()
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(0, 0), true);
            ctx.LineTo(new Point(4, 1));
            ctx.LineTo(new Point(-4, 1));
            ctx.EndFigure(true);
        }
        return g;
    }

    private void SetFromPointer(Point p)
    {
        if (Bounds.Height <= 0) return;
        double v = p.Y / Bounds.Height;
        if (Math.Abs(v - Centre) < CentreSnap) v = Centre;
        Value = Math.Clamp(v, Minimum, Maximum);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus(NavigationMethod.Pointer);
        if (e.ClickCount >= 2)
        {
            Value = Centre;
        }
        else
        {
            _dragging = true;
            e.Pointer.Capture(this);
            SetFromPointer(e.GetPosition(this));
        }
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        SetFromPointer(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        double? v = e.Key switch
        {
            Key.Up => Value - SmallChange,
            Key.Down => Value + SmallChange,
            Key.PageUp => Value - LargeChange,
            Key.PageDown => Value + LargeChange,
            Key.Home => Minimum,
            Key.End => Maximum,
            _ => null,
        };
        if (v is not { } next) return;
        Value = Math.Clamp(next, Minimum, Maximum);
        e.Handled = true;
    }
}
