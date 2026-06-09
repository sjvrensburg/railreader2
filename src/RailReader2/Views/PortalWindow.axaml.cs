using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using RailReader2.ViewModels;

namespace RailReader2.Views;

/// <summary>
/// Detached portal viewport (Phase 2). A borderless, non-modal, optionally always-on-top window that
/// mirrors the same <see cref="MainWindowViewModel.ActivePortalImage"/> the in-panel view binds, so
/// the sync loop is unchanged whether docked or floating. Created/torn down by
/// <c>MainWindow</c> in reaction to <see cref="MainWindowViewModel.IsPortalPoppedOut"/>; geometry is
/// persisted by <c>MainWindow</c> via <see cref="Services.PortalWindowSettings"/>.
/// </summary>
public partial class PortalWindow : Window
{
    public PortalWindow()
    {
        InitializeComponent();
    }

    /// <summary>Set the Pin toggle's checked state to match restored settings without re-firing its handler.</summary>
    public void SyncTopmostToggle(bool topmost)
    {
        Topmost = topmost;
        TopmostToggle.IsChecked = topmost;
    }

    private void OnChromePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnResizePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginResizeDrag(WindowEdge.SouthEast, e);
    }

    private void OnTopmostToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { IsChecked: { } chk })
            Topmost = chk;
    }

    private void OnDockClick(object? sender, RoutedEventArgs e)
        => (DataContext as MainWindowViewModel)?.DismissPortalWindow();
}
