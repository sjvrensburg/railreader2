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
                    PageLayer.MotionBlurEnabled = vm.Config.MotionBlur;
                    PageLayer.MotionBlurIntensity = vm.Config.MotionBlurIntensity;
                    PageLayer.LineFocusBlurEnabled = vm.Config.LineFocusBlur;
                    PageLayer.LineFocusBlurIntensity = vm.Config.LineFocusBlurIntensity;
                    PageLayer.BionicReadingEnabled = vm.Config.BionicReading;
                    PageLayer.BionicFadeIntensity = vm.Config.BionicFadeIntensity;
                    PageLayer.BionicFadeRects = vm.Config.BionicReading && vm.ActiveTab is { } bionicTab
                        ? bionicTab.State.GetOrComputeBionicOverlay(bionicTab.CurrentPage, vm.Config.BionicFixationPercent)
                        : null;
                    PageLayer.InvalidateVisual();
                    Minimap.InvalidateVisual();
                },
                InvalidateOverlay = () =>
                {
                    OverlayLayer.LineFocusBlurActive = vm.Config.LineFocusBlur;
                    OverlayLayer.Tint = vm.Config.LineHighlightTint;
                    OverlayLayer.TintOpacity = vm.Config.LineHighlightOpacity;
                    OverlayLayer.InvalidateVisual();
                },
                InvalidateSearch = () => SearchLayer.InvalidateVisual(),
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
        PageLayer.Tab = tab;
        PageLayer.ColourEffects = Vm?.ColourEffects;
        PageLayer.MotionBlurEnabled = Vm?.Config.MotionBlur ?? true;
        PageLayer.MotionBlurIntensity = Vm?.Config.MotionBlurIntensity ?? 0.5;
        PageLayer.LineFocusBlurEnabled = Vm?.Config.LineFocusBlur ?? false;
        PageLayer.LineFocusBlurIntensity = Vm?.Config.LineFocusBlurIntensity ?? 0.5;
        SearchLayer.Tab = tab;
        SearchLayer.ViewModel = Vm;
        AnnotationLayer.Tab = tab;
        AnnotationLayer.ViewModel = Vm;
        OverlayLayer.Tab = tab;
        OverlayLayer.ColourEffects = Vm?.ColourEffects;
        OverlayLayer.LineFocusBlurActive = Vm?.Config.LineFocusBlur ?? false;
        OverlayLayer.Tint = Vm?.Config.LineHighlightTint ?? LineHighlightTint.Auto;
        OverlayLayer.TintOpacity = Vm?.Config.LineHighlightOpacity ?? 0.25;

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
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (ctrl)
        {
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            switch (e.Key)
            {
                case Key.O when shift:
                    if (vm.ShowOutline && !OutlinePanel.IsBookmarksTabActive)
                        vm.ShowOutline = false;
                    else
                    {
                        vm.ShowOutline = true;
                        OutlinePanel.SwitchToOutlineTab();
                    }
                    e.Handled = true; return;
                case Key.B when shift:
                    if (vm.ShowOutline && OutlinePanel.IsBookmarksTabActive)
                        vm.ShowOutline = false;
                    else
                    {
                        vm.ShowOutline = true;
                        OutlinePanel.SwitchToBookmarksTab();
                    }
                    e.Handled = true; return;
                case Key.O:
                    _ = vm.OpenFileCommand.ExecuteAsync(null); e.Handled = true; return;
                case Key.W: vm.CloseTab(vm.ActiveTabIndex); e.Handled = true; return;
                case Key.Q: Close(); e.Handled = true; return;
                case Key.M:
                    vm.ShowMinimap = !vm.ShowMinimap; e.Handled = true; return;
                case Key.OemComma:
                    vm.ShowSettings = true; e.Handled = true; return;
                case Key.F:
                    vm.OpenSearch();
                    SearchBar.FocusSearch();
                    e.Handled = true; return;
                case Key.G:
                    vm.ShowGoToPage = true; e.Handled = true; return;
                case Key.Z when shift:
                    vm.RedoAnnotation(); e.Handled = true; return;
                case Key.Z:
                    vm.UndoAnnotation(); e.Handled = true; return;
                case Key.Y:
                    vm.RedoAnnotation(); e.Handled = true; return;
                case Key.C:
                    if (vm.SelectedText is not null) vm.CopySelectedText();
                    e.Handled = true; return;
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

        // When the search TextBox has focus, let text input keys through.
        // Only intercept non-text keys (F-keys, Escape, PgUp/PgDn, etc.).
        bool searchFocused = vm.ShowSearch && SearchBar.IsSearchInputFocused;

        // Navigation keys always handled at window level.
        // Ctrl cases above already return, so ctrl is never held here.
        switch (e.Key)
        {
            case Key.Down when !searchFocused:
            case Key.S when !searchFocused:
                vm.HandleArrowDown(); e.Handled = true; break;
            case Key.Up when !searchFocused:
            case Key.W when !searchFocused:
                vm.HandleArrowUp(); e.Handled = true; break;
            case Key.Right when !searchFocused:
                vm.HandleArrowRight(e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true; break;
            case Key.Left when !searchFocused:
            case Key.A when !searchFocused:
                vm.HandleArrowLeft(e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true; break;
            case Key.P when !searchFocused:
                vm.ToggleAutoScrollExclusive();
                RailToolBar.SetJumpMode(vm.JumpMode);
                RailToolBar.UpdateToggleStates();
                e.Handled = true; break;
            case Key.J when !searchFocused:
                vm.ToggleJumpModeExclusive();
                RailToolBar.SetJumpMode(vm.JumpMode);
                RailToolBar.UpdateToggleStates();
                e.Handled = true; break;
            case Key.C when !searchFocused:
            {
                var effect = vm.CycleColourEffect();
                string name = effect switch
                {
                    ColourEffect.None => "None",
                    ColourEffect.HighContrast => "High Contrast",
                    ColourEffect.HighVisibility => "High Visibility",
                    ColourEffect.Amber => "Amber Filter",
                    ColourEffect.Invert => "Invert",
                    _ => effect.ToString(),
                };
                vm.ShowStatusToast($"Colour: {name}");
                e.Handled = true; break;
            }
            case Key.B when !searchFocused:
                vm.ShowBookmarkDialog = true;
                e.Handled = true; break;
            case Key.OemTilde when !searchFocused:
                vm.NavigateBack();
                OutlinePanel.UpdateBookmarkSource();
                e.Handled = true; break;
            case Key.F when !searchFocused:
                vm.ToggleLineFocusBlur();
                RailToolBar.UpdateToggleStates();
                e.Handled = true; break;
            case Key.R when !searchFocused:
                vm.ToggleBionicReading();
                e.Handled = true; break;
            case Key.OemOpenBrackets when !searchFocused && e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                RailToolBar.AdjustBlur(-0.05); e.Handled = true; break;
            case Key.OemCloseBrackets when !searchFocused && e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                RailToolBar.AdjustBlur(0.05); e.Handled = true; break;
            case Key.OemOpenBrackets when !searchFocused:
                RailToolBar.AdjustSpeed(-5); e.Handled = true; break;
            case Key.OemCloseBrackets when !searchFocused:
                RailToolBar.AdjustSpeed(5); e.Handled = true; break;
            case Key.D when !searchFocused && e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                if (vm.ActiveTab is { } dbgTab)
                {
                    dbgTab.DebugOverlay = !dbgTab.DebugOverlay;
                    OverlayLayer.InvalidateVisual();
                }
                e.Handled = true; break;
            case Key.D when !searchFocused:
                vm.HandleArrowRight(); e.Handled = true; break;
            case Key.PageDown:
                if (vm.ActiveTab is { } t1) vm.GoToPage(t1.CurrentPage + 1);
                e.Handled = true; break;
            case Key.PageUp:
                if (vm.ActiveTab is { } t2) vm.GoToPage(t2.CurrentPage - 1);
                e.Handled = true; break;
            case Key.Home when !searchFocused:
                if (vm.ActiveTab is { } tH && tH.Rail.Active)
                    vm.HandleLineHome();
                else
                    vm.GoToPage(0);
                e.Handled = true; break;
            case Key.End when !searchFocused:
                if (vm.ActiveTab is { } tE2 && tE2.Rail.Active)
                    vm.HandleLineEnd();
                else if (vm.ActiveTab is { } tE3)
                    vm.GoToPage(tE3.PageCount - 1);
                e.Handled = true; break;
            case Key.OemPlus or Key.Add when !searchFocused:
                vm.HandleZoomKey(true); e.Handled = true; break;
            case Key.OemMinus or Key.Subtract when !searchFocused:
                vm.HandleZoomKey(false); e.Handled = true; break;
            case Key.D0 or Key.NumPad0 when !searchFocused:
                vm.HandleResetZoom(); e.Handled = true; break;
            case Key.Space when !searchFocused:
                vm.HandleArrowDown(); e.Handled = true; break;
            case Key.F1:
                vm.ShowShortcuts = true; e.Handled = true; break;
            case Key.F3 when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                vm.PreviousMatch(); e.Handled = true; break;
            case Key.F3:
                vm.NextMatch(); e.Handled = true; break;
            case Key.D1 when !searchFocused:
                vm.SetAnnotationTool(AnnotationTool.Highlight); e.Handled = true; break;
            case Key.D2 when !searchFocused:
                vm.SetAnnotationTool(AnnotationTool.Pen); e.Handled = true; break;
            case Key.D3 when !searchFocused:
                vm.SetAnnotationTool(AnnotationTool.Rectangle); e.Handled = true; break;
            case Key.D4 when !searchFocused:
                vm.SetAnnotationTool(AnnotationTool.TextNote); e.Handled = true; break;
            case Key.D5 when !searchFocused:
                vm.SetAnnotationTool(AnnotationTool.Eraser); e.Handled = true; break;
            case Key.Delete or Key.Back when !searchFocused && !vm.IsAnnotating && vm.SelectedAnnotation is not null:
                vm.DeleteSelectedAnnotation(); e.Handled = true; break;
            case Key.F11:
                vm.IsFullScreen = !vm.IsFullScreen; e.Handled = true; break;
            case Key.Escape when vm.AutoScrollActive:
                vm.StopAutoScroll(); RailToolBar.UpdateToggleStates(); e.Handled = true; break;
            case Key.Escape when vm.IsFullScreen:
                vm.IsFullScreen = false; e.Handled = true; break;
            case Key.Escape when vm.IsRadialMenuOpen:
                vm.CloseRadialMenu(); e.Handled = true; break;
            case Key.Escape when vm.IsAnnotating:
                vm.CancelAnnotationTool(); e.Handled = true; break;
            case Key.Escape when vm.ShowSearch:
                vm.CloseSearch(); e.Handled = true; break;
        }

        if (!e.Handled)
            base.OnKeyDown(e);
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
}
