using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using RailReader.Core.Models;
using RailReader2.ViewModels;

namespace RailReader2.Views;

public partial class OutlinePanel : UserControl
{
    private MainWindowViewModel? _vm;

    public OutlinePanel()
    {
        InitializeComponent();
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
            UpdateOutlineSource();
            UpdateBookmarkSource();
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainWindowViewModel.ActiveTab))
        {
            UpdateOutlineSource();
            UpdateBookmarkSource();
        }
    }

    private void UpdateOutlineSource()
    {
        OutlineTree.ItemsSource = _vm?.ActiveTab?.Outline;
    }

    public void UpdateBookmarkSource()
    {
        var bookmarks = _vm?.ActiveTab?.Annotations?.Bookmarks;
        BookmarkList.ItemsSource = bookmarks is not null ? new List<BookmarkEntry>(bookmarks) : null;
        UpdateBackButton();
    }

    private void UpdateBackButton()
    {
        BackButton.IsVisible = _vm?.Controller.LastPositionPage >= 0;
    }

    public bool IsBookmarksTabActive => PaneTabs.SelectedIndex == 1;

    public void SwitchToOutlineTab()
    {
        PaneTabs.SelectedIndex = 0;
    }

    public void SwitchToBookmarksTab()
    {
        PaneTabs.SelectedIndex = 1;
    }

    private void OnOutlineSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is { } vm && OutlineTree.SelectedItem is OutlineEntry { Page: { } page })
            vm.GoToPage(page);
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
        if (sender is not Button btn) return;
        if (btn.DataContext is not BookmarkEntry bm) return;

        var bookmarks = vm.ActiveTab?.Annotations?.Bookmarks;
        if (bookmarks is null) return;

        int index = bookmarks.IndexOf(bm);
        if (index < 0) return;

        vm.NavigateToBookmark(index);
        UpdateBackButton();
    }

    private async void OnAddBookmarkClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm || vm.ActiveTab is not { } tab) return;

        var window = TopLevel.GetTopLevel(this) as Window;
        if (window is null) return;

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
        // Stop the click from bubbling up to the parent bookmark button
        e.Handled = true;

        if (_vm is not { } vm) return;
        if (sender is not Button btn) return;

        // Walk up to find the BookmarkEntry from the outer button's DataContext
        var bm = FindBookmarkEntry(btn);
        if (bm is null) return;

        var bookmarks = vm.ActiveTab?.Annotations?.Bookmarks;
        if (bookmarks is null) return;

        int index = bookmarks.IndexOf(bm);
        if (index < 0) return;

        vm.Controller.RemoveBookmark(index);
        UpdateBookmarkSource();
    }

    private async void OnRenameBookmarkClick(object? sender, RoutedEventArgs e)
    {
        // Stop the click from bubbling up to the parent bookmark button
        e.Handled = true;

        if (_vm is not { } vm) return;
        if (sender is not Button btn) return;

        var bm = FindBookmarkEntry(btn);
        if (bm is null) return;

        var bookmarks = vm.ActiveTab?.Annotations?.Bookmarks;
        if (bookmarks is null) return;

        int index = bookmarks.IndexOf(bm);
        if (index < 0) return;

        var window = TopLevel.GetTopLevel(this) as Window;
        if (window is null) return;

        var dialog = new BookmarkNameDialog(bm.Page + 1) { FontSize = vm.CurrentFontSize };
        dialog.SetName(bm.Name);
        var newName = await dialog.ShowDialog<string?>(window);
        if (newName is not null)
        {
            vm.Controller.RenameBookmark(index, newName);
            UpdateBookmarkSource();
        }
    }

    /// <summary>
    /// Walk up the visual tree to find the BookmarkEntry DataContext
    /// from the outer Button in the ItemTemplate.
    /// </summary>
    private static BookmarkEntry? FindBookmarkEntry(Visual control)
    {
        for (var v = control.GetVisualParent(); v is not null; v = v.GetVisualParent())
        {
            if (v is Button { DataContext: BookmarkEntry bm })
                return bm;
            if (v is ItemsControl)
                break;
        }
        return null;
    }
}
