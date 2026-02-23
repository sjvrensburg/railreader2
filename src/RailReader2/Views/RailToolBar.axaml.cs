using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using RailReader2.ViewModels;

namespace RailReader2.Views;

public partial class RailToolBar : UserControl
{
    private Slider? _speedSlider;
    private Slider? _blurSlider;

    public MainWindowViewModel? ViewModel { get; set; }

    public RailToolBar()
    {
        InitializeComponent();
        BuildSliders();
    }

    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromRgb(180, 180, 180));

    private void BuildSliders()
    {
        // Speed slider
        SliderPanel.Children.Add(MakeLabel("Spd"));

        _speedSlider = new Slider
        {
            Orientation = Orientation.Vertical,
            Minimum = 5,
            Maximum = 80,
            Value = 30,
            Height = 120,
            Width = 28,
            HorizontalAlignment = HorizontalAlignment.Center,
            TickFrequency = 5,
        };
        ToolTip.SetTip(_speedSlider, "Scroll speed ([ / ] keys)");
        _speedSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && ViewModel is { } vm)
            {
                vm.Config.ScrollSpeedMax = _speedSlider.Value;
                vm.OnConfigChanged();
            }
        };
        SliderPanel.Children.Add(_speedSlider);

        // Blur slider
        SliderPanel.Children.Add(MakeLabel("Blur"));

        _blurSlider = new Slider
        {
            Orientation = Orientation.Vertical,
            Minimum = 0,
            Maximum = 1,
            Value = 0.33,
            Height = 120,
            Width = 28,
            HorizontalAlignment = HorizontalAlignment.Center,
            TickFrequency = 0.1,
        };
        ToolTip.SetTip(_blurSlider, "Motion blur intensity (Shift+[ / Shift+] keys)");
        _blurSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && ViewModel is { } vm)
            {
                vm.Config.MotionBlurIntensity = _blurSlider.Value;
                vm.OnConfigChanged();
            }
        };
        SliderPanel.Children.Add(_blurSlider);
    }

    private static TextBlock MakeLabel(string text) => new()
    {
        Text = text,
        // FontSize not set — inherits from Window.FontSize which respects UiFontScale
        Foreground = LabelBrush,
        HorizontalAlignment = HorizontalAlignment.Center,
        TextAlignment = TextAlignment.Center,
    };

    /// <summary>
    /// Syncs slider values from the current config. Call after ViewModel is set.
    /// </summary>
    public void SyncFromConfig()
    {
        if (ViewModel is not { } vm) return;
        if (_speedSlider is not null)
            _speedSlider.Value = vm.Config.ScrollSpeedMax;
        if (_blurSlider is not null)
            _blurSlider.Value = vm.Config.MotionBlurIntensity;
    }

    /// <summary>Adjust scroll speed by a delta. Used by keyboard shortcuts.</summary>
    public void AdjustSpeed(double delta)
    {
        if (_speedSlider is not null)
            _speedSlider.Value = Math.Clamp(_speedSlider.Value + delta, _speedSlider.Minimum, _speedSlider.Maximum);
    }

    /// <summary>Adjust blur intensity by a delta. Used by keyboard shortcuts.</summary>
    public void AdjustBlur(double delta)
    {
        if (_blurSlider is not null)
            _blurSlider.Value = Math.Clamp(_blurSlider.Value + delta, _blurSlider.Minimum, _blurSlider.Maximum);
    }
}
