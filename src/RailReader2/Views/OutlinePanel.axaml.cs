using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RailReader2.ViewModels;

namespace RailReader2.Views;

/// <summary>
/// Side panel as a single-open accordion. The five self-contained pane views (Outline,
/// Bookmarks, Index, Search, Comments) are stacked as Expander sections; exactly one is open
/// at a time and fills the panel (its grid row is starred, the rest are header-height). The
/// open section is kept in two-way sync with the ViewModel's <see cref="MainWindowViewModel.ActivePane"/>,
/// so the View menu and keyboard pane shortcuts drive the same state. Each pane view owns its
/// own ViewModel wiring.
/// </summary>
public partial class OutlinePanel : UserControl
{
    private readonly (Expander Expander, SidePane Pane, int Row)[] _sections;
    private MainWindowViewModel? _vm;

    // Guards the ActivePane <-> Expander.IsExpanded sync against re-entrancy.
    private bool _applying;

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
            ApplyActivePane(_vm.ActivePane);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.ActivePane) && _vm is not null)
            ApplyActivePane(_vm.ActivePane);
    }

    private void OnExpanderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_applying || _vm is null) return;
        if (e.Property != Expander.IsExpandedProperty || sender is not Expander expander) return;

        var section = Array.Find(_sections, s => s.Expander == expander);
        if (section.Expander is null) return;

        if (expander.IsExpanded)
        {
            // User opened this section — make it the active pane (which collapses the rest).
            _vm.ActivePane = section.Pane;
        }
        else if (_vm.ActivePane == section.Pane)
        {
            // Keep one section always open: re-open the active one if the user collapsed it.
            _applying = true;
            expander.IsExpanded = true;
            _applying = false;
        }
    }

    private void ApplyActivePane(SidePane active)
    {
        _applying = true;
        foreach (var (expander, pane, row) in _sections)
        {
            bool isActive = pane == active;
            expander.IsExpanded = isActive;
            Accordion.RowDefinitions[row].Height =
                isActive ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        }
        _applying = false;
    }

    private void OnClosePanelClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null)
            _vm.ShowOutline = false;
    }
}
