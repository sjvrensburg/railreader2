using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader2.ViewModels;

namespace RailReader2.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _subscribedVm;
    private PortalWindow? _portalWindow;

    // Fullscreen hover reveal: show threshold < hide threshold for hysteresis
    private const double FullScreenShowThreshold = 5.0;
    private const double FullScreenHideThreshold = 60.0;

    // Throttle chrome reveal toggles. Each flip reflows the DockPanel and
    // resizes the Viewport, which in turn invalidates the page layer's GPU
    // texture cache. 150ms is well below user-perceptible reveal latency but
    // turns 60Hz pointer-move noise into at most ~6 toggles/sec.
    private const double ChromeToggleMinIntervalMs = 150.0;
    private DateTime _lastChromeToggle = DateTime.MinValue;

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
            // The DocumentView owns the viewport wiring, viewport-size sync, and the
            // initial (incl. window.Opened-early-tab) render for the active tab.
            Document.Initialize(vm, vm.ActiveTab);
            vm.SetInvalidation(BuildInvalidationCallbacks(vm));

            SetupClipboardAndToolBar(vm);
            RailToolBar.ViewModel = vm;
            RailToolBar.SyncFromConfig();

            vm.ReadSidePanelWidth = () =>
            {
                var w = MainGrid.ColumnDefinitions[0].Width;
                return w.Value > 0 ? w.Value : 220;
            };
            UpdateSidebarColumnWidth(vm.ShowOutline);

            _subscribedVm = vm;
            vm.PropertyChanged += OnVmPropertyChanged;
            vm.ViewportFocusRequested += OnViewportFocusRequested;
        }
    }

    private void OnViewportFocusRequested() => Document.FocusViewport();

    /// <summary>
    /// Build the granular invalidation callbacks passed to the ViewModel. Each
    /// callback builds an immutable state snapshot on the UI thread and sends
    /// it to the appropriate CompositionCustomVisual handler, which re-renders
    /// on the next compositor frame.
    /// </summary>
    private InvalidationCallbacks BuildInvalidationCallbacks(MainWindowViewModel vm) => new()
    {
        // Layer rendering is delegated to the DocumentView (which owns the layers and
        // builds state for its tab); cross-cutting chrome (status bar, search panel)
        // stays here.
        InvalidateCamera = () =>
        {
            Document.RenderCamera();
            StatusBar.UpdateZoom();
        },
        InvalidatePage = () => Document.RenderPage(),
        InvalidateOverlay = () => Document.RenderOverlay(),
        // The Search pane refreshes its own "N of M" display via the VM's
        // SearchInvalidated event; here we only repaint the highlight layer.
        InvalidateSearch = () => Document.RenderSearch(),
        InvalidateAnnotations = () => Document.RenderAnnotations(),
        AnnounceAccessibility = () => Document.NotifyAccessibility(),
    };

    protected override void OnUnloaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Close the detached portal window first — a lingering non-modal window would keep the app
        // process alive after the main window unloads.
        ClosePortalWindow();
        if (_subscribedVm is { } vm)
        {
            vm.PropertyChanged -= OnVmPropertyChanged;
            vm.ViewportFocusRequested -= OnViewportFocusRequested;
            Document.Teardown();
            _subscribedVm = null;
        }
        base.OnUnloaded(e);
    }

    // --- Detached portal window (Phase 2) ---

    private void UpdatePortalWindow(MainWindowViewModel vm)
    {
        if (vm.ShouldShowPortalWindow)
        {
            if (_portalWindow is { } existing) { existing.Activate(); return; }

            var win = new PortalWindow { DataContext = vm };
            var settings = Services.PortalWindowSettings.Load();
            win.Width = settings.Width > 0 ? settings.Width : 420;
            win.Height = settings.Height > 0 ? settings.Height : 320;
            win.SyncTopmostToggle(settings.Topmost);
            if (settings.HasPosition)
            {
                win.WindowStartupLocation = WindowStartupLocation.Manual;
                win.Position = new PixelPoint(settings.X, settings.Y);
            }
            win.Closed += OnPortalWindowClosed;
            _portalWindow = win;
            // Non-modal, no owner so the user can drag it to another monitor and keep it on top.
            win.Show();
        }
        else
        {
            ClosePortalWindow();
        }
    }

    // The window's own teardown (covers app shutdown). The dock path goes through ClosePortalWindow,
    // which unsubscribes first, so this only runs on an externally-driven close.
    private void OnPortalWindowClosed(object? sender, EventArgs e)
    {
        if (_portalWindow is { } win)
        {
            SavePortalWindowGeometry(win);
            win.Closed -= OnPortalWindowClosed;
            _portalWindow = null;
        }
        if (Vm is { ShouldShowPortalWindow: true } vm)
            vm.DismissPortalWindow();
    }

    private void ClosePortalWindow()
    {
        if (_portalWindow is not { } win) return;
        SavePortalWindowGeometry(win);
        win.Closed -= OnPortalWindowClosed;
        _portalWindow = null;
        win.Close();
    }

    private static void SavePortalWindowGeometry(PortalWindow win)
    {
        new Services.PortalWindowSettings
        {
            X = win.Position.X,
            Y = win.Position.Y,
            Width = win.ClientSize.Width > 0 ? win.ClientSize.Width : win.Width,
            Height = win.ClientSize.Height > 0 ? win.ClientSize.Height : win.Height,
            Topmost = win.Topmost,
        }.Save();
    }

    private async void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (Vm is not { } vm) return;
        switch (args.PropertyName)
        {
            case nameof(MainWindowViewModel.ActiveTab):
                Document.SetTab(vm.ActiveTab);
                UpdateRailToolBarVisibility();
                break;
            case nameof(MainWindowViewModel.ShowOutline):
                UpdateSidebarColumnWidth(vm.ShowOutline);
                break;
            case nameof(MainWindowViewModel.ShowShortcuts) when vm.ShowShortcuts:
                vm.ShowShortcuts = false;
                await new ShortcutsDialog { FontSize = vm.CurrentFontSize }.ShowDialog(this);
                break;
            case nameof(MainWindowViewModel.ShowAbout) when vm.ShowAbout:
                vm.ShowAbout = false;
                var aboutDlg = new AboutDialog { FontSize = vm.CurrentFontSize };
                aboutDlg.SetLogFilePath(vm.LogFilePath);
                await aboutDlg.ShowDialog(this);
                break;
            case nameof(MainWindowViewModel.ShowSettings) when vm.ShowSettings:
                vm.ShowSettings = false;
                // Scan All temporarily overrides BackgroundAnalysisWindowPages and
                // restores the captured value on teardown; editing settings mid-scan
                // would be silently reverted, so suppress the dialog while scanning.
                if (vm.IsScanAllActive) break;
                await new SettingsWindow { DataContext = vm, FontSize = vm.CurrentFontSize }.ShowDialog(this);
                break;
            case nameof(MainWindowViewModel.ActiveTool):
                Document.UpdateAnnotationCursor();
                break;
            case nameof(MainWindowViewModel.ShouldShowPortalWindow):
                UpdatePortalWindow(vm);
                break;
            case nameof(MainWindowViewModel.IsFullScreen):
                WindowState = vm.IsFullScreen ? WindowState.FullScreen : WindowState.Normal;
                WindowDecorations = vm.IsFullScreen ? WindowDecorations.None : WindowDecorations.Full;
                break;
            case nameof(MainWindowViewModel.ShowBookmarkDialog) when vm.ShowBookmarkDialog:
                vm.ShowBookmarkDialog = false;
                if (vm.ActiveTab is { } bmTab)
                {
                    var bmDialog = new BookmarkNameDialog(bmTab.CurrentPage + 1)
                        { FontSize = vm.CurrentFontSize };
                    var bmName = await bmDialog.ShowDialog<string?>(this);
                    if (bmName is not null)
                    {
                        bool added = vm.Controller.AddBookmark(bmName);
                        vm.NotifyBookmarksChanged();
                        vm.ShowStatusToast(added ? $"Bookmark: {bmName}" : $"Updated bookmark: {bmName}");
                    }
                }
                break;
            case nameof(MainWindowViewModel.ShowGoToPage) when vm.ShowGoToPage:
                vm.ShowGoToPage = false;
                if (vm.ActiveTab is { } gotoTab)
                {
                    var dialog = new GoToPageDialog(gotoTab.CurrentPage + 1, gotoTab.PageCount)
                        { FontSize = vm.CurrentFontSize };
                    var result = await dialog.ShowDialog<int>(this);
                    if (result > 0)
                        vm.GoToPage(result - 1);
                }
                break;
        }
    }

    private void SetupClipboardAndToolBar(MainWindowViewModel vm)
    {
        vm.CopyToClipboard = async text =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
                await clipboard.SetTextAsync(text);
        };

        vm.CopyImageToClipboard = async pngBytes =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null || pngBytes is null) return;
            // Put the image on the clipboard as a real bitmap (DataFormat.Bitmap =
            // CF_DIB on Windows), which is what Paint/Word/browsers paste. The earlier
            // port wrote a raw "image/png" platform format that those apps don't read,
            // so paste silently did nothing. Decode the rendered PNG into an Avalonia
            // Bitmap and hand it to SetBitmapAsync.
            using var ms = new System.IO.MemoryStream(pngBytes);
            var bitmap = new Avalonia.Media.Imaging.Bitmap(ms);
            await clipboard.SetBitmapAsync(bitmap);
        };
    }

    private void UpdateRailToolBarVisibility()
    {
        bool shouldShow = Vm?.ActiveTab?.Rail.Active == true;
        bool wasVisible = RailToolBar.IsVisible;
        RailToolBar.IsVisible = shouldShow;

        // Persist config when toolbar hides (deferred save for slider changes)
        if (wasVisible && !shouldShow)
        {
            Vm?.AppConfig.Save();
            RailToolBar.SetJumpMode(false);
        }
        else if (shouldShow && Vm is { } v)
        {
            RailToolBar.SetJumpMode(v.JumpMode);
            RailToolBar.UpdateToggleStates();
        }
    }

    private void UpdateSidebarColumnWidth(bool showOutline)
    {
        var col = MainGrid.ColumnDefinitions[0];
        double width = Vm?.ActiveTab?.SidePanelWidth ?? 220;
        col.Width = showOutline ? new GridLength(width) : new GridLength(0);
    }

    /// <summary>
    /// Override OnKeyDown to intercept keys during the tunneling phase,
    /// before child controls (outline TreeView, etc.) can swallow them.
    /// Rail navigation keys always take priority when rail mode is active.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Vm is not { } vm) { base.OnKeyDown(e); return; }

        // During Scan All, only Escape is allowed (to cancel the scan).
        // All other keyboard input is suppressed.
        if (vm.IsScanAllActive)
        {
            if (e.Key == Key.Escape)
            {
                vm.CancelScanAll();
                e.Handled = true;
            }
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && HandleCtrlShortcut(vm, e))
            { RailToolBar.SyncState(); return; }

        // Alt+Arrow: navigation history (back/forward)
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            if (e.Key == Key.Left) { vm.NavigateBack(); e.Handled = true; return; }
            if (e.Key == Key.Right) { vm.NavigateForward(); e.Handled = true; return; }
        }

        // When the search TextBox has focus, let text input keys through. Also require the
        // Search section to actually be the open pane, so a stale focus flag (Search collapsed
        // while the box held focus) can't keep swallowing nav keys.
        bool textInputFocused = (vm.ShowOutline && vm.IsSearchInputFocused && vm.ActivePane == SidePane.Search)
            || StatusBar.IsEditing;

        if (!textInputFocused && HandleNavigationKey(vm, e))
            { RailToolBar.SyncState(); return; }

        if (HandleGlobalKey(vm, e))
            { RailToolBar.SyncState(); return; }

        base.OnKeyDown(e);
    }

    /// <summary>Ctrl+ keyboard shortcuts. Returns true if the key was handled.</summary>
    private bool HandleCtrlShortcut(MainWindowViewModel vm, KeyEventArgs e)
    {
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.O when shift:
                vm.TogglePane(SidePane.Outline);
                e.Handled = true; return true;
            case Key.B when shift:
                vm.TogglePane(SidePane.Bookmarks);
                e.Handled = true; return true;
            case Key.I when shift:
                vm.TogglePane(SidePane.Index);
                e.Handled = true; return true;
            // Semantic rail jumps — next block of a role. Backward (previous) is menu-only.
            case Key.H when shift:
                vm.JumpToRole(BlockRole.Heading); e.Handled = true; return true;
            case Key.G when shift:
                vm.JumpToRole(BlockRole.Figure); e.Handled = true; return true;
            case Key.T when shift:
                vm.JumpToRole(BlockRole.Table); e.Handled = true; return true;
            case Key.E when shift:
                vm.JumpToRole(BlockRole.DisplayMath); e.Handled = true; return true;
            case Key.O:
                _ = vm.OpenFileCommand.ExecuteAsync(null); e.Handled = true; return true;
            case Key.W: vm.CloseTab(vm.ActiveTabIndex); e.Handled = true; return true;
            case Key.Q: Close(); e.Handled = true; return true;
            case Key.M when shift:
                vm.ToggleMarginCropping(); e.Handled = true; return true;
            case Key.M:
                vm.ShowMinimap = !vm.ShowMinimap; e.Handled = true; return true;
            case Key.OemComma:
                vm.ShowSettings = true; e.Handled = true; return true;
            case Key.F:
                vm.OpenSearch();
                e.Handled = true; return true;
            case Key.G:
                vm.ShowGoToPage = true; e.Handled = true; return true;
            case Key.E:
                vm.ToggleAnnotationMode(); e.Handled = true; return true;
            case Key.Z when shift:
                vm.RedoAnnotation(); e.Handled = true; return true;
            case Key.Z:
                vm.UndoAnnotation(); e.Handled = true; return true;
            case Key.Y:
                vm.RedoAnnotation(); e.Handled = true; return true;
            case Key.C:
                if (vm.SelectedText is not null) vm.CopySelectedText();
                e.Handled = true; return true;
            case Key.L:
                vm.FireAndForget(vm.CopyBlockAsLatex(), nameof(vm.CopyBlockAsLatex));
                e.Handled = true; return true;
            case Key.Home:
                vm.GoToPage(0); e.Handled = true; return true;
            case Key.End:
                if (vm.ActiveTab is { } tEnd) vm.GoToPage(tEnd.PageCount - 1);
                e.Handled = true; return true;
            case Key.Tab:
                if (vm.Tabs.Count > 0)
                    vm.SelectTab((vm.ActiveTabIndex + 1) % vm.Tabs.Count);
                e.Handled = true; return true;
            default: return false;
        }
    }

    /// <summary>Navigation and toggle keys — only when search is not focused. Returns true if handled.</summary>
    private bool HandleNavigationKey(MainWindowViewModel vm, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down or Key.S:
                vm.HandleArrowDown(); e.Handled = true; return true;
            case Key.Up or Key.W:
                vm.HandleArrowUp(); e.Handled = true; return true;
            case Key.Right:
                vm.HandleArrowRight(e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true; return true;
            case Key.Left or Key.A:
                vm.HandleArrowLeft(e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true; return true;
            case Key.P:
                vm.ToggleAutoScrollExclusive(); e.Handled = true; return true;
            case Key.J:
                vm.ToggleJumpModeExclusive(); e.Handled = true; return true;
            case Key.C:
            {
                var effect = vm.CycleColourEffect();
                vm.ShowStatusToast($"Colour: {effect.DisplayName()}");
                e.Handled = true; return true;
            }
            case Key.B:
                vm.ShowBookmarkDialog = true; e.Handled = true; return true;
            case Key.OemTilde:
                vm.NavigateBack();
                vm.NotifyBookmarksChanged();
                e.Handled = true; return true;
            case Key.F:
                vm.ToggleLineFocusBlur(); e.Handled = true; return true;
            case Key.H:
                vm.ToggleLineHighlight(); RailToolBar.UpdateToggleStates(); e.Handled = true; return true;
            case Key.OemOpenBrackets when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift):
                RailToolBar.AdjustBlur(-0.01); e.Handled = true; return true;
            case Key.OemCloseBrackets when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift):
                RailToolBar.AdjustBlur(0.01); e.Handled = true; return true;
            case Key.OemOpenBrackets when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                RailToolBar.AdjustBlur(-0.05); e.Handled = true; return true;
            case Key.OemCloseBrackets when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                RailToolBar.AdjustBlur(0.05); e.Handled = true; return true;
            case Key.OemOpenBrackets when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                RailToolBar.AdjustSpeed(-1); e.Handled = true; return true;
            case Key.OemCloseBrackets when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                RailToolBar.AdjustSpeed(1); e.Handled = true; return true;
            case Key.OemOpenBrackets:
                RailToolBar.AdjustSpeed(-5); e.Handled = true; return true;
            case Key.OemCloseBrackets:
                RailToolBar.AdjustSpeed(5); e.Handled = true; return true;
            case Key.D when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                if (vm.ActiveTab is { } dbgTab)
                {
                    dbgTab.DebugOverlay = !dbgTab.DebugOverlay;
                    Document.RenderOverlay();
                }
                e.Handled = true; return true;
            case Key.D:
                vm.HandleArrowRight(); e.Handled = true; return true;
            case Key.Home:
                if (vm.ActiveTab is { } tH && tH.Rail.Active)
                    vm.HandleLineHome();
                else
                    vm.GoToPage(0);
                e.Handled = true; return true;
            case Key.End:
                if (vm.ActiveTab is { } tE2 && tE2.Rail.Active)
                    vm.HandleLineEnd();
                else if (vm.ActiveTab is { } tE3)
                    vm.GoToPage(tE3.PageCount - 1);
                e.Handled = true; return true;
            case Key.OemPlus or Key.Add:
                vm.HandleZoomKey(true); e.Handled = true; return true;
            case Key.OemMinus or Key.Subtract:
                vm.HandleZoomKey(false); e.Handled = true; return true;
            case Key.D0 or Key.NumPad0:
                vm.HandleResetZoom(); e.Handled = true; return true;
            case Key.Space:
                vm.HandleArrowDown(); e.Handled = true; return true;
            case Key.D1:
                vm.SetAnnotationTool(AnnotationTool.Highlight); e.Handled = true; return true;
            case Key.D2:
                vm.SetAnnotationTool(AnnotationTool.Pen); e.Handled = true; return true;
            case Key.D3:
                vm.SetAnnotationTool(AnnotationTool.Rectangle); e.Handled = true; return true;
            case Key.D4:
                vm.SetAnnotationTool(AnnotationTool.TextNote); e.Handled = true; return true;
            case Key.D5:
                vm.SetAnnotationTool(AnnotationTool.Eraser); e.Handled = true; return true;
            case Key.Delete or Key.Back when !vm.IsAnnotating && vm.SelectedAnnotation is not null:
                vm.DeleteSelectedAnnotation(); e.Handled = true; return true;
            default: return false;
        }
    }

    /// <summary>Global keys — work even when search is focused. Returns true if handled.</summary>
    private bool HandleGlobalKey(MainWindowViewModel vm, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.PageDown:
                if (vm.ActiveTab is { } t1) vm.GoToPage(t1.CurrentPage + 1);
                e.Handled = true; return true;
            case Key.PageUp:
                if (vm.ActiveTab is { } t2) vm.GoToPage(t2.CurrentPage - 1);
                e.Handled = true; return true;
            case Key.F1:
                vm.ShowShortcuts = true; e.Handled = true; return true;
            case Key.F3 when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                vm.PreviousMatch(); e.Handled = true; return true;
            case Key.F3:
                vm.NextMatch(); e.Handled = true; return true;
            case Key.F11:
                vm.IsFullScreen = !vm.IsFullScreen; e.Handled = true; return true;
            case Key.Escape when vm.AutoScrollActive:
                vm.StopAutoScroll(); e.Handled = true; return true;
            case Key.Escape when vm.IsFullScreen:
                vm.IsFullScreen = false; e.Handled = true; return true;
            case Key.Escape when vm.IsAnnotating:
                vm.CancelAnnotationTool(); e.Handled = true; return true;
            case Key.Escape when vm.IsAnnotationMode:
                vm.IsAnnotationMode = false; e.Handled = true; return true;
            case Key.Escape when vm.ShowOutline && vm.ActivePane == SidePane.Search:
                vm.ClearSearch();
                vm.ActivePane = SidePane.Outline;
                e.Handled = true; return true;
            default: return false;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (Vm is { } vm && e.Key is Key.Left or Key.Right or Key.A or Key.D)
        {
            vm.HandleArrowRelease(true);
            e.Handled = true;
        }

        // Clear non-rail edge-hold on vertical key release
        if (Vm is { } vm3 && e.Key is Key.Down or Key.Up or Key.S or Key.W or Key.Space)
        {
            vm3.Controller.ClearPageEdgeHold();
            e.Handled = true;
        }

        // Resume rail mode when Ctrl is released after free pan
        if (Vm is { RailPaused: true } vm2 && e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            vm2.ResumeRailFromPause();
            e.Handled = true;
        }

        if (!e.Handled)
            base.OnKeyUp(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Vm is not { IsFullScreen: true } vm) return;

        // Suppress hover-reveal entirely while the user is actively rail-scrolling.
        // A chrome toggle reflows the layout and may force a GPU texture re-upload
        // on the page layer; that's exactly what we don't want during scroll. The
        // user isn't trying to surface the chrome mid-scroll anyway.
        var doc = vm.Controller.ActiveDocument;
        if (vm.AutoScrollActive || (doc is not null && doc.Rail.ScrollSpeed > 0.1))
            return;

        // Throttle: don't flip the chrome more than once per ChromeToggleMinIntervalMs.
        // Pointer moves arrive at ~60Hz; without throttling, hovering near an edge
        // produces a sustained reflow storm.
        if ((DateTime.UtcNow - _lastChromeToggle).TotalMilliseconds < ChromeToggleMinIntervalMs)
            return;

        var pos = e.GetPosition(this);
        bool toggled = false;

        // Top edge: tab bar reveal
        if (!vm.ShowFullScreenHeader && pos.Y <= FullScreenShowThreshold)
        { vm.ShowFullScreenHeader = true; toggled = true; }
        else if (vm.ShowFullScreenHeader && pos.Y > FullScreenHideThreshold)
        { vm.ShowFullScreenHeader = false; toggled = true; }

        // Bottom edge: status bar reveal
        double distFromBottom = Bounds.Height - pos.Y;
        if (!vm.ShowFullScreenFooter && distFromBottom <= FullScreenShowThreshold)
        { vm.ShowFullScreenFooter = true; toggled = true; }
        else if (vm.ShowFullScreenFooter && distFromBottom > FullScreenHideThreshold)
        { vm.ShowFullScreenFooter = false; toggled = true; }

        if (toggled) _lastChromeToggle = DateTime.UtcNow;
    }
}
