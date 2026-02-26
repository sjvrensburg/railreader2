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
    private TextBlock? _speedLabel;
    private bool _jumpMode;

    public MainWindowViewModel? ViewModel { get; set; }

    public RailToolBar()
    {
        InitializeComponent();
        BuildSliders();
    }

    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromRgb(180, 180, 180));

    private void BuildSliders()
    {
        // Speed / Jump distance slider
        _speedLabel = MakeLabel("Spd");
        SliderPanel.Children.Add(_speedLabel);

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
                if (_jumpMode)
                    vm.Config.JumpPercentage = _speedSlider.Value;
                else
                    vm.Config.ScrollSpeedMax = _speedSlider.Value;
                vm.OnSliderChanged();
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
                vm.OnSliderChanged();
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
    /// Switches the speed slider between scroll speed and jump distance modes.
    /// </summary>
    public void SetJumpMode(bool jumpMode)
    {
        if (_jumpMode == jumpMode) return;
        _jumpMode = jumpMode;

        if (_speedSlider is null || _speedLabel is null || ViewModel is not { } vm) return;

        _speedLabel.Text = jumpMode ? "Jmp" : "Spd";
        _speedSlider.Value = jumpMode ? vm.Config.JumpPercentage : vm.Config.ScrollSpeedMax;
        ToolTip.SetTip(_speedSlider, jumpMode
            ? "Jump distance % ([ / ] keys)"
            : "Scroll speed ([ / ] keys)");
    }

    /// <summary>
    /// Syncs slider values from the current config. Call after ViewModel is set.
    /// </summary>
    public void SyncFromConfig()
    {
        if (ViewModel is not { } vm) return;
        if (_speedSlider is not null)
            _speedSlider.Value = _jumpMode ? vm.Config.JumpPercentage : vm.Config.ScrollSpeedMax;
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
