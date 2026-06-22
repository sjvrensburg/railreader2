using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using RailReader2.Services;
using RailReader2.ViewModels;

namespace RailReader2.Views;

/// <summary>
/// The "Table Reading" accordion section: navigation mode (cell/row) and focus aids (scope + tint/dim)
/// for the table the rail is on. Binds to the shared <see cref="MainWindowViewModel"/> via the inherited
/// DataContext; controls are wired imperatively (like <see cref="ToolBarView"/>) since the options are
/// enums. Changes write straight to the VM, which persists them to the table-nav prefs sidecar.
/// </summary>
public partial class TableReadingView : UserControl
{
    private MainWindowViewModel? _vm;
    private bool _syncing;

    public TableReadingView()
    {
        InitializeComponent();

        ModeCellRadio.IsCheckedChanged += (_, _) => Apply(() => _vm!.TableNavMode = TableNavMode.Cell, ModeCellRadio);
        ModeRowRadio.IsCheckedChanged += (_, _) => Apply(() => _vm!.TableNavMode = TableNavMode.Row, ModeRowRadio);

        ScopeCellRadio.IsCheckedChanged += (_, _) => Apply(() => _vm!.TableFocusScope = TableFocusScope.Cell, ScopeCellRadio);
        ScopeRowRadio.IsCheckedChanged += (_, _) => Apply(() => _vm!.TableFocusScope = TableFocusScope.Row, ScopeRowRadio);
        ScopeColumnRadio.IsCheckedChanged += (_, _) => Apply(() => _vm!.TableFocusScope = TableFocusScope.Column, ScopeColumnRadio);
        ScopeRowColRadio.IsCheckedChanged += (_, _) => Apply(() => _vm!.TableFocusScope = TableFocusScope.RowAndColumn, ScopeRowColRadio);

        // Freeze-mode toggles arm a placement (the pointer then shows the matching guide line; a click
        // drops the split). Click (not IsCheckedChanged) so syncing IsChecked from the VM can't loop.
        FreezeRowsButton.Click += (_, _) => _vm?.ArmFreeze(FreezeMode.Rows);
        FreezeColumnsButton.Click += (_, _) => _vm?.ArmFreeze(FreezeMode.Columns);
        FreezeBothButton.Click += (_, _) => _vm?.ArmFreeze(FreezeMode.Both);
        UnfreezeButton.Click += (_, _) => _vm?.Unfreeze();

        DataContextChanged += OnDataContextChanged;
    }

    // Only act on a control's own "checked on" edge (radios fire twice — off then on), and never while
    // we're pushing VM state into the controls.
    private void Apply(Action set, ToggleButton? radioGate)
    {
        if (_vm is null || _syncing) return;
        if (radioGate is not null && radioGate.IsChecked != true) return;
        set();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmPropertyChanged;

        _vm = DataContext as MainWindowViewModel;

        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            SyncFromVm();
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
        if (e.PropertyName is nameof(MainWindowViewModel.TableNavMode)
            or nameof(MainWindowViewModel.TableFocusScope))
            SyncFromVm();
        else if (e.PropertyName is nameof(MainWindowViewModel.IsFrozen)
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

    private void SyncFromVm()
    {
        if (_vm is null) return;
        _syncing = true;
        try
        {
            ModeCellRadio.IsChecked = _vm.TableNavMode == TableNavMode.Cell;
            ModeRowRadio.IsChecked = _vm.TableNavMode == TableNavMode.Row;

            ScopeCellRadio.IsChecked = _vm.TableFocusScope == TableFocusScope.Cell;
            ScopeRowRadio.IsChecked = _vm.TableFocusScope == TableFocusScope.Row;
            ScopeColumnRadio.IsChecked = _vm.TableFocusScope == TableFocusScope.Column;
            ScopeRowColRadio.IsChecked = _vm.TableFocusScope == TableFocusScope.RowAndColumn;
            UpdateFreezeControls();
        }
        finally
        {
            _syncing = false;
        }
    }
}
