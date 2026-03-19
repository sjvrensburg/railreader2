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
        BionicReadingCheck.IsChecked = c.BionicReading;
        BionicFixationSlider.Value = c.BionicFixationPercent;
        BionicFadeSlider.Value = c.BionicFadeIntensity;
        EffectCombo.SelectedIndex = (int)c.ColourEffect;
        IntensitySlider.Value = c.ColourEffectIntensity;
        PixelSnappingCheck.IsChecked = c.PixelSnapping;
        LineFocusBlurCheck.IsChecked = c.LineFocusBlur;
        LineFocusBlurSlider.Value = c.LineFocusBlurIntensity;
        LineFocusPaddingSlider.Value = c.LineFocusPadding;
        AutoScrollLinePause.Value = (decimal)c.AutoScrollLinePauseMs;
        AutoScrollBlockPause.Value = (decimal)c.AutoScrollBlockPauseMs;
        JumpPercentage.Value = (decimal)c.JumpPercentage;

        LineHighlightTintCombo.ItemsSource = Enum.GetNames<LineHighlightTint>();
        LineHighlightTintCombo.SelectedIndex = (int)c.LineHighlightTint;
        LineHighlightOpacitySlider.Value = c.LineHighlightOpacity;

        BuildClassCheckboxes(_classItems, c.NavigableClasses,
            set => { vm.Config.NavigableClasses = set; vm.OnConfigChanged(); },
            NavigableClassesList);
        BuildClassCheckboxes(_centeringClassItems, c.CenteringClasses,
            set => { vm.Config.CenteringClasses = set; vm.OnConfigChanged(); },
            CenteringClassesList);
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
        c.JumpPercentage = (double)(JumpPercentage.Value ?? 25m);
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

    private void OnLineFocusBlurIntensityChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
        => OnSliderChanged(e, c => c.LineFocusBlurIntensity = LineFocusBlurSlider.Value);

    private void OnLineFocusPaddingChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
        => OnSliderChanged(e, c => c.LineFocusPadding = LineFocusPaddingSlider.Value);

    private void OnLineHighlightTintChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Vm is not { } vm || _loading) return;
        vm.Config.LineHighlightTint = (LineHighlightTint)LineHighlightTintCombo.SelectedIndex;
        vm.OnConfigChanged();
    }

    private void OnLineHighlightOpacityChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
        => OnSliderChanged(e, c => c.LineHighlightOpacity = LineHighlightOpacitySlider.Value);

    private void OnBionicReadingChanged(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || _loading) return;
        bool value = BionicReadingCheck.IsChecked == true;
        vm.Config.BionicReading = value; // update default for new documents
        if (vm.ActiveTab is { } tab) tab.BionicReading = value;
        vm.OnConfigChanged();
    }

    private void OnBionicFixationChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
        => OnSliderChanged(e, c => c.BionicFixationPercent = BionicFixationSlider.Value);

    private void OnBionicFadeChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
        => OnSliderChanged(e, c => c.BionicFadeIntensity = BionicFadeSlider.Value);

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
        vm.Config.LineFocusBlur = defaults.LineFocusBlur;
        vm.Config.LineFocusBlurIntensity = defaults.LineFocusBlurIntensity;
        if (vm.ActiveTab is { } resetTab)
        {
            resetTab.LineFocusBlur = defaults.LineFocusBlur;
            resetTab.BionicReading = defaults.BionicReading;
        }
        vm.Config.AutoScrollLinePauseMs = defaults.AutoScrollLinePauseMs;
        vm.Config.AutoScrollBlockPauseMs = defaults.AutoScrollBlockPauseMs;
        vm.Config.JumpPercentage = defaults.JumpPercentage;
        vm.Config.LineHighlightTint = defaults.LineHighlightTint;
        vm.Config.LineHighlightOpacity = defaults.LineHighlightOpacity;
        vm.Config.BionicReading = defaults.BionicReading;
        vm.Config.BionicFixationPercent = defaults.BionicFixationPercent;
        vm.Config.BionicFadeIntensity = defaults.BionicFadeIntensity;
        _loading = true;
        LoadFromConfig();
        _loading = false;
        vm.OnConfigChanged();
    }
}
