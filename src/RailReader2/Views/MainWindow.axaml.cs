using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using RailReader2.ViewModels;

namespace RailReader2.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (Vm is { } vm)
        {
            Viewport.ViewModel = vm;
            Minimap.ViewModel = vm;

            // Wire granular invalidation callbacks
            vm.SetInvalidation(new InvalidationCallbacks
            {
                InvalidateCamera = UpdateCameraTransform,
                InvalidatePage = () =>
                {
                    PageLayer.InvalidateVisual();
                    Minimap.InvalidateVisual();
                },
                InvalidateOverlay = () => OverlayLayer.InvalidateVisual(),
            });

            // Legacy fallback (for any code still calling SetInvalidateCanvas)
            vm.SetInvalidateCanvas(() =>
            {
                UpdateCameraTransform();
                PageLayer.InvalidateVisual();
                OverlayLayer.InvalidateVisual();
                Minimap.InvalidateVisual();
            });

            // Keep ViewModel's viewport size in sync with the actual drawable area.
            // SizeChanged fires during the initial layout pass (before window.Opened),
            // so OpenDocument will already see correct dimensions when it runs.
            vm.SetViewportSize(Viewport.Bounds.Width, Viewport.Bounds.Height);
            Viewport.SizeChanged += (_, _) =>
            {
                vm.SetViewportSize(Viewport.Bounds.Width, Viewport.Bounds.Height);
                if (vm.ActiveTab is { } tab)
                {
                    var (ww, wh) = (Viewport.Bounds.Width, Viewport.Bounds.Height);
                    tab.ClampCamera(ww, wh);
                    UpdateCameraTransform();
                }
            };

            // Wire up initial tab state
            UpdateLayerBindings(vm.ActiveTab);

            vm.PropertyChanged += (_, args) =>
            {
                switch (args.PropertyName)
                {
                    case nameof(MainWindowViewModel.ActiveTab):
                        UpdateLayerBindings(vm.ActiveTab);
                        UpdateCameraTransform();
                        PageLayer.InvalidateVisual();
                        OverlayLayer.InvalidateVisual();
                        Minimap.InvalidateVisual();
                        break;
                    case nameof(MainWindowViewModel.ShowShortcuts) when vm.ShowShortcuts:
                        vm.ShowShortcuts = false;
                        new ShortcutsDialog().ShowDialog(this);
                        break;
                    case nameof(MainWindowViewModel.ShowAbout) when vm.ShowAbout:
                        vm.ShowAbout = false;
                        new AboutDialog().ShowDialog(this);
                        break;
                    case nameof(MainWindowViewModel.ShowSettings) when vm.ShowSettings:
                        vm.ShowSettings = false;
                        var settings = new SettingsWindow { DataContext = vm };
                        settings.ShowDialog(this);
                        break;
                }
            };
        }
    }

    private void UpdateLayerBindings(TabViewModel? tab)
    {
        PageLayer.Tab = tab;
        PageLayer.ColourEffects = Vm?.ColourEffects;
        OverlayLayer.Tab = tab;
        OverlayLayer.ColourEffects = Vm?.ColourEffects;

        // Wire DPI render callback so page layer refreshes when bitmap upgrades
        if (tab is not null)
        {
            tab.OnDpiRenderComplete = () =>
            {
                PageLayer.InvalidateVisual();
                Minimap.InvalidateVisual();
            };
        }
    }

    private void UpdateCameraTransform()
    {
        var tab = Vm?.ActiveTab;
        if (tab is null)
        {
            CameraPanel.RenderTransform = null;
            PagePanel.Width = 0;
            PagePanel.Height = 0;
            return;
        }

        CameraPanel.RenderTransform = new MatrixTransform(
            new Matrix(tab.Camera.Zoom, 0, 0, tab.Camera.Zoom,
                       tab.Camera.OffsetX, tab.Camera.OffsetY));
        PagePanel.Width = tab.PageWidth;
        PagePanel.Height = tab.PageHeight;
        Minimap.InvalidateVisual();
    }

    /// <summary>
    /// Override OnKeyDown to intercept keys during the tunneling phase,
    /// before child controls (outline TreeView, etc.) can swallow them.
    /// Rail navigation keys always take priority when rail mode is active.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Vm is not { } vm) { base.OnKeyDown(e); return; }
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (ctrl)
        {
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            switch (e.Key)
            {
                case Key.O when shift:
                    vm.ShowOutline = !vm.ShowOutline; e.Handled = true; return;
                case Key.O:
                    _ = vm.OpenFileCommand.ExecuteAsync(null); e.Handled = true; return;
                case Key.W: vm.CloseTab(vm.ActiveTabIndex); e.Handled = true; return;
                case Key.Q: Close(); e.Handled = true; return;
                case Key.M:
                    vm.ShowMinimap = !vm.ShowMinimap; e.Handled = true; return;
                case Key.OemComma:
                    vm.ShowSettings = true; e.Handled = true; return;
                case Key.Home:
                    vm.GoToPage(0); e.Handled = true; return;
                case Key.End:
                    if (vm.ActiveTab is { } tEnd) vm.GoToPage(tEnd.PageCount - 1);
                    e.Handled = true; return;
                case Key.Tab:
                    if (vm.Tabs.Count > 0)
                        vm.SelectTab((vm.ActiveTabIndex + 1) % vm.Tabs.Count);
                    e.Handled = true; return;
            }
        }

        // Navigation keys always handled at window level
        switch (e.Key)
        {
            case Key.Down or Key.S when !ctrl:
                vm.HandleArrowDown(); e.Handled = true; break;
            case Key.Up or Key.W when !ctrl:
                vm.HandleArrowUp(); e.Handled = true; break;
            case Key.Right or Key.D when !ctrl:
                vm.HandleArrowRight(); e.Handled = true; break;
            case Key.Left or Key.A when !ctrl:
                vm.HandleArrowLeft(); e.Handled = true; break;
            case Key.PageDown:
                if (vm.ActiveTab is { } t1) vm.GoToPage(t1.CurrentPage + 1);
                e.Handled = true; break;
            case Key.PageUp:
                if (vm.ActiveTab is { } t2) vm.GoToPage(t2.CurrentPage - 1);
                e.Handled = true; break;
            case Key.Home:
                vm.GoToPage(0); e.Handled = true; break;
            case Key.End:
                if (vm.ActiveTab is { } t3) vm.GoToPage(t3.PageCount - 1);
                e.Handled = true; break;
            case Key.OemPlus or Key.Add:
                vm.HandleZoomKey(true); e.Handled = true; break;
            case Key.OemMinus or Key.Subtract:
                vm.HandleZoomKey(false); e.Handled = true; break;
            case Key.D0 or Key.NumPad0:
                vm.HandleResetZoom(); e.Handled = true; break;
            case Key.Space:
                vm.HandleArrowDown(); e.Handled = true; break;
            case Key.F1:
                vm.ShowShortcuts = true; e.Handled = true; break;
        }

        if (e.Key == Key.D && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (vm.ActiveTab is { } tab)
            {
                tab.DebugOverlay = !tab.DebugOverlay;
                OverlayLayer.InvalidateVisual();
            }
            e.Handled = true;
        }

        if (!e.Handled)
            base.OnKeyDown(e);
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (e.Key is Key.Left or Key.Right or Key.A or Key.D)
        {
            vm.HandleArrowRelease(true);
            e.Handled = true;
        }
    }
}
