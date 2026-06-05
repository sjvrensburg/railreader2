using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using RailReader.Core.Models;
using RailReader2.ViewModels;

namespace RailReader2.Views;

/// <summary>
/// Bookmarks pane — named bookmarks for the active document plus a "Back to previous
/// location" jump. Refreshes from the ViewModel's <see cref="MainWindowViewModel.BookmarksChanged"/>
/// signal and on document switch.
/// </summary>
public partial class BookmarksView : UserControl
{
    private MainWindowViewModel? _vm;

    // All five pane views are realised at once in the accordion, and ActiveTab is raised
    // synthetically on every navigation, so a collapsed/hidden pane would otherwise rebuild
    // its list on every page turn. Defer refreshes that arrive off-screen until it is shown.
    private bool _refreshPending;

    public BookmarksView()
    {
        InitializeComponent();
        EffectiveViewportChanged += OnViewportChanged;
    }

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
        _vm.BookmarksChanged += RefreshIfVisible;
        RefreshIfVisible();
    }

    private void Detach()
    {
        if (_vm is null) return;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.BookmarksChanged -= RefreshIfVisible;
        _vm = null;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainWindowViewModel.ActiveTab))
            RefreshIfVisible();
    }

    /// <summary>Rebuild the list now if the pane is visible, otherwise defer until it is.</summary>
    private void RefreshIfVisible()
    {
        if (IsEffectivelyVisible) { UpdateBookmarkSource(); _refreshPending = false; }
        else _refreshPending = true;
    }

    // Fired when the pane's visible region changes (panel shown/hidden, section expand/collapse);
    // flush a deferred refresh once it becomes visible.
    private void OnViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        if (IsEffectivelyVisible && _refreshPending)
        {
            _refreshPending = false;
            UpdateBookmarkSource();
        }
    }

    private void UpdateBookmarkSource()
    {
        var bookmarks = _vm?.ActiveTab?.Annotations.Bookmarks;
        BookmarkList.ItemsSource = bookmarks is not null ? new List<BookmarkEntry>(bookmarks) : null;
        UpdateBackButton();
    }

    private void UpdateBackButton()
    {
        BackButton.IsVisible = _vm?.Controller.CanGoBack == true;
    }

    private void OnBackClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm) return;
        vm.NavigateBack();
        UpdateBackButton();
    }

    private void OnBookmarkClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm) return;
        if (sender is not Button { DataContext: BookmarkEntry bm }) return;

        var bookmarks = vm.ActiveTab?.Annotations.Bookmarks;
        if (bookmarks is null) return;

        int index = bookmarks.IndexOf(bm);
        if (index < 0) return;

        vm.NavigateToBookmark(index);
        UpdateBackButton();
    }

    private async void OnAddBookmarkClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm || vm.ActiveTab is not { } tab) return;

        if (TopLevel.GetTopLevel(this) is not Window window) return;

        var dialog = new BookmarkNameDialog(tab.CurrentPage + 1) { FontSize = vm.CurrentFontSize };
        var name = await dialog.ShowDialog<string?>(window);
        if (name is not null)
        {
            bool added = vm.Controller.AddBookmark(name);
            UpdateBookmarkSource();
            vm.ShowStatusToast(added ? $"Bookmark: {name}" : $"Updated bookmark: {name}");
        }
    }

    private void OnDeleteBookmarkClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_vm is not { } vm) return;
        if (ResolveBookmarkIndex(sender) is not (var bm, var index)) return;

        vm.Controller.RemoveBookmark(index);
        UpdateBookmarkSource();
    }

    private async void OnRenameBookmarkClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_vm is not { } vm) return;
        if (ResolveBookmarkIndex(sender) is not (var bm, var index)) return;
        if (TopLevel.GetTopLevel(this) is not Window window) return;

        var dialog = new BookmarkNameDialog(bm.Page + 1) { FontSize = vm.CurrentFontSize };
        dialog.SetName(bm.Name);
        var newName = await dialog.ShowDialog<string?>(window);
        if (newName is not null)
        {
            vm.Controller.RenameBookmark(index, newName);
            UpdateBookmarkSource();
        }
    }

    private (BookmarkEntry Entry, int Index)? ResolveBookmarkIndex(object? sender)
    {
        if (sender is not Button btn) return null;
        var bookmarks = _vm?.ActiveTab?.Annotations.Bookmarks;
        if (bookmarks is null) return null;

        BookmarkEntry? bm = null;
        for (var v = btn.GetVisualParent(); v is not null; v = v.GetVisualParent())
        {
            if (v is Button { DataContext: BookmarkEntry entry }) { bm = entry; break; }
            if (v is ItemsControl) break;
        }
        if (bm is null) return null;

        int index = bookmarks.IndexOf(bm);
        return index >= 0 ? (bm, index) : null;
    }
}
