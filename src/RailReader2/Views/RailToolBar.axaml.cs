using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
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

    private Button? _autoScrollBtn;
    private Button? _jumpBtn;
    private Button? _focusBlurBtn;

    // Matching search overlay / toolbar button style
    private static readonly IBrush ActiveBg = new SolidColorBrush(Color.Parse("#0078D4"));
    private static readonly IBrush ActiveFg = Brushes.White;
    private static readonly IBrush InactiveBg = new SolidColorBrush(Color.Parse("#404040"));
    private static readonly IBrush InactiveFg = new SolidColorBrush(Color.Parse("#E0E0E0"));
    private static readonly IBrush InactiveBorder = new SolidColorBrush(Color.Parse("#606060"));

    public MainWindowViewModel? ViewModel { get; set; }

    public RailToolBar()
    {
        InitializeComponent();
        BuildButtons();
        BuildSliders();
    }

    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromRgb(180, 180, 180));

    private Button MakeToggleButton(string label, string tooltip, EventHandler<RoutedEventArgs> handler)
    {
        var btn = new Button
        {
            Content = label,
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            Width = 32,
            Height = 22,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 10,
            Background = InactiveBg,
            Foreground = InactiveFg,
            BorderBrush = InactiveBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
        };
        ToolTip.SetTip(btn, tooltip);
        btn.Click += handler;
        return btn;
    }

    private void BuildButtons()
    {
        _autoScrollBtn = MakeToggleButton("P", "Toggle auto-scroll (P)", (_, _) =>
        {
            if (ViewModel is { } vm)
            {
                vm.ToggleAutoScrollExclusive();
                SetJumpMode(vm.JumpMode);
                UpdateToggleStates();
            }
        });
        ButtonPanel.Children.Add(_autoScrollBtn);

        _jumpBtn = MakeToggleButton("J", "Toggle jump mode (J)", (_, _) =>
        {
            if (ViewModel is { } vm)
            {
                vm.ToggleJumpModeExclusive();
                SetJumpMode(vm.JumpMode);
                UpdateToggleStates();
            }
        });
        ButtonPanel.Children.Add(_jumpBtn);

        _focusBlurBtn = MakeToggleButton("F", "Toggle line focus blur (F)", (_, _) =>
        {
            if (ViewModel is { } vm)
            {
                vm.Config.LineFocusBlur = !vm.Config.LineFocusBlur;
                vm.OnConfigChanged();
                UpdateToggleStates();
            }
        });
        ButtonPanel.Children.Add(_focusBlurBtn);
    }

    public void UpdateToggleStates()
    {
        if (ViewModel is not { } vm) return;
        ApplyToggleStyle(_autoScrollBtn, vm.AutoScrollActive);
        ApplyToggleStyle(_jumpBtn, vm.JumpMode);
        ApplyToggleStyle(_focusBlurBtn, vm.Config.LineFocusBlur);
    }

    private static void ApplyToggleStyle(Button? btn, bool active)
    {
        if (btn is null) return;
        btn.Background = active ? ActiveBg : InactiveBg;
        btn.Foreground = active ? ActiveFg : InactiveFg;
        btn.BorderBrush = active ? ActiveBg : InactiveBorder;
    }

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
