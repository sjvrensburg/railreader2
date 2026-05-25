using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Core.Vlm.OpenAI;
using RailReader2.Services;
using RailReader2.ViewModels;

namespace RailReader2.Views;

public partial class NavigableRoleItem : ObservableObject
{
    [ObservableProperty] private bool _isChecked;
    public string Label { get; init; } = "";
    public BlockRole Role { get; init; }
}

public partial class SettingsWindow : Window
{
    private bool _loading = true;
    private readonly ObservableCollection<NavigableRoleItem> _roleItems = [];
    private readonly ObservableCollection<NavigableRoleItem> _centeringRoleItems = [];
    private CustomLayoutModelConfig _customModel = new();

    /// <summary>
    /// Roles offered in the settings UI. Excludes <see cref="BlockRole.Unknown"/>
    /// (sentinel; never user-selected) and roles that aren't meaningful to
    /// navigate — page furniture (<see cref="BlockRole.Header"/>,
    /// <see cref="BlockRole.Footer"/>, <see cref="BlockRole.PageNumber"/>,
    /// <see cref="BlockRole.Decoration"/>). Order is reading-comfort
    /// importance.
    /// </summary>
    private static readonly (BlockRole Role, string Label)[] s_userVisibleRoles =
    [
        (BlockRole.Text,        "Paragraph text"),
        (BlockRole.Title,       "Document title"),
        (BlockRole.Heading,     "Section heading"),
        (BlockRole.Caption,     "Figure / table caption"),
        (BlockRole.Aside,       "Aside / sidebar"),
        (BlockRole.DisplayMath, "Display equation"),
        (BlockRole.Algorithm,   "Algorithm / pseudocode"),
        (BlockRole.Table,       "Table"),
        (BlockRole.Figure,      "Figure / image"),
        (BlockRole.Chart,       "Chart / graph"),
        (BlockRole.Footnote,    "Footnote"),
        (BlockRole.Reference,   "Reference / bibliography"),
    ];

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
        var c = vm.AppConfig;
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

        BuildRoleCheckboxes(_roleItems, c.NavigableRoles,
            set => { vm.AppConfig.NavigableRoles = set; vm.OnConfigChanged(); },
            NavigableRolesList);
        BuildRoleCheckboxes(_centeringRoleItems, c.CenteringRoles,
            set => { vm.AppConfig.CenteringRoles = set; vm.OnConfigChanged(); },
            CenteringRolesList);

        VlmEndpoint.Text = c.VlmEndpoint ?? "";
        VlmModelName.Text = c.VlmModel ?? "";
        VlmApiKey.Text = c.VlmApiKey ?? "";
        VlmStructuredOutput.IsChecked = c.VlmStructuredOutput;

        _customModel = CustomLayoutModelConfig.Load();
        CustomModelEnabled.IsChecked = _customModel.Enabled;
        CustomModelPath.Text = _customModel.ModelPath ?? "";
        CustomModelMappingPath.Text = _customModel.MappingPath ?? "";
        UpdateCustomModelStatus();
        PopulateBuiltinAnalyzerCombo();
    }

    /// <summary>
    /// Populates the analyzer dropdown. The Heron option is shown either way,
    /// but if no Heron .onnx is found on any probe path we surface that in the
    /// status line below the combo and roll back the selection to PP if the
    /// user tries to pick it (a properly-disabled item would silently swallow
    /// the click and confuse users).
    /// </summary>
    private void PopulateBuiltinAnalyzerCombo()
    {
        var items = new List<BuiltinAnalyzerItem>
        {
            new(BuiltinAnalyzer.PpDocLayoutV3, "PP-DocLayoutV3 (default, bundled)"),
            new(BuiltinAnalyzer.Heron,         "Docling Heron"),
        };
        BuiltinAnalyzerCombo.ItemsSource = items;
        BuiltinAnalyzerCombo.DisplayMemberBinding = new Avalonia.Data.Binding(nameof(BuiltinAnalyzerItem.Label));
        BuiltinAnalyzerCombo.SelectedIndex = (int)_customModel.BuiltinAnalyzer;
        UpdateBuiltinAnalyzerStatus();
    }

    private void UpdateBuiltinAnalyzerStatus()
    {
        if (_customModel.BuiltinAnalyzer == BuiltinAnalyzer.Heron)
        {
            var heron = HeronModelLocator.FindModelPath();
            BuiltinAnalyzerStatus.Text = heron != null
                ? $"Heron model: {heron}  Restart to apply."
                : $"Heron model not found ({HeronModelLocator.FileName}). See docs/heron-layout-model.md to download it; the app will fall back to PP-DocLayoutV3 until then.";
        }
        else
        {
            BuiltinAnalyzerStatus.Text = "Using PP-DocLayoutV3.";
        }
    }

    private void OnBuiltinAnalyzerChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (BuiltinAnalyzerCombo.SelectedItem is BuiltinAnalyzerItem item)
        {
            _customModel.BuiltinAnalyzer = item.Value;
            _customModel.Save();
            UpdateBuiltinAnalyzerStatus();
        }
    }

    private sealed record BuiltinAnalyzerItem(BuiltinAnalyzer Value, string Label);

    private void SaveToConfig()
    {
        if (Vm is not { } vm || _loading) return;
        var c = vm.AppConfig;
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

    private void BuildRoleCheckboxes(
        ObservableCollection<NavigableRoleItem> items, IReadOnlySet<BlockRole> activeSet,
        Action<HashSet<BlockRole>> onChanged, ItemsControl target)
    {
        items.Clear();
        foreach (var (role, label) in s_userVisibleRoles)
        {
            var item = new NavigableRoleItem
            {
                Label = label,
                Role = role,
                IsChecked = activeSet.Contains(role),
            };
            item.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(NavigableRoleItem.IsChecked) && !_loading && Vm is not null)
                    onChanged(items.Where(x => x.IsChecked).Select(x => x.Role).ToHashSet());
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
        vm.AppConfig.MotionBlur = MotionBlurCheck.IsChecked == true;
        vm.OnConfigChanged();
    }

    private void OnSliderChanged(Avalonia.AvaloniaPropertyChangedEventArgs e, Action<AppConfig> apply)
    {
        if (e.Property.Name != "Value" || Vm is not { } vm || _loading) return;
        apply(vm.AppConfig);
        vm.OnConfigChanged();
    }

    private void OnBlurIntensityChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
        => OnSliderChanged(e, c => c.MotionBlurIntensity = BlurIntensitySlider.Value);

    private void OnPixelSnappingChanged(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || _loading) return;
        vm.AppConfig.PixelSnapping = PixelSnappingCheck.IsChecked == true;
        vm.OnConfigChanged();
    }

    private void OnLineFocusBlurChanged(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || _loading) return;
        bool value = LineFocusBlurCheck.IsChecked == true;
        vm.AppConfig.LineFocusBlur = value; // update default for new documents
        if (vm.ActiveTab is { } tab) tab.LineFocusBlur = value;
        vm.OnConfigChanged();
    }

    private void OnMarginCroppingChanged(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || _loading) return;
        bool value = MarginCroppingCheck.IsChecked == true;
        vm.AppConfig.MarginCropping = value;
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
        vm.AppConfig.LineHighlightEnabled = value;
        if (vm.ActiveTab is { } tab) tab.LineHighlightEnabled = value;
        vm.OnConfigChanged();
    }

    private void OnLineHighlightTintChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Vm is not { } vm || _loading) return;
        vm.AppConfig.LineHighlightTint = (LineHighlightTint)LineHighlightTintCombo.SelectedIndex;
        vm.OnConfigChanged();
    }

    private void OnLineHighlightOpacityChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
        => OnSliderChanged(e, c => c.LineHighlightOpacity = LineHighlightOpacitySlider.Value);

    private void OnAutoScrollTriggerChanged(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || _loading) return;
        vm.AppConfig.AutoScrollTriggerEnabled = AutoScrollTriggerCheck.IsChecked == true;
        vm.OnConfigChanged();
    }

    private void OnSettingChanged(object? sender, NumericUpDownValueChangedEventArgs e) => SaveToConfig();
    private void OnEffectChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Vm is not { } vm || _loading) return;
        var effect = (ColourEffect)EffectCombo.SelectedIndex;
        vm.AppConfig.ColourEffect = effect;
        vm.Controller.SetColourEffect(effect);
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
        vm.AppConfig.RailZoomThreshold = defaults.RailZoomThreshold;
        vm.AppConfig.SnapDurationMs = defaults.SnapDurationMs;
        vm.AppConfig.ScrollSpeedStart = defaults.ScrollSpeedStart;
        vm.AppConfig.ScrollSpeedMax = defaults.ScrollSpeedMax;
        vm.AppConfig.ScrollRampTime = defaults.ScrollRampTime;
        vm.AppConfig.AnalysisLookaheadPages = defaults.AnalysisLookaheadPages;
        vm.AppConfig.UiFontScale = defaults.UiFontScale;
        vm.SetDarkMode(defaults.DarkMode);
        vm.AppConfig.ColourEffect = defaults.ColourEffect;
        vm.Controller.SetColourEffect(defaults.ColourEffect);
        vm.AppConfig.ColourEffectIntensity = defaults.ColourEffectIntensity;
        vm.AppConfig.MotionBlur = defaults.MotionBlur;
        vm.AppConfig.MotionBlurIntensity = defaults.MotionBlurIntensity;
        vm.AppConfig.NavigableRoles = new HashSet<BlockRole>(DefaultRoleSets.Navigable);
        vm.AppConfig.CenteringRoles = new HashSet<BlockRole>(DefaultRoleSets.Centering);
        vm.AppConfig.PixelSnapping = defaults.PixelSnapping;
        vm.AppConfig.MarginCropping = defaults.MarginCropping;
        vm.AppConfig.MinimapWidth = defaults.MinimapWidth;
        vm.AppConfig.MinimapHeight = defaults.MinimapHeight;
        vm.AppConfig.MinimapMarginRight = defaults.MinimapMarginRight;
        vm.AppConfig.MinimapMarginBottom = defaults.MinimapMarginBottom;
        vm.AppConfig.LineFocusBlur = defaults.LineFocusBlur;
        vm.AppConfig.LineFocusBlurIntensity = defaults.LineFocusBlurIntensity;
        if (vm.ActiveTab is { } resetTab)
        {
            resetTab.LineFocusBlur = defaults.LineFocusBlur;
            resetTab.LineHighlightEnabled = defaults.LineHighlightEnabled;
            resetTab.MarginCropping = defaults.MarginCropping;
        }
        vm.AppConfig.AutoScrollLinePauseMs = defaults.AutoScrollLinePauseMs;
        vm.AppConfig.AutoScrollBlockPauseMs = defaults.AutoScrollBlockPauseMs;
        vm.AppConfig.AutoScrollEquationPauseMs = defaults.AutoScrollEquationPauseMs;
        vm.AppConfig.AutoScrollHeaderPauseMs = defaults.AutoScrollHeaderPauseMs;
        vm.AppConfig.AutoScrollTriggerEnabled = defaults.AutoScrollTriggerEnabled;
        vm.AppConfig.AutoScrollTriggerDelayMs = defaults.AutoScrollTriggerDelayMs;
        vm.AppConfig.JumpPercentage = defaults.JumpPercentage;
        vm.AppConfig.LineHighlightEnabled = defaults.LineHighlightEnabled;
        vm.AppConfig.LineHighlightTint = defaults.LineHighlightTint;
        vm.AppConfig.LineHighlightOpacity = defaults.LineHighlightOpacity;
        vm.AppConfig.VlmEndpoint = defaults.VlmEndpoint;
        vm.AppConfig.VlmModel = defaults.VlmModel;
        vm.AppConfig.VlmApiKey = defaults.VlmApiKey;
        vm.AppConfig.VlmStructuredOutput = defaults.VlmStructuredOutput;
        _loading = true;
        LoadFromConfig();
        _loading = false;
        vm.OnConfigChanged();
    }

    private void OnVlmTextChanged(object? sender, TextChangedEventArgs e) => SaveToConfig();

    private void OnVlmCheckChanged(object? sender, RoutedEventArgs e) => SaveToConfig();

    // --- Custom layout model ---

    private void OnCustomModelEnabledChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _customModel.Enabled = CustomModelEnabled.IsChecked == true;
        _customModel.Save();
        UpdateCustomModelStatus();
    }

    private async void OnBrowseCustomModel(object? sender, RoutedEventArgs e)
    {
        var path = await PickFile("Select ONNX model", "ONNX", new[] { "onnx" });
        if (path == null) return;
        CustomModelPath.Text = path;
        _customModel.ModelPath = path;
        _customModel.Save();
        UpdateCustomModelStatus();
    }

    private async void OnBrowseCustomModelMapping(object? sender, RoutedEventArgs e)
    {
        var path = await PickFile("Select class-mapping JSON", "JSON", new[] { "json" });
        if (path == null) return;
        CustomModelMappingPath.Text = path;
        _customModel.MappingPath = path;
        _customModel.Save();
        UpdateCustomModelStatus();
    }

    private async Task<string?> PickFile(string title, string typeLabel, string[] extensions)
    {
        var sp = StorageProvider;
        if (sp == null) return null;
        var picks = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(typeLabel) { Patterns = extensions.Select(x => "*." + x).ToArray() },
            },
        });
        if (picks.Count == 0) return null;
        return picks[0].TryGetLocalPath();
    }

    private void UpdateCustomModelStatus()
    {
        if (!_customModel.Enabled)
        {
            CustomModelStatus.Text = "Using bundled PP-DocLayoutV3.";
            return;
        }

        if (string.IsNullOrWhiteSpace(_customModel.ModelPath) || string.IsNullOrWhiteSpace(_customModel.MappingPath))
        {
            CustomModelStatus.Text = "Select both an ONNX file and a class-mapping JSON file.";
            return;
        }

        if (!File.Exists(_customModel.ModelPath))
        {
            CustomModelStatus.Text = $"ONNX file not found: {_customModel.ModelPath}";
            return;
        }

        var (caps, error) = CustomLayoutModelLoader.LoadCapabilities(_customModel.MappingPath);
        if (error != null)
        {
            CustomModelStatus.Text = $"Mapping invalid: {error}";
            return;
        }

        CustomModelStatus.Text = $"OK — {caps!.Classes.Count} classes, input size {caps.InputSize}px. Restart RailReader2 to apply.";
    }

    // --- VLM ---

    private async void OnTestVlmConnection(object? sender, RoutedEventArgs e)
    {
        SaveToConfig();
        var config = Vm?.AppConfig;
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
            IVlmService vlm = new OpenAIVlmClient();
            var result = await vlm.TestConnectionAsync(VlmEndpointConfig.FromCoreSettings(config.ToCoreSettings()));
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
