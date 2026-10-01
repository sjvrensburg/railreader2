using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.ComponentModel;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader2.Services;
using RailReader2.ViewModels;

namespace RailReader2.Views;

/// <summary>A simple-view checkbox standing for several block roles at once ("Figures and charts").
/// Checked when every role in the group is in the set; toggling it adds or removes them all.</summary>
public partial class RoleGroupItem : ObservableObject
{
    [ObservableProperty] private bool _isChecked;
    public string Label { get; init; } = "";
    public BlockRole[] Roles { get; init; } = [];
}

/// <summary>
/// The simple/advanced split. The simple view is not a subset of the advanced controls: where a
/// group of raw numbers only makes sense together (the reading-pace timings, the two current-line
/// toggles, the auto-scroll stop roles), it gets one outcome-shaped control that writes those
/// fields, and the advanced view shows the raw fields beneath it. As with the GPU presets, nothing
/// extra is persisted — the preset shown is always re-derived from the real values, and a
/// hand-tuned combination reads as "Custom".
/// </summary>
public partial class SettingsWindow
{
    private bool _suppressAdvancedToggle;
    private readonly ObservableCollection<RoleGroupItem> _stopRoleGroups = [];

    // --- Simple / advanced view ---

    private void InitAdvancedView()
        => SetAdvanced(SettingsViewPreferences.Load().ShowAdvanced, persist: false);

    /// <summary>Shows or hides the advanced view. <paramref name="persist"/> is false when the
    /// switch is flipped on the user's behalf (opening straight to an advanced page from a toast),
    /// so a one-off visit doesn't change what Settings looks like next time.</summary>
    private void SetAdvanced(bool on, bool persist)
    {
        Classes.Set("advanced", on);

        _suppressAdvancedToggle = true;
        try { AdvancedToggle.IsChecked = on; }
        finally { _suppressAdvancedToggle = false; }

        // A hidden TabItem keeps showing its content if it was selected — move off it.
        if (!on && MainTabControl.SelectedItem is TabItem { } selected && selected.Classes.Contains("adv"))
            MainTabControl.SelectedIndex = 0;

        if (persist)
        {
            var prefs = SettingsViewPreferences.Load();
            prefs.ShowAdvanced = on;
            prefs.Save();
        }
    }

    private void OnAdvancedToggled(object? sender, RoutedEventArgs e)
    {
        if (_suppressAdvancedToggle) return;
        SetAdvanced(AdvancedToggle.IsChecked == true, persist: true);
    }

    /// <summary>Fluent sizes tab headers from a fixed 24 px resource, which looks shouted in a
    /// sidebar and ignores the UI font scale — track the window's own font size instead.</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FontSizeProperty)
            Resources["TabItemHeaderFontSize"] = FontSize;
    }

    // --- Reading pace ---

    private sealed record PacePreset(double SnapMs, double ScrollStart, double ScrollMax, double RampS, double LinePauseMs);

    // Normal is Core's AppConfig defaults; the others move every timing the same direction.
    private static readonly PacePreset s_paceRelaxed = new(650, 10, 28, 2.0, 700);
    private static PacePreset NormalPace
    {
        get
        {
            var d = new AppConfig();
            return new(d.SnapDurationMs, d.ScrollSpeedStart, d.ScrollSpeedMax, d.ScrollRampTime, d.AutoScrollLinePauseMs);
        }
    }
    private static readonly PacePreset s_paceBrisk = new(300, 20, 60, 1.0, 200);

    private static PacePreset CurrentPace(AppConfig c)
        => new(c.SnapDurationMs, c.ScrollSpeedStart, c.ScrollSpeedMax, c.ScrollRampTime, c.AutoScrollLinePauseMs);

    private static bool PaceMatches(PacePreset a, PacePreset b)
        => Math.Abs(a.SnapMs - b.SnapMs) < 0.5
        && Math.Abs(a.ScrollStart - b.ScrollStart) < 0.5
        && Math.Abs(a.ScrollMax - b.ScrollMax) < 0.5
        && Math.Abs(a.RampS - b.RampS) < 0.05
        && Math.Abs(a.LinePauseMs - b.LinePauseMs) < 0.5;

    private void UpdatePaceRadios()
    {
        if (Vm is not { } vm) return;
        var current = CurrentPace(vm.AppConfig);
        bool relaxed = PaceMatches(current, s_paceRelaxed);
        bool normal = PaceMatches(current, NormalPace);
        bool brisk = PaceMatches(current, s_paceBrisk);

        bool wasLoading = _loading;
        _loading = true; // suppress OnPaceChanged while reflecting state
        try
        {
            PaceRelaxed.IsChecked = relaxed;
            PaceNormal.IsChecked = normal;
            PaceBrisk.IsChecked = brisk;
        }
        finally { _loading = wasLoading; }

        PaceCustomStatus.IsVisible = !(relaxed || normal || brisk);
    }

    private void OnPaceChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading || Vm is not { } vm) return;
        if (sender is not RadioButton { IsChecked: true } rb) return;

        var preset = ReferenceEquals(rb, PaceRelaxed) ? s_paceRelaxed
            : ReferenceEquals(rb, PaceBrisk) ? s_paceBrisk
            : NormalPace;

        var c = vm.AppConfig;
        c.SnapDurationMs = preset.SnapMs;
        c.ScrollSpeedStart = preset.ScrollStart;
        c.ScrollSpeedMax = preset.ScrollMax;
        c.ScrollRampTime = preset.RampS;
        c.AutoScrollLinePauseMs = preset.LinePauseMs;
        vm.OnConfigChanged();

        // Mirror into the advanced fields for display only — their own handler would write the
        // same values back one field at a time, re-deriving the preset against half-updated state.
        bool wasLoading = _loading;
        _loading = true;
        try { LoadPaceFields(c); }
        finally { _loading = wasLoading; }
        UpdatePaceRadios();
    }

    private void LoadPaceFields(AppConfig c)
    {
        SnapDuration.Value = (decimal)c.SnapDurationMs;
        ScrollStart.Value = (decimal)c.ScrollSpeedStart;
        ScrollMax.Value = (decimal)c.ScrollSpeedMax;
        RampTime.Value = (decimal)c.ScrollRampTime;
        AutoScrollLinePause.Value = (decimal)c.AutoScrollLinePauseMs;
    }

    private void OnPaceValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        SaveToConfig();
        if (!_loading) UpdatePaceRadios();
    }

    // --- Current line (highlight / dim) ---

    // Index layout of LineStyleCombo: bit 0 = highlight, bit 1 = dim.
    private void LoadLineStyle(AppConfig c, TabViewModel? tab)
    {
        bool highlight = tab?.LineHighlightEnabled ?? c.LineHighlightEnabled;
        bool dim = tab?.LineFocusBlur ?? c.LineFocusBlur;
        LineStyleCombo.SelectedIndex = (highlight ? 1 : 0) | (dim ? 2 : 0);
        UpdateLineStyleDependents();
    }

    private void UpdateLineStyleDependents()
    {
        int style = Math.Max(0, LineStyleCombo.SelectedIndex);
        bool highlight = (style & 1) != 0;
        bool dim = (style & 2) != 0;
        LineHighlightTintCombo.IsEnabled = highlight;
        LineHighlightOpacitySlider.IsEnabled = highlight;
        LineFocusBlurSlider.IsEnabled = dim;
        LinePaddingSlider.IsEnabled = dim;
    }

    private void OnLineStyleChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateLineStyleDependents();
        if (Vm is not { } vm || _loading || LineStyleCombo.SelectedIndex < 0) return;
        int style = LineStyleCombo.SelectedIndex;
        bool highlight = (style & 1) != 0;
        bool dim = (style & 2) != 0;

        // AppConfig holds the default for new documents; the open tab gets it immediately too
        // (the F / H keys and Rail menu still toggle per-tab while reading).
        vm.AppConfig.LineHighlightEnabled = highlight;
        vm.AppConfig.LineFocusBlur = dim;
        if (vm.ActiveTab is { } tab)
        {
            tab.LineHighlightEnabled = highlight;
            tab.LineFocusBlur = dim;
        }
        vm.OnConfigChanged();
    }

    // --- Auto-scroll stop groups ---

    private static readonly (string Label, BlockRole[] Roles)[] s_stopGroups =
    [
        ("Headings", [BlockRole.Heading, BlockRole.Title]),
        ("Equations and algorithms", [BlockRole.DisplayMath, BlockRole.Algorithm]),
        ("Tables", [BlockRole.Table]),
        ("Figures and charts", [BlockRole.Figure, BlockRole.Chart]),
    ];

    private void BuildStopRoleGroups(IReadOnlySet<BlockRole> active)
    {
        _stopRoleGroups.Clear();
        foreach (var (label, roles) in s_stopGroups)
        {
            var group = new RoleGroupItem { Label = label, Roles = roles, IsChecked = roles.All(active.Contains) };
            group.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(RoleGroupItem.IsChecked) || _loading || Vm is not { } vm) return;
                var set = new HashSet<BlockRole>(vm.AppConfig.AutoScrollStopClasses);
                if (group.IsChecked) set.UnionWith(group.Roles);
                else set.ExceptWith(group.Roles);
                vm.AppConfig.AutoScrollStopClasses = set;
                vm.OnConfigChanged();
                SyncStopRoleItems(set);
            };
            _stopRoleGroups.Add(group);
        }
        StopRoleGroupsList.ItemsSource = _stopRoleGroups;
    }

    /// <summary>Reflects a stop-role set onto both lists without re-firing their change handlers.</summary>
    private void SyncStopRoleItems(IReadOnlySet<BlockRole> set)
    {
        bool wasLoading = _loading;
        _loading = true;
        try
        {
            foreach (var item in _stopRoleItems) item.IsChecked = set.Contains(item.Role);
            foreach (var group in _stopRoleGroups) group.IsChecked = group.Roles.All(set.Contains);
        }
        finally { _loading = wasLoading; }
    }
}
