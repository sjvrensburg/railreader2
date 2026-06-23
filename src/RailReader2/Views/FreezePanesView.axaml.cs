using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RailReader2.ViewModels;

namespace RailReader2.Views;

/// <summary>
/// Freeze-pane controls (Rows / Columns / Both / Unfreeze) — hosted in the toolbar's Freeze flyout
/// (<see cref="ToolBarView"/>). Freeze is page-wide and table-independent, so it is available on any
/// page. Binds to the shared <see cref="MainWindowViewModel"/> via the supplied DataContext; controls
/// are wired imperatively (like <see cref="ToolBarView"/>).
/// </summary>
public partial class FreezePanesView : UserControl
{
    private MainWindowViewModel? _vm;

    public FreezePanesView()
    {
        InitializeComponent();

        // Freeze-mode toggles arm a placement (the pointer then shows the matching guide line; a click
        // drops the split). Click (not IsCheckedChanged) so syncing IsChecked from the VM can't loop.
        FreezeRowsButton.Click += (_, _) => _vm?.ArmFreeze(FreezeMode.Rows);
        FreezeColumnsButton.Click += (_, _) => _vm?.ArmFreeze(FreezeMode.Columns);
        FreezeBothButton.Click += (_, _) => _vm?.ArmFreeze(FreezeMode.Both);
        UnfreezeButton.Click += (_, _) => _vm?.Unfreeze();

        // Hosted in the toolbar's Freeze flyout, so this control is loaded/unloaded on every open/close.
        // (Re)wire to the VM on both DataContext change and load so re-opening the flyout stays live.
        DataContextChanged += (_, _) => Rewire();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Rewire();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = null;
        }
        base.OnUnloaded(e);
    }

    private void Rewire()
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmPropertyChanged;

        _vm = DataContext as MainWindowViewModel;

        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            UpdateFreezeControls();
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.IsFrozen)
            or nameof(MainWindowViewModel.CanFreeze)
            or nameof(MainWindowViewModel.FreezeArmMode))
            UpdateFreezeControls();
    }

    private void UpdateFreezeControls()
    {
        if (_vm is null) return;
        var mode = _vm.FreezeArmMode;
        // The armed mode shows as the pressed toggle; modes are available whenever a page is loaded
        // (or while armed). Unfreeze is enabled only when this view actually has a freeze.
        FreezeRowsButton.IsChecked = mode == FreezeMode.Rows;
        FreezeColumnsButton.IsChecked = mode == FreezeMode.Columns;
        FreezeBothButton.IsChecked = mode == FreezeMode.Both;
        bool canArm = _vm.CanFreeze || mode != FreezeMode.None;
        FreezeRowsButton.IsEnabled = canArm;
        FreezeColumnsButton.IsEnabled = canArm;
        FreezeBothButton.IsEnabled = canArm;
        UnfreezeButton.IsEnabled = _vm.IsFrozen;
    }
}
