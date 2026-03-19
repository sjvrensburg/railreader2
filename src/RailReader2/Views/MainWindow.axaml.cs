using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using RailReader.Core;
using RailReader2.Controls;
using RailReader.Core.Models;
using RailReader2.ViewModels;

namespace RailReader2.Views;

public partial class MainWindow : Window
{
    private double _lastMinimapOx;
    private double _lastMinimapOy;
    private double _lastMinimapZoom;

    // Fullscreen tab reveal: show threshold < hide threshold for hysteresis
    private const double FullScreenShowThreshold = 5.0;
    private const double FullScreenHideThreshold = 60.0;

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
                    PageLayer.ActiveEffect = vm.Controller.ActiveColourEffect;
                    PageLayer.ActiveIntensity = vm.Controller.ActiveColourIntensity;
                    PageLayer.MotionBlurEnabled = vm.Config.MotionBlur;
                    PageLayer.MotionBlurIntensity = vm.Config.MotionBlurIntensity;
                    var tab = vm.ActiveTab;
                    bool blur = tab?.LineFocusBlur ?? false;
                    bool bionic = tab?.BionicReading ?? false;
                    PageLayer.LineFocusBlurEnabled = blur;
                    PageLayer.LineFocusBlurIntensity = vm.Config.LineFocusBlurIntensity;
                    PageLayer.LineFocusPadding = vm.Config.LineFocusPadding;
                    PageLayer.BionicReadingEnabled = bionic;
                    PageLayer.BionicFadeIntensity = vm.Config.BionicFadeIntensity;
                    PageLayer.BionicFadeRects = bionic && tab is not null
                        ? tab.State.GetOrComputeBionicOverlay(tab.CurrentPage, vm.Config.BionicFixationPercent)
                        : null;
                    PageLayer.InvalidateVisual();
                    Minimap.InvalidateVisual();
                },
                InvalidateOverlay = () =>
                {
                    OverlayLayer.ActiveEffect = vm.Controller.ActiveColourEffect;
                    OverlayLayer.LineFocusBlurActive = vm.ActiveTab?.LineFocusBlur ?? false;
                    OverlayLayer.Tint = vm.Config.LineHighlightTint;
                    OverlayLayer.TintOpacity = vm.Config.LineHighlightOpacity;
                    OverlayLayer.InvalidateVisual();
                },
                InvalidateSearch = () =>
                {
                    SearchLayer.InvalidateVisual();
                    OutlinePanel.OnSearchInvalidated();
                },
                InvalidateAnnotations = () => AnnotationLayer.InvalidateVisual(),
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

            UpdateLayerBindings(vm.ActiveTab);
            SetupRadialMenu(vm);
            RailToolBar.ViewModel = vm;
            RailToolBar.SyncFromConfig();

            vm.ReadSidePanelWidth = () =>
            {
                var w = MainGrid.ColumnDefinitions[0].Width;
                return w.Value > 0 ? w.Value : 220;
            };
            UpdateSidebarColumnWidth(vm.ShowOutline);

            // window.Opened (which calls OpenDocument) can fire before OnLoaded
            // finishes wiring _invalidation. If a tab is already present, the
            // camera transform was never applied and CenterPage used the wrong
            // viewport size. Re-center and apply now that layout is complete.
            if (vm.ActiveTab is { } earlyTab && Viewport.Bounds.Width > 0)
            {
                earlyTab.CenterPage(Viewport.Bounds.Width, Viewport.Bounds.Height);
                earlyTab.UpdateRailZoom(Viewport.Bounds.Width, Viewport.Bounds.Height);
                UpdateCameraTransform();
                PageLayer.InvalidateVisual();
            }

            vm.PropertyChanged += async (_, args) =>
            {
                switch (args.PropertyName)
                {
                    case nameof(MainWindowViewModel.ActiveTab):
                        UpdateLayerBindings(vm.ActiveTab);
                        UpdateCameraTransform();
                        UpdateRailToolBarVisibility();
                        PageLayer.InvalidateVisual();
                        SearchLayer.InvalidateVisual();
                        AnnotationLayer.InvalidateVisual();
                        OverlayLayer.InvalidateVisual();
                        Minimap.InvalidateVisual();
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
                        await new AboutDialog { FontSize = vm.CurrentFontSize }.ShowDialog(this);
                        break;
                    case nameof(MainWindowViewModel.ShowSettings) when vm.ShowSettings:
                        vm.ShowSettings = false;
                        await new SettingsWindow { DataContext = vm, FontSize = vm.CurrentFontSize }.ShowDialog(this);
                        break;
                    case nameof(MainWindowViewModel.ActiveTool):
                        Viewport.UpdateAnnotationCursor();
                        break;
                    case nameof(MainWindowViewModel.IsFullScreen):
                        WindowState = vm.IsFullScreen ? WindowState.FullScreen : WindowState.Normal;
                        SystemDecorations = vm.IsFullScreen ? SystemDecorations.None : SystemDecorations.Full;
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
                                OutlinePanel.UpdateBookmarkSource();
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
            };
        }
    }

    private void SetupRadialMenu(MainWindowViewModel vm)
    {
        RadialMenuControl.Scale = vm.Config.UiFontScale;

        vm.CopyToClipboard = async text =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
                await clipboard.SetTextAsync(text);
        };

        ToolBar.ViewModel = vm;

        var highlightColors = BuildColorOptions(vm, AnnotationTool.Highlight, DocumentController.HighlightColors, 0);
        var penColors = BuildColorOptions(vm, AnnotationTool.Pen, DocumentController.PenColors, 1);

        var segments = new List<RadialMenu.Segment>
        {
            new("Highlight", RadialMenu.IconChars.Highlighter,
                () => vm.SetAnnotationTool(AnnotationTool.Highlight),
                highlightColors, vm.Controller.GetAnnotationColorIndex(AnnotationTool.Highlight)),
            new("Pen", RadialMenu.IconChars.Pen,
                () => vm.SetAnnotationTool(AnnotationTool.Pen),
                penColors, vm.Controller.GetAnnotationColorIndex(AnnotationTool.Pen)),
            new("Text", RadialMenu.IconChars.TextHeight,
                () => vm.SetAnnotationTool(AnnotationTool.TextNote)),
            new("Rect", RadialMenu.IconChars.Square,
                () => vm.SetAnnotationTool(AnnotationTool.Rectangle)),
            new("Eraser", RadialMenu.IconChars.Eraser,
                () => vm.SetAnnotationTool(AnnotationTool.Eraser)),
        };
        RadialMenuControl.SetSegments(segments, onClose: () => vm.CloseRadialMenu());
    }

    private List<RadialMenu.ColorOption> BuildColorOptions(
        MainWindowViewModel vm, AnnotationTool tool,
        (string Color, float Opacity)[] palette, int segmentIndex)
    {
        var options = new List<RadialMenu.ColorOption>(palette.Length);
        for (int i = 0; i < palette.Length; i++)
        {
            int idx = i;
            var (color, opacity) = palette[i];
            options.Add(new RadialMenu.ColorOption(color, opacity, () =>
            {
                vm.Controller.SetAnnotationColorIndex(tool, idx);
                vm.SetAnnotationTool(tool);
                RadialMenuControl.UpdateSegmentColorIndex(segmentIndex, idx);
            }));
        }
        return options;
    }

    private void UpdateLayerBindings(TabViewModel? tab)
    {
        var config = Vm?.Config;
        var ctrl = Vm?.Controller;

        PageLayer.Tab = tab;
        PageLayer.ColourEffects = Vm?.ColourEffects;
        PageLayer.ActiveEffect = ctrl?.ActiveColourEffect ?? ColourEffect.None;
        PageLayer.ActiveIntensity = ctrl?.ActiveColourIntensity ?? 1.0f;
        PageLayer.MotionBlurEnabled = config?.MotionBlur ?? true;
        PageLayer.MotionBlurIntensity = config?.MotionBlurIntensity ?? 0.5;
        bool blur = tab?.LineFocusBlur ?? false;
        bool bionic = tab?.BionicReading ?? false;
        PageLayer.LineFocusBlurEnabled = blur;
        PageLayer.LineFocusBlurIntensity = config?.LineFocusBlurIntensity ?? 0.5;
        PageLayer.LineFocusPadding = config?.LineFocusPadding ?? 0.2;
        PageLayer.BionicReadingEnabled = bionic;
        PageLayer.BionicFadeIntensity = config?.BionicFadeIntensity ?? 0.6;
        PageLayer.BionicFadeRects = bionic && tab is not null
            ? tab.State.GetOrComputeBionicOverlay(tab.CurrentPage, config?.BionicFixationPercent ?? 0.4)
            : null;
        SearchLayer.Tab = tab;
        SearchLayer.ViewModel = Vm;
        AnnotationLayer.Tab = tab;
        AnnotationLayer.ViewModel = Vm;
        OverlayLayer.Tab = tab;
        OverlayLayer.ActiveEffect = ctrl?.ActiveColourEffect ?? ColourEffect.None;
        OverlayLayer.LineFocusBlurActive = tab?.LineFocusBlur ?? false;
        OverlayLayer.Tint = config?.LineHighlightTint ?? LineHighlightTint.Auto;
        OverlayLayer.TintOpacity = config?.LineHighlightOpacity ?? 0.25;

        if (tab is not null)
            tab.OnDpiRenderComplete = () => Vm?.RequestAnimationFrame();
    }

    private void UpdateRailToolBarVisibility()
    {
        bool shouldShow = Vm?.ActiveTab?.Rail.Active == true;
        bool wasVisible = RailToolBar.IsVisible;
        RailToolBar.IsVisible = shouldShow;

        // Persist config when toolbar hides (deferred save for slider changes)
        if (wasVisible && !shouldShow)
        {
            Vm?.Config.Save();
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

    private void UpdateCameraTransform()
    {
        var tab = Vm?.ActiveTab;
        if (tab is null)
        {
            CameraPanel.RenderTransform = null;
            PagePanel.Width = 0;
            PagePanel.Height = 0;
            UpdateRailToolBarVisibility();
            return;
        }

        CameraPanel.RenderTransform = new MatrixTransform(
            new Matrix(tab.Camera.Zoom, 0, 0, tab.Camera.Zoom,
                       tab.Camera.OffsetX, tab.Camera.OffsetY));
        PagePanel.Width = tab.PageWidth;
        PagePanel.Height = tab.PageHeight;

        // The minimap is ≤200×280px — sub-pixel viewport indicator movement is
        // invisible. Use thresholds large enough to skip redraws during smooth
        // scrolling frames where the visual change is imperceptible.
        if (Math.Abs(tab.Camera.OffsetX - _lastMinimapOx) > 8.0 ||
            Math.Abs(tab.Camera.OffsetY - _lastMinimapOy) > 8.0 ||
            Math.Abs(tab.Camera.Zoom - _lastMinimapZoom) > 0.02)
        {
            _lastMinimapOx = tab.Camera.OffsetX;
            _lastMinimapOy = tab.Camera.OffsetY;
            _lastMinimapZoom = tab.Camera.Zoom;
            Minimap.InvalidateVisual();
        }

        UpdateRailToolBarVisibility();
    }

    /// <summary>
    /// Override OnKeyDown to intercept keys during the tunneling phase,
    /// before child controls (outline TreeView, etc.) can swallow them.
    /// Rail navigation keys always take priority when rail mode is active.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Vm is not { } vm) { base.OnKeyDown(e); return; }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && HandleCtrlShortcut(vm, e))
            { RailToolBar.SyncState(); return; }

        // When the search TextBox has focus, let text input keys through.
        // Only intercept non-text keys (F-keys, Escape, PgUp/PgDn, etc.).
        bool searchFocused = vm.ShowOutline && OutlinePanel.IsSearchInputFocused;

        if (!searchFocused && HandleNavigationKey(vm, e))
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
                if (vm.ShowOutline && !OutlinePanel.IsBookmarksTabActive
                    && !OutlinePanel.IsSearchTabActive)
                    vm.ShowOutline = false;
                else
                {
                    vm.ShowOutline = true;
                    OutlinePanel.SwitchToOutlineTab();
                }
                e.Handled = true; return true;
            case Key.B when shift:
                if (vm.ShowOutline && OutlinePanel.IsBookmarksTabActive)
                    vm.ShowOutline = false;
                else
                {
                    vm.ShowOutline = true;
                    OutlinePanel.SwitchToBookmarksTab();
                }
                e.Handled = true; return true;
            case Key.O:
                _ = vm.OpenFileCommand.ExecuteAsync(null); e.Handled = true; return true;
            case Key.W: vm.CloseTab(vm.ActiveTabIndex); e.Handled = true; return true;
            case Key.Q: Close(); e.Handled = true; return true;
            case Key.M:
                vm.ShowMinimap = !vm.ShowMinimap; e.Handled = true; return true;
            case Key.OemComma:
                vm.ShowSettings = true; e.Handled = true; return true;
            case Key.F:
                vm.OpenSearch();
                e.Handled = true; return true;
            case Key.G:
                vm.ShowGoToPage = true; e.Handled = true; return true;
            case Key.Z when shift:
                vm.RedoAnnotation(); e.Handled = true; return true;
            case Key.Z:
                vm.UndoAnnotation(); e.Handled = true; return true;
            case Key.Y:
                vm.RedoAnnotation(); e.Handled = true; return true;
            case Key.C:
                if (vm.SelectedText is not null) vm.CopySelectedText();
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
                OutlinePanel.UpdateBookmarkSource();
                e.Handled = true; return true;
            case Key.F:
                vm.ToggleLineFocusBlur(); e.Handled = true; return true;
            case Key.R:
                vm.ToggleBionicReading(); e.Handled = true; return true;
            case Key.OemOpenBrackets when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                RailToolBar.AdjustBlur(-0.05); e.Handled = true; return true;
            case Key.OemCloseBrackets when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                RailToolBar.AdjustBlur(0.05); e.Handled = true; return true;
            case Key.OemOpenBrackets:
                RailToolBar.AdjustSpeed(-5); e.Handled = true; return true;
            case Key.OemCloseBrackets:
                RailToolBar.AdjustSpeed(5); e.Handled = true; return true;
            case Key.D when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                if (vm.ActiveTab is { } dbgTab)
                {
                    dbgTab.DebugOverlay = !dbgTab.DebugOverlay;
                    OverlayLayer.InvalidateVisual();
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
            case Key.Escape when vm.IsRadialMenuOpen:
                vm.CloseRadialMenu(); e.Handled = true; return true;
            case Key.Escape when vm.IsAnnotating:
                vm.CancelAnnotationTool(); e.Handled = true; return true;
            case Key.Escape when vm.ShowOutline && OutlinePanel.IsSearchTabActive:
                vm.CloseSearch();
                OutlinePanel.SwitchToOutlineTab();
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

        if (!e.Handled)
            base.OnKeyUp(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Vm is { IsFullScreen: true } vm)
        {
            var pos = e.GetPosition(this);
            if (!vm.ShowFullScreenHeader && pos.Y <= FullScreenShowThreshold)
                vm.ShowFullScreenHeader = true;
            else if (vm.ShowFullScreenHeader && pos.Y > FullScreenHideThreshold)
                vm.ShowFullScreenHeader = false;
        }
    }
}
