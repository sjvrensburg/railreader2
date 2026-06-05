using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RailReader.Core.Models;
using RailReader2.ViewModels;

namespace RailReader2.Views;

/// <summary>
/// Outline pane — the PDF bookmark/outline tree, kept in sync with the current page
/// (highlighting the nearest enclosing heading) and navigating on selection. Wires its
/// ViewModel subscriptions on load and tears them down on unload so it behaves correctly
/// as a lazily-realised tab / dockable tool.
/// </summary>
public partial class OutlineView : PaneRefreshView
{
    private MainWindowViewModel? _vm;
    private TabViewModel? _watchedTab;
    private bool _suppressOutlineSelection;

    // True while a pointer press is in progress on the tree, so OnOutlineSelectionChanged can
    // tell a click apart from a keyboard arrow (only a click hands focus to the viewport).
    private bool _pointerSelect;

    public OutlineView()
    {
        InitializeComponent();
        // Tunnel so the flag is set before the tree processes the press and raises selection.
        OutlineTree.AddHandler(InputElement.PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
        OutlineTree.AddHandler(InputElement.PointerReleasedEvent, OnTreePointerReleased, RoutingStrategies.Tunnel);
    }

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e) => _pointerSelect = true;
    private void OnTreePointerReleased(object? sender, PointerReleasedEventArgs e) => _pointerSelect = false;

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Attach(DataContext as MainWindowViewModel);
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        Detach();
        base.OnUnloaded(e);
    }

    private void Attach(MainWindowViewModel? vm)
    {
        Detach();
        _vm = vm;
        if (_vm is null) return;

        _vm.PropertyChanged += OnVmPropertyChanged;
        WatchActiveTabPage();
        RefreshIfVisible();
    }

    private void Detach()
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = null;
        }
        if (_watchedTab is not null)
        {
            _watchedTab.PropertyChanged -= OnTabPropertyChanged;
            _watchedTab = null;
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        // ActiveTab is raised synthetically on every navigation (per animation frame during rail
        // scroll), but the outline binding only changes on a real tab switch — the highlight
        // follows CurrentPage via OnTabPropertyChanged. Skip the re-bind/tree-walk otherwise.
        if (args.PropertyName == nameof(MainWindowViewModel.ActiveTab) && _vm?.ActiveTab != _watchedTab)
        {
            WatchActiveTabPage();
            RefreshIfVisible();
        }
    }

    // Re-bind the tree and sync the highlight to the current page (deferred while hidden).
    protected override void Refresh()
    {
        UpdateOutlineSource();
        SyncOutlineToPage();
    }

    private void WatchActiveTabPage()
    {
        if (_watchedTab is not null)
            _watchedTab.PropertyChanged -= OnTabPropertyChanged;

        _watchedTab = _vm?.ActiveTab;

        if (_watchedTab is not null)
            _watchedTab.PropertyChanged += OnTabPropertyChanged;
    }

    private void OnTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(TabViewModel.CurrentPage))
            RefreshIfVisible();
    }

    private void UpdateOutlineSource()
    {
        OutlineTree.ItemsSource = _vm?.ActiveTab?.Outline;
    }

    private void SyncOutlineToPage()
    {
        if (_vm?.ActiveTab is not { } tab) return;
        var outline = tab.Outline;
        if (outline is null || outline.Count == 0) return;

        int currentPage = tab.CurrentPage;
        var best = FindEntryForPage(outline, currentPage);

        if (OutlineTree.SelectedItem == best) return;

        _suppressOutlineSelection = true;
        try
        {
            OutlineTree.SelectedItem = best;
        }
        finally
        {
            _suppressOutlineSelection = false;
        }
    }

    private void OnOutlineSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressOutlineSelection) return;
        if (_vm is { } vm && OutlineTree.SelectedItem is OutlineEntry { Page: { } page })
        {
            vm.GoToPage(page);
            // Hand focus to the viewport only for a mouse click (keyboard arrowing of the outline
            // keeps focus). Deferred so the tree's own focus-on-click settles first.
            if (_pointerSelect)
                Dispatcher.UIThread.Post(vm.RequestViewportFocus);
        }
    }

    /// <summary>
    /// Find the outline entry whose page is closest to but not past the current page.
    /// Walks the tree depth-first, returning the last entry with Page &lt;= currentPage.
    /// </summary>
    private static OutlineEntry? FindEntryForPage(List<OutlineEntry> entries, int currentPage)
    {
        OutlineEntry? best = null;
        FindEntryForPageRecursive(entries, currentPage, ref best);
        return best;
    }

    private static void FindEntryForPageRecursive(List<OutlineEntry> entries, int currentPage, ref OutlineEntry? best)
    {
        foreach (var entry in entries)
        {
            if (entry.Page is { } p && p <= currentPage)
            {
                if (best is null || p >= best.Page!.Value)
                    best = entry;
            }
            if (entry.Children.Count > 0)
                FindEntryForPageRecursive(entry.Children, currentPage, ref best);
        }
    }
}
