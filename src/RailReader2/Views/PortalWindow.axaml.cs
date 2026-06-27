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
    // Chrome text sits a touch smaller than body text (12px at the 14px base), like the original
    // fixed-size chrome, but now scales with the UI font-scale setting.
    private const double ChromeFontRatio = 12.0 / 14.0;

    public PortalWindow()
    {
        InitializeComponent();

        // Windows-only: Aero Snap (drag-to-edge, Win+Arrow half-tiling) requires the native window to
        // carry WS_THICKFRAME + WS_MAXIMIZEBOX — styles a borderless WindowDecorations.None window lacks,
        // so on Windows the portal can't be docked to a screen edge the way it can under most X11 WMs
        // (which happily snap even undecorated tops). BorderOnly is the cheapest decoration level that
        // still gets those styles (resizable + maximizable are on by default) *without* WS_CAPTION, and
        // — unlike Full + ExtendClientAreaToDecorationsHint — it leaves NeedsManagedDecorations false, so
        // this Avalonia build draws no managed title bar/caption buttons over our custom dark chrome.
        // Kept Windows-only: WindowDecorations.None already snaps fine on Linux/X11, and bumping the
        // level there risks the WM drawing its own titlebar (MWM hints are coarse), regressing the look.
        if (OperatingSystem.IsWindows())
            WindowDecorations = WindowDecorations.BorderOnly;
    }

    /// <summary>Apply the app's scaled font size (<c>MainWindowViewModel.CurrentFontSize</c>) to the
    /// window: body/hint text inherits it directly, the chrome bar (Lock/Pin/Dock buttons, label,
    /// icons) a slightly smaller derived size. Called on creation and again from MainWindow's
    /// CurrentFontSize property-change case, so a live Settings change reaches an open window.</summary>
    public void ApplyFontScale(double uiFontSize)
    {
        FontSize = uiFontSize;
        Avalonia.Controls.Documents.TextElement.SetFontSize(ChromeBar, uiFontSize * ChromeFontRatio);
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
