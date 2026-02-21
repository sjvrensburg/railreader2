using Avalonia.Controls;
using Avalonia.Interactivity;
using RailReader2.Models;
using RailReader2.ViewModels;

namespace RailReader2.Views;

public partial class MenuBarView : UserControl
{
    public MenuBarView() => InitializeComponent();

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

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
