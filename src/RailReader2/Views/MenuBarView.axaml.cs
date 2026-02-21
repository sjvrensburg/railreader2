using Avalonia.Controls;
using Avalonia.Interactivity;
using RailReader2.Models;
using RailReader2.ViewModels;

namespace RailReader2.Views;

public partial class MenuBarView : UserControl
{
    public MenuBarView() => InitializeComponent();

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        UpdateRecentFiles();
        if (DataContext is MainWindowViewModel vm)
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainWindowViewModel.ActiveTab))
                    UpdateRecentFiles();
            };
    }

    private void UpdateRecentFiles()
    {
        var menu = this.FindControl<MenuItem>("RecentFilesMenu");
        if (menu is null) return;

        menu.Items.Clear();
        var vm = Vm;
        var files = vm?.Config.RecentFiles;
        if (files is null || files.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "(No recent files)", IsEnabled = false });
            return;
        }
        foreach (var filePath in files)
        {
            if (!System.IO.File.Exists(filePath)) continue;
            var name = System.IO.Path.GetFileName(filePath);
            var dir = System.IO.Path.GetDirectoryName(filePath);
            var item = new MenuItem { Header = $"{name}  ({dir})" };
            var captured = filePath;
            item.Click += (_, _) => vm!.OpenDocument(captured);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0)
            menu.Items.Add(new MenuItem { Header = "(No recent files)", IsEnabled = false });
    }

    private void OnCloseTab(object? s, RoutedEventArgs e) => Vm?.CloseTab(Vm.ActiveTabIndex);
    private void OnQuit(object? s, RoutedEventArgs e) =>
        (VisualRoot as Window)?.Close();

    private void OnZoomIn(object? s, RoutedEventArgs e) => Vm?.HandleZoomKey(true);
    private void OnZoomOut(object? s, RoutedEventArgs e) => Vm?.HandleZoomKey(false);
    private void OnResetZoom(object? s, RoutedEventArgs e) => Vm?.HandleResetZoom();

    private void OnToggleOutline(object? s, RoutedEventArgs e)
    { if (Vm is { } vm) vm.ShowOutline = !vm.ShowOutline; }
    private void OnToggleMinimap(object? s, RoutedEventArgs e)
    { if (Vm is { } vm) vm.ShowMinimap = !vm.ShowMinimap; }
    private void OnToggleDebug(object? s, RoutedEventArgs e)
    {
        if (Vm?.ActiveTab is { } tab)
        {
            tab.DebugOverlay = !tab.DebugOverlay;
            Vm.InvalidateCanvas();
        }
    }

    private void OnEffectNone(object? s, RoutedEventArgs e) => Vm?.SetColourEffect(ColourEffect.None);
    private void OnEffectHighContrast(object? s, RoutedEventArgs e) => Vm?.SetColourEffect(ColourEffect.HighContrast);
    private void OnEffectHighVisibility(object? s, RoutedEventArgs e) => Vm?.SetColourEffect(ColourEffect.HighVisibility);
    private void OnEffectAmber(object? s, RoutedEventArgs e) => Vm?.SetColourEffect(ColourEffect.Amber);
    private void OnEffectInvert(object? s, RoutedEventArgs e) => Vm?.SetColourEffect(ColourEffect.Invert);

    private void OnGoToPage(object? s, RoutedEventArgs e)
    { if (Vm is { } vm) vm.ShowGoToPage = true; }

    private void OnPrevPage(object? s, RoutedEventArgs e)
    { if (Vm?.ActiveTab is { } tab) Vm.GoToPage(tab.CurrentPage - 1); }
    private void OnNextPage(object? s, RoutedEventArgs e)
    { if (Vm?.ActiveTab is { } tab) Vm.GoToPage(tab.CurrentPage + 1); }
    private void OnFirstPage(object? s, RoutedEventArgs e) => Vm?.GoToPage(0);
    private void OnLastPage(object? s, RoutedEventArgs e)
    { if (Vm?.ActiveTab is { } tab) Vm.GoToPage(tab.PageCount - 1); }

    private void OnShowSettings(object? s, RoutedEventArgs e)
    { if (Vm is { } vm) vm.ShowSettings = true; }
    private void OnShowShortcuts(object? s, RoutedEventArgs e)
    { if (Vm is { } vm) vm.ShowShortcuts = true; }
    private void OnShowAbout(object? s, RoutedEventArgs e)
    { if (Vm is { } vm) vm.ShowAbout = true; }

    // Edit menu handlers
    private void OnShowFind(object? s, RoutedEventArgs e) => Vm?.OpenSearch();
    private void OnFindNext(object? s, RoutedEventArgs e) => Vm?.NextMatch();
    private void OnFindPrevious(object? s, RoutedEventArgs e) => Vm?.PreviousMatch();
    private void OnUndo(object? s, RoutedEventArgs e) => Vm?.UndoAnnotation();
    private void OnRedo(object? s, RoutedEventArgs e) => Vm?.RedoAnnotation();
}
