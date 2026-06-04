using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace RailReader2.Controls;

/// <summary>
/// A small decorative vector icon (Lucide path data). Renders the geometry as a
/// stroked path that inherits <see cref="TemplatedControl.Foreground"/> and scales
/// uniformly via a Viewbox, so it tracks button hover/checked/disabled states and the
/// theme's text colour like the old icon font did. Its size tracks the inherited
/// <see cref="TemplatedControl.FontSize"/> (which scales with the UI font-scale setting),
/// so icons grow/shrink with the rest of the UI.
///
/// Per Lucide's accessibility guidance the icon itself is decorative and hidden from the
/// accessibility tree — the host control (e.g. a button) carries the accessible name via
/// AutomationProperties.Name.
/// </summary>
public class Icon : TemplatedControl
{
    private const double FallbackFontSize = 14;

    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));

    /// <summary>Icon edge length as a multiple of the inherited FontSize. Lets a given
    /// icon be daintier or larger while still scaling with the UI (default 1.15em).</summary>
    public static readonly StyledProperty<double> SizeFactorProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(SizeFactor), 1.15);

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public double SizeFactor
    {
        get => GetValue(SizeFactorProperty);
        set => SetValue(SizeFactorProperty, value);
    }

    static Icon()
    {
        FontSizeProperty.Changed.AddClassHandler<Icon>((icon, _) => icon.UpdateSize());
        SizeFactorProperty.Changed.AddClassHandler<Icon>((icon, _) => icon.UpdateSize());
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateSize();
    }

    private void UpdateSize()
    {
        double fs = double.IsNaN(FontSize) || FontSize <= 0 ? FallbackFontSize : FontSize;
        double size = fs * SizeFactor;
        Width = size;
        Height = size;
    }
}
