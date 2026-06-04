using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RailReader2.ViewModels;

namespace RailReader2.Views;

/// <summary>
/// Side panel as an accordion. The five self-contained pane views (Outline, Bookmarks, Index,
/// Search, Comments) are stacked as Expander sections. At most one is open at a time: opening a
/// section collapses the others and that section's grid row is starred so it fills the panel
/// (collapsed rows are header-height). The open section can also be collapsed, leaving none
/// open. The open section is synced with the ViewModel's <see cref="MainWindowViewModel.ActivePane"/>
/// so the View menu and keyboard pane shortcuts drive the same state.
/// </summary>
public partial class OutlinePanel : UserControl
{
    private readonly (Expander Expander, SidePane Pane, int Row)[] _sections;
    private MainWindowViewModel? _vm;

    // Guards the ActivePane <-> Expander.IsExpanded sync against re-entrancy.
    private bool _syncing;

    public OutlinePanel()
    {
        InitializeComponent();

        _sections =
        [
            (OutlineExpander, SidePane.Outline, 0),
            (BookmarksExpander, SidePane.Bookmarks, 1),
            (IndexExpander, SidePane.Index, 2),
            (SearchExpander, SidePane.Search, 3),
            (CommentsExpander, SidePane.Comments, 4),
        ];
        foreach (var section in _sections)
            section.Expander.PropertyChanged += OnExpanderPropertyChanged;

        DataContextChanged += OnDataContextChanged;
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

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmPropertyChanged;

        _vm = DataContext as MainWindowViewModel;

        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            ExpandOnly(_vm.ActivePane);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.ActivePane) && _vm is not null && !_syncing)
            ExpandOnly(_vm.ActivePane);
    }

    private void OnExpanderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_syncing) return;
        if (e.Property != Expander.IsExpandedProperty || sender is not Expander expander) return;

        var section = Array.Find(_sections, s => s.Expander == expander);
        if (section.Expander is null) return;

        _syncing = true;
        try
        {
            if (expander.IsExpanded)
            {
                // Opening a section: collapse the others (single-open) and make it active.
                foreach (var other in _sections)
                    if (other.Expander != expander)
                        other.Expander.IsExpanded = false;
                if (_vm is not null) _vm.ActivePane = section.Pane;
            }
            // Collapsing is allowed — nothing else opens; just refresh the row heights.
            UpdateRowHeights();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Expand exactly the given pane's section (collapsing the rest). Used when an
    /// external ActivePane change (menu / shortcut / search) requests a section.</summary>
    private void ExpandOnly(SidePane pane)
    {
        _syncing = true;
        try
        {
            foreach (var (expander, p, _) in _sections)
                expander.IsExpanded = p == pane;
            UpdateRowHeights();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>The open section's row fills the panel (starred); collapsed rows are header-height.</summary>
    private void UpdateRowHeights()
    {
        foreach (var (expander, _, row) in _sections)
            Accordion.RowDefinitions[row].Height =
                expander.IsExpanded ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
    }
}
