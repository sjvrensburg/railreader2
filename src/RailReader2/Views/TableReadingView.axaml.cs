using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RailReader2.ViewModels;

namespace RailReader2.Views;

/// <summary>
/// The "Table Reading" accordion section: freeze-pane controls for the table the rail is on. Binds to
/// the shared <see cref="MainWindowViewModel"/> via the inherited DataContext; controls are wired
/// imperatively (like <see cref="ToolBarView"/>).
/// </summary>
public partial class TableReadingView : UserControl
{
    private MainWindowViewModel? _vm;

    public TableReadingView()
    {
        InitializeComponent();

        // Freeze-mode toggles arm a placement (the pointer then shows the matching guide line; a click
        // drops the split). Click (not IsCheckedChanged) so syncing IsChecked from the VM can't loop.
        FreezeRowsButton.Click += (_, _) => _vm?.ArmFreeze(FreezeMode.Rows);
        FreezeColumnsButton.Click += (_, _) => _vm?.ArmFreeze(FreezeMode.Columns);
        FreezeBothButton.Click += (_, _) => _vm?.ArmFreeze(FreezeMode.Both);
        UnfreezeButton.Click += (_, _) => _vm?.Unfreeze();

        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
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

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = null;
        }
        base.OnUnloaded(e);
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
