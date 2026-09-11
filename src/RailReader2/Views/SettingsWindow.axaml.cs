using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using RailReader.Core;
using RailReader.Core.Analysis.WebGpu;
using RailReader.Core.Models;
using RailReader.Core.Ocr.RapidOcr;
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
    private readonly ObservableCollection<NavigableRoleItem> _stopRoleItems = [];
    private CustomLayoutModelConfig _customModel = new();
    private CancellationTokenSource? _downloadCts;
    private CancellationTokenSource? _gpuDownloadCts;
    private OcrPreferences? _ocrPrefs;
    private CancellationTokenSource? _ocrDownloadCts;

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

    /// <summary>Selects the tab whose <c>TabItem.Header</c> matches <paramref name="header"/>
    /// (e.g. "OCR"). Call any time after construction — the tabs exist as soon as
    /// <c>InitializeComponent</c> has run. No-op if no tab matches.</summary>
    public void SelectTab(string header)
    {
        foreach (var item in MainTabControl.Items)
        {
            if (item is TabItem { Header: string h } ti && h == header)
            {
                MainTabControl.SelectedItem = ti;
                return;
            }
        }
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
        AnalysisWindow.Value = c.BackgroundAnalysisWindowPages;
        PageCacheRadius.Value = c.PageCacheRadius;
        EffectCombo.SelectedIndex = (int)c.ColourEffect;
        IntensitySlider.Value = c.ColourEffectIntensity;
        RenderQualityCombo.SelectedIndex = (int)c.RenderQuality;
        CustomMaxDpi.Value = c.CustomMaxRenderDpi;
        CustomTierStep.Value = c.CustomRenderTierStep;
        UpdateCustomRenderPanel(c.RenderQuality);
        ContinuousScrollCheck.IsChecked = c.ContinuousScroll;
        PixelSnappingCheck.IsChecked = c.PixelSnapping;
        MarginCroppingCheck.IsChecked = c.MarginCropping;
        LineFocusBlurCheck.IsChecked = c.LineFocusBlur;
        LineFocusBlurSlider.Value = c.LineFocusBlurIntensity;
        LinePaddingSlider.Value = c.LinePadding;
        AutoScrollLinePause.Value = (decimal)c.AutoScrollLinePauseMs;
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
        BuildRoleCheckboxes(_stopRoleItems, c.AutoScrollStopClasses,
            set => { vm.AppConfig.AutoScrollStopClasses = set; vm.OnConfigChanged(); },
            StopRolesList);

        VlmEndpoint.Text = c.VlmEndpoint ?? "";
        VlmModelName.Text = c.VlmModel ?? "";
        VlmApiKey.Text = c.VlmApiKey ?? "";
        VlmStructuredOutput.IsChecked = c.VlmStructuredOutput;

        _customModel = CustomLayoutModelConfig.Load();
        _ocrPrefs = OcrPreferences.Load();
        CustomModelEnabled.IsChecked = _customModel.Enabled;
        CustomModelPath.Text = _customModel.ModelPath ?? "";
        CustomModelMappingPath.Text = _customModel.MappingPath ?? "";
        UpdateCustomModelStatus();
        PopulateBuiltinAnalyzerCombo();
        GpuAccelerationCheck.IsChecked = _customModel.Accelerator == AcceleratorPreference.Gpu;

        // Layout and OCR GPU acceleration are mutually exclusive (see OcrPreferences.Accelerator's
        // doc comment) — reconcile a hand-edited sidecar that somehow has both set to Gpu before
        // either status block renders. Layout wins the tie, matching MainWindowViewModel's startup
        // reconciliation. Uses the real WouldLayoutUseGpu (not the raw Accelerator flag) so this
        // reconciliation can't itself be fooled by a config shape that never reaches GPU anyway —
        // BuiltinAnalyzerCombo/CustomModelEnabled above are already populated, so this reads the
        // same on-disk state WouldLayoutUseGpu resolves.
        if (WouldLayoutUseGpu() && _ocrPrefs.Accelerator == AcceleratorPreference.Gpu)
        {
            _ocrPrefs.Accelerator = AcceleratorPreference.Cpu;
            _ocrPrefs.Save();
        }
        UpdateGpuAccelerationStatus();

        OcrModeCombo.SelectedIndex = (int)vm.Controller.OcrMode;
        OcrDeskewCheck.IsChecked = c.DeskewOcrLines;
        UpdateOcrStatus();
        PopulateOcrLanguageCombo();
        OcrGpuAccelerationCheck.IsChecked = _ocrPrefs.Accelerator == AcceleratorPreference.Gpu;
        UpdateOcrGpuAccelerationStatus();
        UpdatePresetRadios();
        UpdateModelsOverview();
    }

    private void UpdateOcrStatus()
    {
        var error = Vm?.Controller.Worker?.OcrStartupError;
        OcrStatus.Text = error is not null
            ? $"OCR model failed to load: {error} — layout analysis still works, OCR is inactive."
            : "";
        UpdateOcrDeskewStatus();
    }

    /// <summary>
    /// Deskew is estimated from the OCR detector's own line quads, so it is inert with OCR off
    /// (Core 0.56.0 leaves the pixel-projection fallback uncorrected). Say so rather than letting
    /// a ticked box imply an effect it can't have.
    /// </summary>
    private void UpdateOcrDeskewStatus()
    {
        bool ocrOn = OcrModeCombo.SelectedIndex > 0;
        OcrDeskewCheck.IsEnabled = ocrOn;
        OcrDeskewStatus.Text = ocrOn
            ? ""
            : "Inactive while OCR mode is Off — the tilt is measured from the OCR detections.";
    }

    private void OnOcrDeskewChanged(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || _loading) return;
        // Persisted in AppConfig; OnConfigChanged pushes it to the controller, whose setter drops
        // the cached analysis of the scanned pages already grouped under the old value.
        vm.AppConfig.DeskewOcrLines = OcrDeskewCheck.IsChecked == true;
        vm.OnConfigChanged();
    }

    private sealed record OcrLanguageItem(string? Id, string Label, OcrModelDescriptor? Descriptor);

    /// <summary>
    /// Populates the OCR language-pack dropdown: the bundled Latin-only default plus every
    /// downloadable <see cref="OcrModelRegistry"/> entry. The chosen set is a startup preference
    /// (<see cref="OcrPreferences.ModelSetId"/>, read once by <c>MainWindowViewModel</c>'s
    /// constructor when it builds the OCR service factory), so switching it here needs a restart
    /// to take effect — same convention as the layout-model picker above.
    /// </summary>
    private void PopulateOcrLanguageCombo()
    {
        _ocrPrefs = OcrPreferences.Load();
        var items = new List<OcrLanguageItem> { new(null, MainWindowViewModel.BundledOcrPackDisplayName, null) };
        items.AddRange(OcrModelRegistry.All.Select(d =>
            new OcrLanguageItem(d.Id, $"{d.DisplayName} — {d.LanguageCoverage}", d)));
        OcrLanguageCombo.ItemsSource = items;
        OcrLanguageCombo.DisplayMemberBinding = new Avalonia.Data.Binding(nameof(OcrLanguageItem.Label));
        OcrLanguageCombo.SelectedIndex = items.FindIndex(it => it.Id == _ocrPrefs.ModelSetId);
        if (OcrLanguageCombo.SelectedIndex < 0) OcrLanguageCombo.SelectedIndex = 0;
        UpdateOcrLanguageStatus();
    }

    private void UpdateOcrLanguageStatus()
    {
        if (OcrLanguageCombo.SelectedItem is not OcrLanguageItem item)
        {
            OcrLanguageStatus.Text = "";
            return;
        }
        if (item.Descriptor is not { } desc)
        {
            DownloadOcrModelButton.IsEnabled = false;
            OcrLanguageStatus.Text = "Bundled with the app — no download needed.";
            return;
        }
        DownloadOcrModelButton.IsEnabled = true;
        var state = OcrModelDownloader.IsInstalled(desc)
            ? $"{desc.DisplayName} installed. Restart to apply if just switched."
            : $"{desc.DisplayName} not installed (~{desc.ApproxSizeMb} MB). Press Download, then restart to apply.";
        OcrLanguageStatus.Text = state + RecognitionCostHint(desc);
    }

    /// <summary>
    /// The pack names describe accuracy ("most accurate") and download size, neither of which
    /// hints at recognition cost — and the tiers differ by more than an order of magnitude there.
    /// Measured: Medium takes ~2 minutes to recognise one 43-line scanned page on a desktop CPU,
    /// during which the single analysis worker thread serves nothing else, so every open
    /// document's layout analysis waits behind it and the app looks hung. Say so up front.
    /// Thresholds are against <see cref="OcrModelDescriptor.RelativeFullCost"/> (Tiny = 1).
    /// </summary>
    private static string RecognitionCostHint(OcrModelDescriptor desc) => desc.RelativeFullCost switch
    {
        >= 10 => "\nSlowest to run: expect a minute or more per scanned page on a typical CPU, and layout analysis for other pages pauses while it works. Prefer Tiny or Small unless you need the extra accuracy.",
        >= 1.5 => "\nModerate cost — noticeably slower per scanned page than Tiny.",
        _ => "\nFastest of the multilingual packs.",
    };

    private void OnOcrLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (OcrLanguageCombo.SelectedItem is OcrLanguageItem item)
        {
            var prefs = _ocrPrefs ??= OcrPreferences.Load();
            prefs.ModelSetId = item.Id;
            prefs.Save();
            UpdateOcrLanguageStatus();
            UpdatePresetRadios(); // pack choice decides whether "Faster scanned OCR" still matches
            UpdateModelsOverview();
        }
    }

    /// <summary>Downloads the selected language pack's three files (detector/recognizer/dictionary)
    /// to <c>ConfigDir</c>, verifying each published SHA-256. Usable after a restart.</summary>
    private async void OnDownloadOcrModel(object? sender, RoutedEventArgs e)
    {
        if (OcrLanguageCombo.SelectedItem is not OcrLanguageItem { Descriptor: { } desc }) return;

        _ocrDownloadCts?.Cancel();
        _ocrDownloadCts?.Dispose();
        var cts = _ocrDownloadCts = new CancellationTokenSource();

        SetOcrDownloadUiActive(true);
        OcrDownloadProgress.Value = 0;
        OcrLanguageStatus.Text = $"Downloading {desc.DisplayName} (~{desc.ApproxSizeMb} MB)…";

        try
        {
            var progress = new Progress<double>(p => OcrDownloadProgress.Value = p);
            var result = await OcrModelDownloader.DownloadAsync(desc, progress, cts.Token);

            OcrLanguageStatus.Text = result switch
            {
                { Ok: true } => $"Installed {desc.DisplayName}. Restart to apply.",
                { Error: "Cancelled." } => "Download cancelled.",
                _ => $"Download failed: {result.Error}",
            };
        }
        catch (Exception ex)
        {
            OcrLanguageStatus.Text = $"Download failed: {ex.Message}";
        }
        finally
        {
            SetOcrDownloadUiActive(false);
            if (ReferenceEquals(_ocrDownloadCts, cts))
            {
                _ocrDownloadCts = null;
            }
            cts.Dispose();
        }
    }

    private void OnCancelOcrDownload(object? sender, RoutedEventArgs e) => _ocrDownloadCts?.Cancel();

    private void SetOcrDownloadUiActive(bool active)
    {
        OcrDownloadProgress.IsVisible = active;
        CancelOcrDownloadButton.IsVisible = active;
        DownloadOcrModelButton.IsEnabled = !active;
        OcrLanguageCombo.IsEnabled = !active;
    }

    /// <summary>
    /// Populates the analyzer dropdown. The Heron and PP-DocLayout-S options
    /// are shown either way, but if the corresponding .onnx isn't found on any
    /// probe path we surface that in the status line below the combo. The
    /// loader falls back to PP-DocLayoutV3 with a warning rather than refusing
    /// to start.
    /// </summary>
    private void PopulateBuiltinAnalyzerCombo()
    {
        var items = new List<BuiltinAnalyzerItem>
        {
            new(BuiltinAnalyzer.PpDocLayoutV3, "PP-DocLayoutV3 (bundled)"),
            new(BuiltinAnalyzer.PpDocLayoutS,  "PP-DocLayout-S (lightweight)"),
            new(BuiltinAnalyzer.Heron,         "Docling Heron (default)"),
        };
        BuiltinAnalyzerCombo.ItemsSource = items;
        BuiltinAnalyzerCombo.DisplayMemberBinding = new Avalonia.Data.Binding(nameof(BuiltinAnalyzerItem.Label));
        BuiltinAnalyzerCombo.SelectedIndex = items.FindIndex(it => it.Value == _customModel.BuiltinAnalyzer);
        if (BuiltinAnalyzerCombo.SelectedIndex < 0) BuiltinAnalyzerCombo.SelectedIndex = 0;
        UpdateBuiltinAnalyzerStatus();
    }

    private void UpdateBuiltinAnalyzerStatus()
    {
        switch (_customModel.BuiltinAnalyzer)
        {
            case BuiltinAnalyzer.Heron:
                {
                    var heron = HeronModelLocator.FindModelPath();
                    BuiltinAnalyzerStatus.Text = heron != null
                        ? $"Heron model: {heron}  Restart to apply."
                        : $"Heron model not found ({HeronModelLocator.FileName}). See docs/heron-layout-model.md to download it; the app will fall back to PP-DocLayoutV3 until then.";
                    break;
                }
            case BuiltinAnalyzer.PpDocLayoutS:
                {
                    var pps = PPDocLayoutSModelLocator.FindModelPath();
                    BuiltinAnalyzerStatus.Text = pps != null
                        ? $"PP-DocLayout-S model: {pps}  Restart to apply."
                        : $"PP-DocLayout-S model not found ({PPDocLayoutSModelLocator.FileName}). Press Download to install it.";
                    break;
                }
            default:
                {
                    var v3 = LayoutModelRegistry.PPDocLayoutV3;
                    var path = LayoutModelLocator.FindModelPath(v3);
                    BuiltinAnalyzerStatus.Text = path != null
                        ? $"PP-DocLayoutV3 model: {path}  Restart to apply."
                        : $"PP-DocLayoutV3 model not found ({v3.FileName}). Press Download to install it (~{v3.ApproxSizeMb} MB).";
                    break;
                }
        }
    }

    private void OnBuiltinAnalyzerChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (BuiltinAnalyzerCombo.SelectedItem is BuiltinAnalyzerItem item)
        {
            _customModel.BuiltinAnalyzer = item.Value;
            ClearStaleGpuAcceleratorIfIncompatible();
            _customModel.Save();
            UpdateBuiltinAnalyzerStatus();
            UpdateGpuAccelerationStatus();
            UpdateModelsOverview();
        }
    }

    /// <summary>
    /// Resets <c>_customModel.Accelerator</c> to CPU when the current in-memory shape (just-changed
    /// <see cref="CustomLayoutModelConfig.BuiltinAnalyzer"/> or <see cref="CustomLayoutModelConfig.Enabled"/>,
    /// <em>not yet saved</em>) no longer supports GPU at all — PP-DocLayout-S has no GPU/FP16
    /// variant, the custom-model path always runs CPU. Uses
    /// <see cref="CustomLayoutModelLoader.CanConfigShapeUseGpu"/> rather than the real
    /// <see cref="CustomLayoutModelLoader.ResolveModel"/> deliberately: this runs <em>before</em>
    /// <c>_customModel.Save()</c>, so a real resolve here would read the stale on-disk config, not
    /// the change about to be made. This is pure hygiene — stops an irrelevant <c>Accelerator ==
    /// Gpu</c> accumulating as dead state in <c>custom_layout_model.json</c> and keeps
    /// <c>GpuAccelerationCheck</c> (unchecked here, guarded so its own change handler doesn't
    /// re-fire) from looking checked-but-disabled — not the actual GPU mutual-exclusion gate, which
    /// is <see cref="WouldLayoutUseGpu"/> below. Caller still saves and refreshes status.
    /// </summary>
    private void ClearStaleGpuAcceleratorIfIncompatible()
    {
        if (CustomLayoutModelLoader.CanConfigShapeUseGpu(_customModel) || _customModel.Accelerator != AcceleratorPreference.Gpu)
            return;
        _customModel.Accelerator = AcceleratorPreference.Cpu;

        bool wasLoading = _loading;
        _loading = true;
        try { GpuAccelerationCheck.IsChecked = false; }
        finally { _loading = wasLoading; }
    }

    /// <summary>
    /// The actual answer to "would layout use the GPU slot right now" — calls the real
    /// <see cref="CustomLayoutModelLoader.ResolveModel"/> (which always reads the config fresh from
    /// disk) rather than predicting from raw field shape, so it can never diverge from what a
    /// restart would really do — including through <c>ResolveModel</c>'s fallback paths (a custom
    /// model whose files are missing/invalid, or PP-DocLayout-S with a missing file, both of which
    /// can still land on a GPU-eligible built-in). Every call site that decides whether OCR may
    /// claim the mutually-exclusive GPU slot must use this, not
    /// <see cref="CustomLayoutModelLoader.CanConfigShapeUseGpu"/> — getting this one wrong risks the
    /// onnxruntime#32561 concurrent-WebGPU-session crash, not just a stale checkbox.
    /// </summary>
    private bool WouldLayoutUseGpu()
        => Vm is { } vm && CustomLayoutModelLoader.ResolveModel(vm.AppConfig, RailReaderLogging.Logger).IsGpu;

    /// <summary>
    /// GPU model status for whichever architecture <see cref="BuiltinAnalyzerCombo"/> currently
    /// selects. Disabled (with an explanation) whenever the current configuration can never use
    /// GPU at all — a custom model is enabled, or PP-DocLayout-S is selected (neither has a
    /// GPU/FP16 variant; see <see cref="CustomLayoutModelLoader.CanConfigShapeUseGpu"/>) — rather than
    /// silently doing nothing when checked. Also disabled while OCR GPU acceleration is on — the
    /// two are mutually exclusive, see <see cref="OcrPreferences.Accelerator"/>'s doc comment.
    /// </summary>
    private void UpdateGpuAccelerationStatus()
    {
        var ocrPrefs = _ocrPrefs ??= OcrPreferences.Load();
        if (ocrPrefs.Accelerator == AcceleratorPreference.Gpu)
        {
            GpuAccelerationCheck.IsEnabled = false;
            DownloadGpuModelButton.IsEnabled = false;
            GpuAccelerationStatus.Text = "Disabled — OCR GPU acceleration is already using the GPU slot (only one of the two can run on GPU at a time).";
            return;
        }
        if (_customModel.Enabled)
        {
            GpuAccelerationCheck.IsEnabled = false;
            DownloadGpuModelButton.IsEnabled = false;
            GpuAccelerationStatus.Text = "A custom layout model is in use — it always runs on CPU, no GPU path exists for a user-supplied model.";
            return;
        }
        if (LayoutModelDownloader.GpuDescriptorFor(_customModel.BuiltinAnalyzer) is not { } gpuDesc)
        {
            GpuAccelerationCheck.IsEnabled = false;
            DownloadGpuModelButton.IsEnabled = false;
            GpuAccelerationStatus.Text = "PP-DocLayout-S has no GPU model — it always runs on CPU.";
            return;
        }
        GpuAccelerationCheck.IsEnabled = true;
        DownloadGpuModelButton.IsEnabled = true;

        // Probes for a WebGPU-capable device on first call (cached after); safe to call repeatedly.
        var deviceLine = WebGpuAccelerator.IsAvailable
            ? $"GPU device detected: {WebGpuAccelerator.DeviceDescription}."
            : "No compatible GPU device detected — will fall back to CPU.";

        var gpuPath = LayoutModelLocator.FindModelPath(gpuDesc);
        var modelLine = gpuPath != null
            ? $"{gpuDesc.DisplayName} installed: {gpuPath}"
            : $"{gpuDesc.DisplayName} not installed (~{gpuDesc.ApproxSizeMb} MB). Press Download GPU model to install it.";

        var restartNote = GpuAccelerationCheck.IsChecked == true ? "  Restart to apply." : "";
        GpuAccelerationStatus.Text = $"{deviceLine}\n{modelLine}{restartNote}";
    }

    private void OnGpuAccelerationChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _customModel.Accelerator = GpuAccelerationCheck.IsChecked == true
            ? AcceleratorPreference.Gpu
            : AcceleratorPreference.Cpu;
        _customModel.Save();

        // Mutually exclusive with OCR GPU acceleration — see OnOcrGpuAccelerationChanged and
        // OcrPreferences.Accelerator's doc comment. WouldLayoutUseGpu (the real resolve), not a raw
        // Accelerator check or CanConfigShapeUseGpu's shape heuristic — _customModel.Save() just
        // ran, so this reads the real just-saved state including any fallback ResolveModel would
        // actually take (a custom model whose files are missing, PP-DocLayout-S with a missing
        // file — both can still land on GPU via a built-in fallback despite the raw shape saying no).
        if (WouldLayoutUseGpu())
        {
            var ocrPrefs = _ocrPrefs ??= OcrPreferences.Load();
            if (ocrPrefs.Accelerator == AcceleratorPreference.Gpu)
            {
                ocrPrefs.Accelerator = AcceleratorPreference.Cpu;
                ocrPrefs.Save();
                OcrGpuAccelerationCheck.IsChecked = false;
                UpdateOcrGpuAccelerationStatus();
            }
        }
        UpdateGpuAccelerationStatus();
        UpdatePresetRadios();
        UpdateModelsOverview();
    }

    /// <summary>Downloads the GPU model for whichever architecture is currently selected
    /// in <see cref="BuiltinAnalyzerCombo"/>, to the same writable <c>ConfigDir/models</c> location
    /// as <see cref="OnDownloadModel"/>. Usable after a restart.</summary>
    private async void OnDownloadGpuModel(object? sender, RoutedEventArgs e)
    {
        if (LayoutModelDownloader.GpuDescriptorFor(_customModel.BuiltinAnalyzer) is not { } desc)
            return;

        _gpuDownloadCts?.Cancel();
        _gpuDownloadCts?.Dispose();
        var cts = _gpuDownloadCts = new CancellationTokenSource();

        SetGpuDownloadUiActive(true);
        GpuDownloadProgress.Value = 0;
        GpuAccelerationStatus.Text = $"Downloading {desc.DisplayName} (~{desc.ApproxSizeMb} MB)…";

        try
        {
            var progress = new Progress<double>(p => GpuDownloadProgress.Value = p);
            var result = await LayoutModelDownloader.DownloadAsync(desc, progress, cts.Token);

            GpuAccelerationStatus.Text = result switch
            {
                { Ok: true } => $"Installed {desc.DisplayName} → {result.Path}  Restart to apply.",
                { Error: "Cancelled." } => "Download cancelled.",
                _ => $"Download failed: {result.Error}",
            };
        }
        catch (Exception ex)
        {
            GpuAccelerationStatus.Text = $"Download failed: {ex.Message}";
        }
        finally
        {
            SetGpuDownloadUiActive(false);
            if (ReferenceEquals(_gpuDownloadCts, cts))
            {
                _gpuDownloadCts = null;
            }
            cts.Dispose();
        }
    }

    private void OnCancelGpuDownload(object? sender, RoutedEventArgs e) => _gpuDownloadCts?.Cancel();

    private void SetGpuDownloadUiActive(bool active)
    {
        GpuDownloadProgress.IsVisible = active;
        CancelGpuDownloadButton.IsVisible = active;
        DownloadGpuModelButton.IsEnabled = !active;
    }

    /// <summary>
    /// Downloads the currently-selected built-in model to the writable
    /// <c>ConfigDir/models</c> location (works inside the read-only AppImage),
    /// verifying its published SHA-256. The model becomes usable after a restart.
    /// </summary>
    private async void OnDownloadModel(object? sender, RoutedEventArgs e)
    {
        if (LayoutModelDownloader.DescriptorFor(_customModel.BuiltinAnalyzer) is not { } desc)
            return;

        _downloadCts?.Cancel();
        _downloadCts?.Dispose();
        var cts = _downloadCts = new CancellationTokenSource();

        SetDownloadUiActive(true);
        DownloadProgress.Value = 0;
        BuiltinAnalyzerStatus.Text = $"Downloading {desc.DisplayName} (~{desc.ApproxSizeMb} MB)…";

        try
        {
            var progress = new Progress<double>(p => DownloadProgress.Value = p);
            var result = await LayoutModelDownloader.DownloadAsync(desc, progress, cts.Token);

            BuiltinAnalyzerStatus.Text = result switch
            {
                { Ok: true } => $"Installed {desc.DisplayName} → {result.Path}  Restart to apply.",
                { Error: "Cancelled." } => "Download cancelled.",
                _ => $"Download failed: {result.Error}",
            };
        }
        catch (Exception ex)
        {
            BuiltinAnalyzerStatus.Text = $"Download failed: {ex.Message}";
        }
        finally
        {
            SetDownloadUiActive(false);
            if (ReferenceEquals(_downloadCts, cts))
            {
                _downloadCts = null;
            }
            cts.Dispose();
        }
    }

    private void OnCancelDownload(object? sender, RoutedEventArgs e) => _downloadCts?.Cancel();

    /// <summary>Cancel any in-flight model download when the dialog is closed mid-download — otherwise
    /// the HTTP copy + hash would run to completion against a window the user has already dismissed.</summary>
    protected override void OnClosed(EventArgs e)
    {
        _downloadCts?.Cancel();
        _downloadCts?.Dispose();
        _downloadCts = null;
        _gpuDownloadCts?.Cancel();
        _gpuDownloadCts?.Dispose();
        _gpuDownloadCts = null;
        _ocrDownloadCts?.Cancel();
        _ocrDownloadCts?.Dispose();
        _ocrDownloadCts = null;
        base.OnClosed(e);
    }

    private void SetDownloadUiActive(bool active)
    {
        DownloadProgress.IsVisible = active;
        CancelDownloadButton.IsVisible = active;
        DownloadModelButton.IsEnabled = !active;
        BuiltinAnalyzerCombo.IsEnabled = !active;
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
        c.BackgroundAnalysisWindowPages = (int)(AnalysisWindow.Value ?? 12m);
        c.PageCacheRadius = (int)(PageCacheRadius.Value ?? 24m);
        c.ColourEffectIntensity = IntensitySlider.Value;
        c.AutoScrollLinePauseMs = (double)(AutoScrollLinePause.Value ?? 400m);
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

    // --- Render quality ---

    private void UpdateCustomRenderPanel(RenderQuality quality)
        => CustomRenderPanel.IsVisible = quality == RenderQuality.Custom;

    private void OnRenderQualityChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Vm is not { } vm || _loading) return;
        var quality = (RenderQuality)RenderQualityCombo.SelectedIndex;
        vm.AppConfig.RenderQuality = quality;
        UpdateCustomRenderPanel(quality);
        // OnConfigChanged → ToCoreSettings → controller.OnConfigChanged invalidates the
        // page cache, so the open page re-rasterises at the new DPI with no restart.
        vm.OnConfigChanged();
    }

    private void OnCustomRenderChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (Vm is not { } vm || _loading) return;
        // NumericUpDown Minimum/Maximum already constrain entry; clamp defensively so a
        // mid-edit null or stray value can never push Core below its 150 DPI / step-1 floor.
        vm.AppConfig.CustomMaxRenderDpi = (int)Math.Clamp(CustomMaxDpi.Value ?? 600m, 150m, 1200m);
        vm.AppConfig.CustomRenderTierStep = (int)Math.Clamp(CustomTierStep.Value ?? 75m, 1m, 300m);
        vm.OnConfigChanged();
    }

    private void OnContinuousScrollChanged(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || _loading) return;
        // Persisted in AppConfig; OnConfigChanged → ToCoreSettings → controller.OnConfigChanged
        // reaches Viewport.OnScrollModeChanged for every open viewport, so this applies live —
        // no restart needed.
        vm.AppConfig.ContinuousScroll = ContinuousScrollCheck.IsChecked == true;
        vm.OnConfigChanged();
    }

    // --- OCR ---

    private void OnOcrModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Vm is not { } vm || _loading) return;
        var mode = (OcrMode)OcrModeCombo.SelectedIndex;
        vm.Controller.OcrMode = mode;
        var prefs = OcrPreferences.Load();
        prefs.Mode = mode;
        prefs.Save();
        UpdateOcrStatus();
        UpdateModelsOverview(); // OCR mode itself is live, unlike pack/accelerator — reflects immediately
    }

    private void OnOcrGpuAccelerationChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var prefs = _ocrPrefs ??= OcrPreferences.Load();
        prefs.Accelerator = OcrGpuAccelerationCheck.IsChecked == true
            ? AcceleratorPreference.Gpu
            : AcceleratorPreference.Cpu;
        prefs.Save();

        // Mutually exclusive with layout-model GPU acceleration — see OnGpuAccelerationChanged and
        // OcrPreferences.Accelerator's doc comment. WouldLayoutUseGpu (the real resolve), same
        // reasoning as OnGpuAccelerationChanged's mirror check — a raw Accelerator==Gpu check would
        // wrongly steal/refuse the slot for a layout config that can't actually use it.
        if (prefs.Accelerator == AcceleratorPreference.Gpu && WouldLayoutUseGpu())
        {
            _customModel.Accelerator = AcceleratorPreference.Cpu;
            _customModel.Save();
            GpuAccelerationCheck.IsChecked = false;
            UpdateGpuAccelerationStatus();
        }
        UpdateOcrGpuAccelerationStatus();
        UpdatePresetRadios();
        UpdateModelsOverview();
    }

    /// <summary>
    /// Disabled while layout-model GPU acceleration is on — the two are mutually exclusive
    /// (concurrent WebGPU <c>Session.Run()</c> across two sessions crashes the process, and
    /// <c>AnalysisWorker</c> runs OCR and layout inference concurrently). See
    /// <see cref="OcrPreferences.Accelerator"/>'s doc comment.
    /// </summary>
    private void UpdateOcrGpuAccelerationStatus()
    {
        if (WouldLayoutUseGpu())
        {
            OcrGpuAccelerationCheck.IsEnabled = false;
            OcrGpuAccelerationStatus.Text = "Disabled — layout-model GPU acceleration is already using the GPU slot (only one of the two can run on GPU at a time).";
            return;
        }
        OcrGpuAccelerationCheck.IsEnabled = true;

        // Probes for a WebGPU-capable device on first call (cached after); safe to call repeatedly.
        var deviceLine = WebGpuAccelerator.IsAvailable
            ? $"GPU device detected: {WebGpuAccelerator.DeviceDescription}."
            : "No compatible GPU device detected — will fall back to CPU.";
        var restartNote = OcrGpuAccelerationCheck.IsChecked == true ? "  Restart to apply." : "";
        OcrGpuAccelerationStatus.Text = $"{deviceLine}{restartNote}";
    }

    // --- GPU acceleration presets (issue #232) ---
    //
    // A preset is a convenience writer over the same three fields the checkboxes/combo below
    // already own (CustomLayoutModelConfig.Accelerator, OcrPreferences.Accelerator,
    // OcrPreferences.ModelSetId) — there is deliberately no separate persisted "preset" value,
    // so it can never drift from the actual configuration. Toggling a checkbox by hand
    // re-derives which preset (if any) the resulting state matches (UpdatePresetRadios).

    /// <summary>
    /// Applies a preset by writing <see cref="CustomLayoutModelConfig.Accelerator"/> and
    /// <see cref="OcrPreferences.Accelerator"/>/<see cref="OcrPreferences.ModelSetId"/> directly —
    /// once, together — rather than routing through <see cref="OnGpuAccelerationChanged"/>/
    /// <see cref="OnOcrGpuAccelerationChanged"/>. Those two exist for a <i>manual</i> checkbox
    /// toggle, where only one side changes and the other must react to it; a preset always sets
    /// both sides in one shot, and cascading through the checkboxes' own change handlers would
    /// mean each intermediate step computes state (including <see cref="UpdatePresetRadios"/>'s
    /// preset match) against a partially-updated configuration — order-dependent by construction,
    /// and one more preset or a reordered branch away from showing a stale radio selection. The
    /// checkboxes/combo below are updated afterwards purely for display, guarded so their change
    /// handlers don't re-fire and redo the writes this method already made.
    /// </summary>
    private void OnPresetChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not RadioButton { IsChecked: true } rb) return;

        var ocrPrefs = _ocrPrefs ??= OcrPreferences.Load();
        AcceleratorPreference layoutAccel;
        AcceleratorPreference ocrAccel;
        string? ocrModelSetId = ocrPrefs.ModelSetId; // unchanged unless overridden below

        if (ReferenceEquals(rb, PresetCpuOnly))
        {
            layoutAccel = AcceleratorPreference.Cpu;
            ocrAccel = AcceleratorPreference.Cpu;
        }
        else if (ReferenceEquals(rb, PresetFasterNavigation))
        {
            layoutAccel = AcceleratorPreference.Gpu;
            ocrAccel = AcceleratorPreference.Cpu;
        }
        else if (ReferenceEquals(rb, PresetFasterScannedOcr))
        {
            layoutAccel = AcceleratorPreference.Cpu;
            ocrAccel = AcceleratorPreference.Gpu;
            ocrModelSetId = OcrModelRegistry.PPOCRv6Medium.Id;
        }
        else
        {
            return; // unrecognised radio — nothing to apply
        }

        _customModel.Accelerator = layoutAccel;
        _customModel.Save();
        ocrPrefs.Accelerator = ocrAccel;
        ocrPrefs.ModelSetId = ocrModelSetId;
        ocrPrefs.Save();

        bool wasLoading = _loading;
        _loading = true;
        try
        {
            GpuAccelerationCheck.IsChecked = layoutAccel == AcceleratorPreference.Gpu;
            OcrGpuAccelerationCheck.IsChecked = ocrAccel == AcceleratorPreference.Gpu;
            PopulateOcrLanguageCombo(); // re-reads _ocrPrefs, refreshes the combo/status to match
        }
        finally { _loading = wasLoading; }

        UpdateGpuAccelerationStatus();
        UpdateOcrGpuAccelerationStatus();
        UpdatePresetRadios();
        UpdateModelsOverview();
    }

    /// <summary>
    /// Reflects the current layout/OCR accelerator + OCR pack state back onto the preset radio
    /// buttons — checking the one that matches, or none (with <c>PresetCustomStatus</c> shown)
    /// when the current configuration doesn't match any preset exactly. Called after every change
    /// to the underlying checkboxes/combo, from whichever direction (preset click or manual toggle).
    /// </summary>
    private void UpdatePresetRadios()
    {
        bool layoutGpu = _customModel.Accelerator == AcceleratorPreference.Gpu;
        var ocrPrefs = _ocrPrefs ??= OcrPreferences.Load();
        bool ocrGpu = ocrPrefs.Accelerator == AcceleratorPreference.Gpu;
        bool ocrMedium = ocrPrefs.ModelSetId == OcrModelRegistry.PPOCRv6Medium.Id;

        bool matchesCpuOnly = !layoutGpu && !ocrGpu;
        bool matchesFasterNav = layoutGpu && !ocrGpu;
        bool matchesFasterOcr = !layoutGpu && ocrGpu && ocrMedium;

        bool wasLoading = _loading;
        _loading = true; // suppress OnPresetChanged while setting IsChecked programmatically
        try
        {
            PresetCpuOnly.IsChecked = matchesCpuOnly;
            PresetFasterNavigation.IsChecked = matchesFasterNav;
            PresetFasterScannedOcr.IsChecked = matchesFasterOcr;
        }
        finally { _loading = wasLoading; }

        PresetCustomStatus.IsVisible = !(matchesCpuOnly || matchesFasterNav || matchesFasterOcr);
    }

    // --- Models tab: what's actually running (issue #232) ---

    /// <summary>
    /// Refreshes the Models tab's "Active" rows from <see cref="MainWindowViewModel"/>'s startup-
    /// resolved <c>Active*</c> properties — deliberately NOT from <c>_customModel</c>/<c>_ocrPrefs</c>,
    /// which reflect the current (possibly not-yet-applied) Settings selection. OCR mode is the one
    /// exception: <c>DocumentController.OcrMode</c> applies live, so it's read straight off the
    /// controller rather than frozen at startup. "Pending" rows re-resolve the current on-disk
    /// config through the same resolution logic <see cref="MainWindowViewModel"/>'s constructor
    /// uses (<see cref="CustomLayoutModelLoader.ResolveModel"/> / <see cref="MainWindowViewModel.ResolveOcrModelSet"/>),
    /// so they can never drift from what a restart would actually produce.
    /// </summary>
    private void UpdateModelsOverview()
    {
        if (Vm is not { } vm) return;

        // --- Layout ---
        var activeLayoutAccel = vm.ActiveLayoutAccelerator == AcceleratorPreference.Gpu ? "GPU" : "CPU";
        ModelsLayoutActiveText.Text = $"{vm.ActiveLayoutModelName} · {activeLayoutAccel}";

        var pendingLayout = CustomLayoutModelLoader.ResolveModel(vm.AppConfig, RailReaderLogging.Logger);
        bool layoutPending = pendingLayout.DisplayName != vm.ActiveLayoutModelName
            || pendingLayout.IsGpu != (vm.ActiveLayoutAccelerator == AcceleratorPreference.Gpu);
        ModelsLayoutPendingText.Text = layoutPending
            ? $"{pendingLayout.DisplayName} · {(pendingLayout.IsGpu ? "GPU" : "CPU")} (restart to apply)"
            : "";

        // --- OCR ---
        var ocrMode = vm.Controller.OcrMode; // live — applies immediately, no "pending" concept
        var activeOcrAccel = vm.ActiveOcrAccelerator == AcceleratorPreference.Gpu ? "GPU" : "CPU";
        ModelsOcrActiveText.Text = ocrMode == OcrMode.Off
            ? "Off"
            : $"{ocrMode} · {vm.ActiveOcrModelName} · {activeOcrAccel}";

        var ocrPrefs = _ocrPrefs ??= OcrPreferences.Load();
        var (_, pendingOcrName, _) = MainWindowViewModel.ResolveOcrModelSet(ocrPrefs.ModelSetId, RailReaderLogging.Logger);
        // Mirrors MainWindowViewModel's own gate exactly: layout claims the GPU slot first, but only
        // if its config would actually resolve to GPU — reuses pendingLayout (above) rather than a
        // second, separately-computed check, so the two can't disagree with each other.
        bool pendingOcrGpu = ocrPrefs.Accelerator == AcceleratorPreference.Gpu
            && !pendingLayout.IsGpu
            && WebGpuAccelerator.IsAvailable;

        // ResolveOcrModelSet already silently falls back to the bundled pack when the selected one
        // isn't installed — which is exactly right for computing what a restart would actually
        // produce, but it also means a preset (or manual pick) that names an uninstalled pack shows
        // as "nothing pending" here, hiding *why* nothing changed. Call that out explicitly instead
        // of leaving it silent — this is the "optimal model isn't installed" case.
        bool selectedOcrPackMissing = ocrPrefs.ModelSetId is { } selectedId
            && OcrModelRegistry.ById(selectedId) is { } candidateDesc
            && OcrModelLocator.Locate(candidateDesc.ModelSet) is null;

        if (ocrMode == OcrMode.Off)
        {
            ModelsOcrPendingText.Text = "";
        }
        else if (selectedOcrPackMissing)
        {
            var selDesc = OcrModelRegistry.ById(ocrPrefs.ModelSetId!)!;
            ModelsOcrPendingText.Text = $"{selDesc.DisplayName} selected but not downloaded — using {pendingOcrName} until it is. Download it in the OCR tab.";
        }
        else
        {
            bool ocrPending = pendingOcrName != vm.ActiveOcrModelName || pendingOcrGpu != (vm.ActiveOcrAccelerator == AcceleratorPreference.Gpu);
            ModelsOcrPendingText.Text = ocrPending
                ? $"{pendingOcrName} · {(pendingOcrGpu ? "GPU" : "CPU")} (restart to apply)"
                : "";
        }

        // --- Advisory: FP32 PP-DocLayoutV3 on CPU, when Heron (INT8) would be materially faster
        // there (see MainWindowViewModel's matching startup check + toast). Evaluated against the
        // ACTIVE state, per design — not the pending selection. ---
        bool showAdvisory = MainWindowViewModel.IsLayoutFp32V3OnCpu(vm.ActiveLayoutArchitecture, vm.ActiveLayoutAccelerator);
        ModelsAdvisoryPanel.IsVisible = showAdvisory;
        if (showAdvisory)
        {
            bool heronInstalled = HeronModelLocator.FindModelPath() != null;
            if (heronInstalled)
            {
                ModelsAdvisoryText.Text = "Layout is running the heavier FP32 PP-DocLayoutV3 model on CPU. Docling Heron (INT8, already installed) is quantized for CPU and will be noticeably faster.";
                ModelsAdvisoryFixButton.Content = "Switch to Heron (INT8)";
            }
            else
            {
                ModelsAdvisoryText.Text = "Layout is running the heavier FP32 PP-DocLayoutV3 model on CPU. Docling Heron (INT8) would be faster here, but isn't downloaded yet.";
                ModelsAdvisoryFixButton.Content = "Switch to Heron & open download";
            }
        }

        // --- Advisory: OCR GPU acceleration on with the Tiny pack, which measured no GPU speedup
        // (see MainWindowViewModel's matching startup check + toast for the full rationale).
        // Evaluated against the ACTIVE state, per design — not the pending selection. ---
        ModelsOcrAdvisoryPanel.IsVisible = MainWindowViewModel.IsOcrGpuWastedOnTinyPack(vm.ActiveOcrAccelerator, vm.ActiveOcrModelSetId);
    }

    /// <summary>One-click fix for the FP32-V3-on-CPU advisory above — mirrors
    /// <see cref="MainWindowViewModel.SwitchToHeronLayoutModel"/>'s live-toast equivalent, but from
    /// inside Settings: switches the built-in layout model to Docling Heron (INT8) and refreshes
    /// every affected control. Takes effect next launch, same convention as every other layout-model
    /// setting.</summary>
    private void OnFixLayoutModelAdvisory(object? sender, RoutedEventArgs e)
    {
        // Switching the preference alone only fixes anything if Heron's file is actually on disk —
        // otherwise CustomLayoutModelLoader's existing fallback just lands back on V3 next launch
        // (logged, not an error) and this same advisory would fire again having "fixed" nothing.
        // When it isn't installed, apply the switch anyway (so it's ready the moment the download
        // finishes) but land the user on Advanced, where the file-missing status line and the
        // already-built Download button are waiting — reusing that download/progress machinery
        // rather than duplicating it in this small panel.
        bool heronInstalled = HeronModelLocator.FindModelPath() != null;

        _customModel.BuiltinAnalyzer = BuiltinAnalyzer.Heron;
        _customModel.Save();

        bool wasLoading = _loading;
        _loading = true;
        try { PopulateBuiltinAnalyzerCombo(); }
        finally { _loading = wasLoading; }

        UpdateBuiltinAnalyzerStatus();
        UpdateGpuAccelerationStatus();
        UpdateModelsOverview();

        if (!heronInstalled) SelectTab("Advanced");
    }

    /// <summary>
    /// One-click (partial) fix for the OCR-GPU-with-Tiny advisory: turns off OCR GPU acceleration,
    /// freeing the GPU slot. Unlike <see cref="OnFixLayoutModelAdvisory"/> this doesn't try to also
    /// decide the "right" replacement — switching to Medium would need a ~138 MB download the user
    /// hasn't asked for, and giving the freed slot to layout is a separate opt-in choice — so this
    /// only undoes the wasted part and leaves the rest to the user (Performance tab, right there).
    /// </summary>
    private void OnFixOcrGpuAdvisory(object? sender, RoutedEventArgs e)
    {
        bool wasLoading = _loading;
        _loading = true;
        try { OcrGpuAccelerationCheck.IsChecked = false; }
        finally { _loading = wasLoading; }

        var ocrPrefs = _ocrPrefs ??= OcrPreferences.Load();
        ocrPrefs.Accelerator = AcceleratorPreference.Cpu;
        ocrPrefs.Save();

        UpdateOcrGpuAccelerationStatus();
        UpdatePresetRadios();
        UpdateModelsOverview();
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
        vm.AppConfig.BackgroundAnalysisWindowPages = defaults.BackgroundAnalysisWindowPages;
        vm.AppConfig.PageCacheRadius = defaults.PageCacheRadius;
        vm.AppConfig.UiFontScale = defaults.UiFontScale;
        vm.SetDarkMode(defaults.DarkMode);
        vm.AppConfig.ColourEffect = defaults.ColourEffect;
        vm.Controller.SetColourEffect(defaults.ColourEffect);
        vm.AppConfig.ColourEffectIntensity = defaults.ColourEffectIntensity;
        vm.AppConfig.RenderQuality = App.DefaultRenderQuality; // desktop ships High, not Core's Quality
        vm.AppConfig.CustomMaxRenderDpi = defaults.CustomMaxRenderDpi;
        vm.AppConfig.CustomRenderTierStep = defaults.CustomRenderTierStep;
        vm.AppConfig.ContinuousScroll = defaults.ContinuousScroll;
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
        vm.AppConfig.AutoScrollStopClasses = new HashSet<BlockRole>(defaults.AutoScrollStopClasses);
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
        vm.Controller.OcrMode = OcrMode.Off;
        vm.AppConfig.DeskewOcrLines = defaults.DeskewOcrLines;
        var ocrPrefs = OcrPreferences.Load();
        ocrPrefs.Mode = OcrMode.Off;
        ocrPrefs.ModelSetId = null;
        ocrPrefs.Accelerator = AcceleratorPreference.Cpu;
        ocrPrefs.Save();
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
        ClearStaleGpuAcceleratorIfIncompatible();
        _customModel.Save();
        UpdateCustomModelStatus();
        UpdateGpuAccelerationStatus();
        UpdateModelsOverview();
    }

    private async void OnBrowseCustomModel(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = await PickFile("Select ONNX model", "ONNX", new[] { "onnx" });
            if (path == null) return;
            CustomModelPath.Text = path;
            _customModel.ModelPath = path;
            _customModel.Save();
            UpdateCustomModelStatus();
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error("[Settings] Browse custom model failed", ex);
        }
    }

    private async void OnBrowseCustomModelMapping(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = await PickFile("Select class-mapping JSON", "JSON", new[] { "json" });
            if (path == null) return;
            CustomModelMappingPath.Text = path;
            _customModel.MappingPath = path;
            _customModel.Save();
            UpdateCustomModelStatus();
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error("[Settings] Browse custom model mapping failed", ex);
        }
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
            CustomModelStatus.Text = $"Using built-in layout model ({_customModel.BuiltinAnalyzer}).";
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
