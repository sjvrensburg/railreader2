using Avalonia.Controls;
using Avalonia.Interactivity;
using RailReader.Core.Models;
using RailReader2.ViewModels;

namespace RailReader2.Views;

public partial class MenuBarView : UserControl
{
    private MainWindowViewModel? _vm;

    public MenuBarView() => InitializeComponent();

    private MainWindowViewModel? Vm => _vm;

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _vm = DataContext as MainWindowViewModel;
        UpdateRecentFiles();
        if (_vm is not null)
            _vm.PropertyChanged += OnVmPropertyChanged;
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmPropertyChanged;
        base.OnUnloaded(e);
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainWindowViewModel.ActiveTab))
            UpdateRecentFiles();
    }

    private void UpdateRecentFiles()
    {
        var menu = this.FindControl<MenuItem>("RecentFilesMenu");
        if (menu is null) return;

        menu.Items.Clear();
        var vm = Vm;
        var files = vm?.AppConfig.RecentFiles;
        if (files is null || files.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "(No recent files)", IsEnabled = false });
            return;
        }
        foreach (var entry in files)
        {
            var filePath = entry.FilePath;
            if (!System.IO.File.Exists(filePath)) continue;
            var name = System.IO.Path.GetFileName(filePath);
            var dir = System.IO.Path.GetDirectoryName(filePath);
            var item = new MenuItem { Header = $"{name}  ({dir})" };
            item.Click += (_, _) => vm!.FireAndForget(vm.OpenDocument(filePath), nameof(vm.OpenDocument));
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0)
            menu.Items.Add(new MenuItem { Header = "(No recent files)", IsEnabled = false });
    }

    private void OnCloseTab(object? s, RoutedEventArgs e) => Vm?.CloseTab(Vm.ActiveTabIndex);

    private void OnQuit(object? s, RoutedEventArgs e)
    {
        // VisualRoot resolves to null when invoked from the menu popup, so close the main
        // window via the application lifetime instead (mirrors what Ctrl+Q does).
        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is { } window)
            window.Close();
    }

    private void OnZoomIn(object? s, RoutedEventArgs e) => Vm?.HandleZoomKey(true);
    private void OnZoomOut(object? s, RoutedEventArgs e) => Vm?.HandleZoomKey(false);
    private void OnResetZoom(object? s, RoutedEventArgs e) => Vm?.HandleResetZoom();

    private void OnToggleOutline(object? s, RoutedEventArgs e)
    { if (Vm is { } vm) vm.ShowOutline = !vm.ShowOutline; }

    // Side-panel pane navigation (View > Side Panel) — shows the panel and switches tab.
    private void OnShowOutlinePane(object? s, RoutedEventArgs e) => Vm?.ShowPane(SidePane.Outline);
    private void OnShowBookmarksPane(object? s, RoutedEventArgs e) => Vm?.ShowPane(SidePane.Bookmarks);
    private void OnShowIndexPane(object? s, RoutedEventArgs e) => Vm?.ShowPane(SidePane.Index);
    private void OnShowCommentsPane(object? s, RoutedEventArgs e) => Vm?.ShowPane(SidePane.Comments);
    private void OnShowSearchPane(object? s, RoutedEventArgs e)
    { if (Vm is { } vm) { vm.ShowOutline = true; vm.OpenSearch(); } }
    private void OnHidePanel(object? s, RoutedEventArgs e)
    { if (Vm is { } vm) vm.ShowOutline = false; }

    private void OnToggleMinimap(object? s, RoutedEventArgs e)
    { if (Vm is { } vm) vm.ShowMinimap = !vm.ShowMinimap; }
    private void OnToggleFullscreen(object? s, RoutedEventArgs e)
    { if (Vm is { } vm) vm.IsFullScreen = !vm.IsFullScreen; }
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

    // Semantic rail jumps (next / previous block of a role).
    private void OnNextHeading(object? s, RoutedEventArgs e) => Vm?.JumpToRole(BlockRole.Heading);
    private void OnNextFigure(object? s, RoutedEventArgs e) => Vm?.JumpToRole(BlockRole.Figure);
    private void OnNextTable(object? s, RoutedEventArgs e) => Vm?.JumpToRole(BlockRole.Table);
    private void OnNextEquation(object? s, RoutedEventArgs e) => Vm?.JumpToRole(BlockRole.DisplayMath);
    private void OnPrevHeading(object? s, RoutedEventArgs e) => Vm?.JumpToRole(BlockRole.Heading, forward: false);
    private void OnPrevFigure(object? s, RoutedEventArgs e) => Vm?.JumpToRole(BlockRole.Figure, forward: false);
    private void OnPrevTable(object? s, RoutedEventArgs e) => Vm?.JumpToRole(BlockRole.Table, forward: false);
    private void OnPrevEquation(object? s, RoutedEventArgs e) => Vm?.JumpToRole(BlockRole.DisplayMath, forward: false);

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
    private void OnToggleAnnotationMode(object? s, RoutedEventArgs e) => Vm?.ToggleAnnotationMode();
    private void OnUndo(object? s, RoutedEventArgs e) => Vm?.UndoAnnotation();
    private void OnRedo(object? s, RoutedEventArgs e) => Vm?.RedoAnnotation();
    private void OnCopyBlockAsLatex(object? s, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.FireAndForget(vm.CopyBlockAsLatex(), nameof(vm.CopyBlockAsLatex));
    }
}
