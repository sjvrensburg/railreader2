using Avalonia.Controls;
using Avalonia.Interactivity;
using RailReader2.ViewModels;

namespace RailReader2.Views;

/// <summary>
/// Side-panel shell: a Close button plus the tabbed host for the five self-contained pane
/// views (Outline, Bookmarks, Index, Search, Comments). The selected tab is bound to the
/// ViewModel's <see cref="MainWindowViewModel.ActivePaneIndex"/>; each pane view owns its
/// own ViewModel wiring. This thin shell is what a future Dock layout replaces — the pane
/// views move out as independent dockable tools unchanged.
/// </summary>
public partial class OutlinePanel : UserControl
{
    public OutlinePanel()
    {
        InitializeComponent();
    }

    private void OnClosePanelClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.ShowOutline = false;
    }
}
