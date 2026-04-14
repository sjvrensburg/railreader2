using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.ComponentModel;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader2.ViewModels;

namespace RailReader2.Views;

public partial class NavigableClassItem : ObservableObject
{
    [ObservableProperty] private bool _isChecked;
    public string Name { get; init; } = "";
    public int ClassId { get; init; }
}

public partial class SettingsWindow : Window
{
    private bool _loading = true;
    private readonly ObservableCollection<NavigableClassItem> _classItems = [];
    private readonly ObservableCollection<NavigableClassItem> _centeringClassItems = [];

    public SettingsWindow()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        LoadFromConfig();
        _loading = false;
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    private void LoadFromConfig()
    {
        if (Vm is not { } vm) return;
        var c = vm.Config;
        FontScale.Value = (decimal)c.UiFontScale;
        DarkModeCheck.IsChecked = c.DarkMode;
        MotionBlurCheck.IsChecked = c.MotionBlur;
        BlurIntensitySlider.Value = c.MotionBlurIntensity;
        ZoomThreshold.Value = (decimal)c.RailZoomThreshold;
        SnapDuration.Value = (decimal)c.SnapDurationMs;
        ScrollStart.Value = (decimal)c.ScrollSpeedStart;
        ScrollMax.Value = (decimal)c.ScrollSpeedMax;
        RampTime.Value = (decimal)c.ScrollRampTime;
        Lookahead.Value = c.AnalysisLookaheadPages;
        EffectCombo.SelectedIndex = (int)c.ColourEffect;
        IntensitySlider.Value = c.ColourEffectIntensity;
        PixelSnappingCheck.IsChecked = c.PixelSnapping;
        MarginCroppingCheck.IsChecked = c.MarginCropping;
        LineFocusBlurCheck.IsChecked = c.LineFocusBlur;
        LineFocusBlurSlider.Value = c.LineFocusBlurIntensity;
        LinePaddingSlider.Value = c.LinePadding;
        AutoScrollLinePause.Value = (decimal)c.AutoScrollLinePauseMs;
        AutoScrollBlockPause.Value = (decimal)c.AutoScrollBlockPauseMs;
        AutoScrollEquationPause.Value = (decimal)c.AutoScrollEquationPauseMs;
        AutoScrollHeaderPause.Value = (decimal)c.AutoScrollHeaderPauseMs;
        AutoScrollTriggerCheck.IsChecked = c.AutoScrollTriggerEnabled;
        AutoScrollTriggerDelay.Value = (decimal)c.AutoScrollTriggerDelayMs;
        JumpPercentage.Value = (decimal)c.JumpPercentage;

        LineHighlightCheck.IsChecked = c.LineHighlightEnabled;
        LineHighlightTintCombo.ItemsSource = Enum.GetNames<LineHighlightTint>();
        LineHighlightTintCombo.SelectedIndex = (int)c.LineHighlightTint;
        LineHighlightOpacitySlider.Value = c.LineHighlightOpacity;

        BuildClassCheckboxes(_classItems, c.NavigableClasses,
            set => { vm.Config.NavigableClasses = set; vm.OnConfigChanged(); },
            NavigableClassesList);
        BuildClassCheckboxes(_centeringClassItems, c.CenteringClasses,
            set => { vm.Config.CenteringClasses = set; vm.OnConfigChanged(); },
            CenteringClassesList);

        VlmEndpoint.Text = c.VlmEndpoint ?? "";
        VlmModelName.Text = c.VlmModel ?? "";
        VlmApiKey.Text = c.VlmApiKey ?? "";
        VlmStructuredOutput.IsChecked = c.VlmStructuredOutput;
    }

    private void SaveToConfig()
    {
        if (Vm is not { } vm || _loading) return;
        var c = vm.Config;
        c.UiFontScale = (float)(FontScale.Value ?? 1.0m);
        c.RailZoomThreshold = (double)(ZoomThreshold.Value ?? 3.0m);
        c.SnapDurationMs = (double)(SnapDuration.Value ?? 300m);
        c.ScrollSpeedStart = (double)(ScrollStart.Value ?? 10m);
        c.ScrollSpeedMax = (double)(ScrollMax.Value ?? 50m);
        c.ScrollRampTime = (double)(RampTime.Value ?? 1.5m);
        c.AnalysisLookaheadPages = (int)(Lookahead.Value ?? 2m);
        c.ColourEffectIntensity = IntensitySlider.Value;
        c.AutoScrollLinePauseMs = (double)(AutoScrollLinePause.Value ?? 400m);
        c.AutoScrollBlockPauseMs = (double)(AutoScrollBlockPause.Value ?? 600m);
        c.AutoScrollEquationPauseMs = (double)(AutoScrollEquationPause.Value ?? 600m);
        c.AutoScrollHeaderPauseMs = (double)(AutoScrollHeaderPause.Value ?? 600m);
        c.AutoScrollTriggerDelayMs = (double)(AutoScrollTriggerDelay.Value ?? 2000m);
        c.JumpPercentage = (double)(JumpPercentage.Value ?? 25m);
        c.VlmEndpoint = string.IsNullOrWhiteSpace(VlmEndpoint.Text) ? null : VlmEndpoint.Text.Trim();
        c.VlmModel = string.IsNullOrWhiteSpace(VlmModelName.Text) ? null : VlmModelName.Text.Trim();
        c.VlmApiKey = string.IsNullOrWhiteSpace(VlmApiKey.Text) ? null : VlmApiKey.Text.Trim();
        c.VlmStructuredOutput = VlmStructuredOutput.IsChecked ?? false;
        vm.OnConfigChanged();
    }

    private void BuildClassCheckboxes(
        ObservableCollection<NavigableClassItem> items, HashSet<int> activeSet,
        Action<HashSet<int>> onChanged, ItemsControl target)
    {
        items.Clear();
        for (int i = 0; i < LayoutConstants.LayoutClasses.Length; i++)
        {
            var item = new NavigableClassItem
            {
                Name = LayoutConstants.LayoutClasses[i],
                ClassId = i,
                IsChecked = activeSet.Contains(i),
            };
            item.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(NavigableClassItem.IsChecked) && !_loading && Vm is not null)
                    onChanged(items.Where(x => x.IsChecked).Select(x => x.ClassId).ToHashSet());
            };
            items.Add(item);
        }
        target.ItemsSource = items;
    }

    private void OnDarkModeChanged(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || _loading) return;
        vm.SetDarkMode(DarkModeCheck.IsChecked == true);
    }

    private void OnMotionBlurChanged(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || _loading) return;
        vm.Config.MotionBlur = MotionBlurCheck.IsChecked == true;
        vm.OnConfigChanged();
    }

    private void OnSliderChanged(Avalonia.AvaloniaPropertyChangedEventArgs e, Action<AppConfig> apply)
    {
        if (e.Property.Name != "Value" || Vm is not { } vm || _loading) return;
        apply(vm.Config);
        vm.OnConfigChanged();
    }

    private void OnBlurIntensityChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
        => OnSliderChanged(e, c => c.MotionBlurIntensity = BlurIntensitySlider.Value);

    private void OnPixelSnappingChanged(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || _loading) return;
        vm.Config.PixelSnapping = PixelSnappingCheck.IsChecked == true;
        vm.OnConfigChanged();
    }

    private void OnLineFocusBlurChanged(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || _loading) return;
        bool value = LineFocusBlurCheck.IsChecked == true;
        vm.Config.LineFocusBlur = value; // update default for new documents
        if (vm.ActiveTab is { } tab) tab.LineFocusBlur = value;
        vm.OnConfigChanged();
    }

    private void OnMarginCroppingChanged(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || _loading) return;
        bool value = MarginCroppingCheck.IsChecked == true;
        vm.Config.MarginCropping = value;
        vm.ApplyMarginCropping(value);
        vm.OnConfigChanged();
    }

    private void OnLineFocusBlurIntensityChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
        => OnSliderChanged(e, c => c.LineFocusBlurIntensity = LineFocusBlurSlider.Value);

    private void OnLinePaddingChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
        => OnSliderChanged(e, c => c.LinePadding = LinePaddingSlider.Value);

    private void OnLineHighlightEnabledChanged(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || _loading) return;
        bool value = LineHighlightCheck.IsChecked == true;
        vm.Config.LineHighlightEnabled = value;
        if (vm.ActiveTab is { } tab) tab.LineHighlightEnabled = value;
        vm.OnConfigChanged();
    }

    private void OnLineHighlightTintChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Vm is not { } vm || _loading) return;
        vm.Config.LineHighlightTint = (LineHighlightTint)LineHighlightTintCombo.SelectedIndex;
        vm.OnConfigChanged();
    }

    private void OnLineHighlightOpacityChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
        => OnSliderChanged(e, c => c.LineHighlightOpacity = LineHighlightOpacitySlider.Value);

    private void OnAutoScrollTriggerChanged(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || _loading) return;
        vm.Config.AutoScrollTriggerEnabled = AutoScrollTriggerCheck.IsChecked == true;
        vm.OnConfigChanged();
    }

    private void OnSettingChanged(object? sender, NumericUpDownValueChangedEventArgs e) => SaveToConfig();
    private void OnEffectChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Vm is not { } vm || _loading) return;
        vm.Controller.SetGlobalColourEffect((ColourEffect)EffectCombo.SelectedIndex);
        SaveToConfig();
    }
    private void OnIntensityChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name != "Value") return;
        SaveToConfig();
    }

    private void OnResetDefaults(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var defaults = new AppConfig();
        vm.Config.RailZoomThreshold = defaults.RailZoomThreshold;
        vm.Config.SnapDurationMs = defaults.SnapDurationMs;
        vm.Config.ScrollSpeedStart = defaults.ScrollSpeedStart;
        vm.Config.ScrollSpeedMax = defaults.ScrollSpeedMax;
        vm.Config.ScrollRampTime = defaults.ScrollRampTime;
        vm.Config.AnalysisLookaheadPages = defaults.AnalysisLookaheadPages;
        vm.Config.UiFontScale = defaults.UiFontScale;
        vm.SetDarkMode(defaults.DarkMode);
        vm.Controller.SetGlobalColourEffect(defaults.ColourEffect);
        vm.Config.ColourEffectIntensity = defaults.ColourEffectIntensity;
        vm.Config.MotionBlur = defaults.MotionBlur;
        vm.Config.MotionBlurIntensity = defaults.MotionBlurIntensity;
        vm.Config.NavigableClasses = LayoutConstants.DefaultNavigableClasses();
        vm.Config.CenteringClasses = LayoutConstants.DefaultCenteringClasses();
        vm.Config.PixelSnapping = defaults.PixelSnapping;
        vm.Config.MarginCropping = defaults.MarginCropping;
        vm.Config.MinimapWidth = defaults.MinimapWidth;
        vm.Config.MinimapHeight = defaults.MinimapHeight;
        vm.Config.MinimapMarginRight = defaults.MinimapMarginRight;
        vm.Config.MinimapMarginBottom = defaults.MinimapMarginBottom;
        vm.Config.LineFocusBlur = defaults.LineFocusBlur;
        vm.Config.LineFocusBlurIntensity = defaults.LineFocusBlurIntensity;
        if (vm.ActiveTab is { } resetTab)
        {
            resetTab.LineFocusBlur = defaults.LineFocusBlur;
            resetTab.LineHighlightEnabled = defaults.LineHighlightEnabled;
            resetTab.MarginCropping = defaults.MarginCropping;
        }
        vm.Config.AutoScrollLinePauseMs = defaults.AutoScrollLinePauseMs;
        vm.Config.AutoScrollBlockPauseMs = defaults.AutoScrollBlockPauseMs;
        vm.Config.AutoScrollEquationPauseMs = defaults.AutoScrollEquationPauseMs;
        vm.Config.AutoScrollHeaderPauseMs = defaults.AutoScrollHeaderPauseMs;
        vm.Config.AutoScrollTriggerEnabled = defaults.AutoScrollTriggerEnabled;
        vm.Config.AutoScrollTriggerDelayMs = defaults.AutoScrollTriggerDelayMs;
        vm.Config.JumpPercentage = defaults.JumpPercentage;
        vm.Config.LineHighlightEnabled = defaults.LineHighlightEnabled;
        vm.Config.LineHighlightTint = defaults.LineHighlightTint;
        vm.Config.LineHighlightOpacity = defaults.LineHighlightOpacity;
        vm.Config.VlmEndpoint = defaults.VlmEndpoint;
        vm.Config.VlmModel = defaults.VlmModel;
        vm.Config.VlmApiKey = defaults.VlmApiKey;
        vm.Config.VlmStructuredOutput = defaults.VlmStructuredOutput;
        _loading = true;
        LoadFromConfig();
        _loading = false;
        vm.OnConfigChanged();
    }

    private void OnVlmTextChanged(object? sender, TextChangedEventArgs e) => SaveToConfig();

    private void OnVlmCheckChanged(object? sender, RoutedEventArgs e) => SaveToConfig();

    private async void OnTestVlmConnection(object? sender, RoutedEventArgs e)
    {
        SaveToConfig();
        var config = Vm?.Config;
        if (config is null) return;

        if (string.IsNullOrWhiteSpace(config.VlmEndpoint))
        {
            VlmTestResult.Text = "Enter an endpoint URL first.";
            return;
        }

        VlmTestResult.Text = "Testing...";
        TestVlmButton.IsEnabled = false;
        try
        {
            var result = await VlmService.TestConnectionAsync(config);
            VlmTestResult.Text = result ?? "Connection successful!";
        }
        catch (Exception ex)
        {
            VlmTestResult.Text = $"Error: {ex.Message}";
        }
        finally
        {
            TestVlmButton.IsEnabled = true;
        }
    }
}
