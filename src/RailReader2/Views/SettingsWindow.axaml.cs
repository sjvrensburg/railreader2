using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.ComponentModel;
using RailReader2.Models;
using RailReader2.Services;
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
    private ObservableCollection<NavigableClassItem> _classItems = [];

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

        // Navigable classes
        _classItems = [];
        for (int i = 0; i < LayoutConstants.LayoutClasses.Length; i++)
        {
            var item = new NavigableClassItem
            {
                Name = LayoutConstants.LayoutClasses[i],
                ClassId = i,
                IsChecked = c.NavigableClasses.Contains(i),
            };
            item.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(NavigableClassItem.IsChecked))
                    OnNavigableClassChanged();
            };
            _classItems.Add(item);
        }
        NavigableClassesList.ItemsSource = _classItems;
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
        vm.OnConfigChanged();
    }

    private void OnNavigableClassChanged()
    {
        if (Vm is not { } vm || _loading) return;
        vm.Config.NavigableClasses = new HashSet<int>(
            _classItems.Where(item => item.IsChecked).Select(item => item.ClassId));
        vm.OnConfigChanged();
    }

    private void OnMotionBlurChanged(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || _loading) return;
        vm.Config.MotionBlur = MotionBlurCheck.IsChecked == true;
        vm.OnConfigChanged();
    }

    private void OnBlurIntensityChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name == "Value" && Vm is { } vm && !_loading)
        {
            vm.Config.MotionBlurIntensity = BlurIntensitySlider.Value;
            vm.OnConfigChanged();
        }
    }

    private void OnSettingChanged(object? sender, NumericUpDownValueChangedEventArgs e) => SaveToConfig();
    private void OnEffectChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Vm is not { } vm || _loading) return;
        vm.Config.ColourEffect = (ColourEffect)(EffectCombo.SelectedIndex);
        SaveToConfig();
    }
    private void OnIntensityChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name == "Value") SaveToConfig();
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
        vm.Config.ColourEffect = defaults.ColourEffect;
        vm.Config.ColourEffectIntensity = defaults.ColourEffectIntensity;
        vm.Config.MotionBlur = defaults.MotionBlur;
        vm.Config.MotionBlurIntensity = defaults.MotionBlurIntensity;
        vm.Config.NavigableClasses = LayoutConstants.DefaultNavigableClasses();
        _loading = true;
        LoadFromConfig();
        _loading = false;
        vm.OnConfigChanged();
    }
}
