using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Renderer.Skia;
using RailReader2.Controls;
using RailReader2.ViewModels;
using SkiaSharp;

namespace RailReader2.Views;

public partial class MainWindow : Window
{
    private double _lastMinimapOx;
    private double _lastMinimapOy;
    private double _lastMinimapZoom;
    private SKImage? _lastMinimapImage;
    private MainWindowViewModel? _subscribedVm;

    // Fullscreen hover reveal: show threshold < hide threshold for hysteresis
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

            // Wire granular invalidation callbacks.
            // Each callback builds an immutable state snapshot on the UI thread
            // and sends it to the appropriate CompositionCustomVisual handler,
            // which re-renders on the next compositor frame.
            vm.SetInvalidation(new InvalidationCallbacks
            {
                InvalidateCamera = () =>
                {
                    UpdatePagePanelSize(vm.ActiveTab);
                    StatusBar.UpdateZoom();
                    UpdateAllLayers(vm, vm.ActiveTab);
                },
                InvalidatePage = () =>
                {
                    var tab = vm.ActiveTab;
                    var state = BuildPageState(vm, tab);
                    PageLayer.UpdateState(state);
                    // Update minimap when the page image itself changes
                    if (!ReferenceEquals(state.Image, _lastMinimapImage))
                    {
                        _lastMinimapImage = state.Image;
                        Minimap.InvalidateVisual();
                    }
                },
                InvalidateOverlay = () =>
                    OverlayLayer.UpdateState(BuildOverlayState(vm, vm.ActiveTab)),
                InvalidateSearch = () =>
                {
                    SearchLayer.UpdateState(BuildSearchState(vm, vm.ActiveTab));
                    OutlinePanel.OnSearchInvalidated();
                },
                InvalidateAnnotations = () =>
                    AnnotationLayer.UpdateState(BuildAnnotationState(vm, vm.ActiveTab)),
            });

            // Keep ViewModel's viewport size in sync with the actual drawable area.
            // SizeChanged fires during the initial layout pass (before window.Opened),
            // so OpenDocument will already see correct dimensions when it runs.
            vm.SetViewportSize(Viewport.Bounds.Width, Viewport.Bounds.Height);
            Viewport.SizeChanged += OnViewportSizeChanged;

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
            // camera state was never sent and CenterPage used the wrong viewport size.
            // Re-center and push fresh state to all layers now that layout is complete.
            if (vm.ActiveTab is { } earlyTab && Viewport.Bounds.Width > 0)
            {
                earlyTab.CenterPage(Viewport.Bounds.Width, Viewport.Bounds.Height);
                earlyTab.UpdateRailZoom(Viewport.Bounds.Width, Viewport.Bounds.Height);
                UpdatePagePanelSize(earlyTab);
                UpdateLayerBindings(earlyTab);
            }

            _subscribedVm = vm;
            vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    protected override void OnUnloaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_subscribedVm is { } vm)
        {
            vm.PropertyChanged -= OnVmPropertyChanged;
            Viewport.SizeChanged -= OnViewportSizeChanged;
            _subscribedVm = null;
        }
        base.OnUnloaded(e);
    }

    private void OnViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.SetViewportSize(Viewport.Bounds.Width, Viewport.Bounds.Height);
        if (vm.ActiveTab is { } tab)
        {
            var (ww, wh) = (Viewport.Bounds.Width, Viewport.Bounds.Height);
            tab.ClampCamera(ww, wh);
            UpdatePagePanelSize(tab);
            UpdateAllLayers(vm, tab);
        }
    }

    private async void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (Vm is not { } vm) return;
        switch (args.PropertyName)
        {
            case nameof(MainWindowViewModel.ActiveTab):
                UpdateLayerBindings(vm.ActiveTab);
                UpdatePagePanelSize(vm.ActiveTab);
                UpdateRailToolBarVisibility();
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
                var aboutDlg = new AboutDialog { FontSize = vm.CurrentFontSize };
                aboutDlg.SetLogFilePath(vm.LogFilePath);
                await aboutDlg.ShowDialog(this);
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

        var highlightColors = BuildColorOptions(vm, AnnotationTool.Highlight, AnnotationInteractionHandler.HighlightColors, 0);
        var penColors = BuildColorOptions(vm, AnnotationTool.Pen, AnnotationInteractionHandler.PenColors, 1);
        var rectColors = BuildColorOptions(vm, AnnotationTool.Rectangle, AnnotationInteractionHandler.RectColors, 3);
        var penThickness = BuildThicknessOptions(vm, AnnotationTool.Pen, 1);
        var rectThickness = BuildThicknessOptions(vm, AnnotationTool.Rectangle, 3);

        var segments = new List<RadialMenu.Segment>
        {
            new("Highlight", RadialMenu.IconChars.Highlighter,
                () => vm.SetAnnotationTool(AnnotationTool.Highlight),
                highlightColors, vm.Controller.Annotations.GetAnnotationColorIndex(AnnotationTool.Highlight)),
            new("Pen", RadialMenu.IconChars.Pen,
                () => vm.SetAnnotationTool(AnnotationTool.Pen),
                penColors, vm.Controller.Annotations.GetAnnotationColorIndex(AnnotationTool.Pen),
                penThickness, vm.Controller.Annotations.GetThicknessIndex(AnnotationTool.Pen)),
            new("Text", RadialMenu.IconChars.TextHeight,
                () => vm.SetAnnotationTool(AnnotationTool.TextNote)),
            new("Rect", RadialMenu.IconChars.Square,
                () => vm.SetAnnotationTool(AnnotationTool.Rectangle),
                rectColors, vm.Controller.Annotations.GetAnnotationColorIndex(AnnotationTool.Rectangle),
                rectThickness, vm.Controller.Annotations.GetThicknessIndex(AnnotationTool.Rectangle)),
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
                vm.Controller.Annotations.SetAnnotationColorIndex(tool, idx);
                vm.SetAnnotationTool(tool);
                RadialMenuControl.UpdateSegmentColorIndex(segmentIndex, idx);
            }));
        }
        return options;
    }

    private List<RadialMenu.ThicknessOption> BuildThicknessOptions(
        MainWindowViewModel vm, AnnotationTool tool, int segmentIndex)
    {
        var presets = AnnotationInteractionHandler.ThicknessPresets;
        var options = new List<RadialMenu.ThicknessOption>(presets.Length);
        for (int i = 0; i < presets.Length; i++)
        {
            int idx = i;
            options.Add(new RadialMenu.ThicknessOption(presets[i], () =>
            {
                vm.Controller.Annotations.SetThicknessIndex(tool, idx);
                RadialMenuControl.UpdateSegmentThicknessIndex(segmentIndex, idx);
            }));
        }
        return options;
    }

    private void UpdateLayerBindings(TabViewModel? tab)
    {
        if (Vm is not { } vm) return;
        UpdateAllLayers(vm, tab);

        if (tab is not null)
            tab.OnDpiRenderComplete = () => vm.RequestAnimationFrame();
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

    /// <summary>
    /// Updates PagePanel dimensions (used by the minimap and scrollbar calculations)
    /// and conditionally invalidates the minimap when the viewport position changes
    /// enough to be visible at its small display size.
    /// The camera transform itself is now applied inside each layer's Skia canvas.
    /// </summary>
    private void UpdatePagePanelSize(TabViewModel? tab)
    {
        if (tab is null)
        {
            PagePanel.Width = 0;
            PagePanel.Height = 0;
            return;
        }

        PagePanel.Width = tab.PageWidth;
        PagePanel.Height = tab.PageHeight;

        // The minimap is ≤200×280px — sub-pixel viewport indicator movement is
        // invisible. Use thresholds large enough to skip redraws during smooth
        // scrolling frames where the visual change is imperceptible.
        if (Math.Abs(tab.Camera.OffsetX - _lastMinimapOx) > 24.0 ||
            Math.Abs(tab.Camera.OffsetY - _lastMinimapOy) > 24.0 ||
            Math.Abs(tab.Camera.Zoom - _lastMinimapZoom) > 0.02)
        {
            _lastMinimapOx = tab.Camera.OffsetX;
            _lastMinimapOy = tab.Camera.OffsetY;
            _lastMinimapZoom = tab.Camera.Zoom;
            Minimap.InvalidateVisual();
        }
    }

    /// <summary>
    /// Sends fresh state to all four composition layer handlers.
    /// </summary>
    private void UpdateAllLayers(MainWindowViewModel vm, TabViewModel? tab)
    {
        PageLayer.UpdateState(BuildPageState(vm, tab));
        OverlayLayer.UpdateState(BuildOverlayState(vm, tab));
        SearchLayer.UpdateState(BuildSearchState(vm, tab));
        AnnotationLayer.UpdateState(BuildAnnotationState(vm, tab));
    }

    // ── State builders ─────────────────────────────────────────────────────────

    private static SKMatrix BuildCamera(TabViewModel? tab)
    {
        if (tab is null) return SKMatrix.Identity;
        float zoom = (float)tab.Camera.Zoom;
        return SKMatrix.CreateScaleTranslation(
            zoom, zoom, (float)tab.Camera.OffsetX, (float)tab.Camera.OffsetY);
    }

    private PdfPageRenderState BuildPageState(MainWindowViewModel vm, TabViewModel? tab)
    {
        float lineY = 0, lineH = 0;
        if (tab?.Rail is { Active: true, NavigableCount: > 0 })
        {
            var line = tab.Rail.CurrentLineInfo;
            lineY = line.Y;
            lineH = line.Height;
        }
        var (image, retired) = tab?.GetCachedImage() ?? (null, null);
        if (retired is not null)
        {
            // Send the retired image to the composition thread for safe disposal.
            // If the layer is detached (visual gone), the message is silently dropped,
            // so dispose immediately on the UI thread as a fallback.
            if (!PageLayer.TrySendMessage(new RetireImage(retired)))
                retired.Dispose();
        }
        return new PdfPageRenderState(
            Image: image,
            PageW: (float)(tab?.PageWidth ?? 0),
            PageH: (float)(tab?.PageHeight ?? 0),
            Camera: BuildCamera(tab),
            ScrollSpeed: (float)(tab?.Rail.ScrollSpeed ?? 0),
            ZoomSpeed: (float)(tab?.Camera.ZoomSpeed ?? 0),
            MotionBlur: vm.Config.MotionBlur,
            MotionBlurIntensity: (float)vm.Config.MotionBlurIntensity,
            LineFocusBlur: tab?.LineFocusBlur ?? false,
            LineFocusIntensity: (float)vm.Config.LineFocusBlurIntensity,
            LinePadding: (float)vm.Config.LinePadding,
            LineY: lineY,
            LineH: lineH,
            Effect: vm.Controller.ActiveColourEffect,
            EffectIntensity: vm.Controller.ActiveColourIntensity,
            Effects: vm.ColourEffects);
    }

    private static RailOverlayRenderState BuildOverlayState(MainWindowViewModel vm, TabViewModel? tab)
    {
        LayoutBlock? currentBlock = null;
        LineInfo currentLine = default;
        if (tab?.Rail is { Active: true, HasAnalysis: true } rail && rail.NavigableCount > 0)
        {
            currentBlock = rail.CurrentNavigableBlock;
            currentLine = rail.CurrentLineInfo;
        }
        PageAnalysis? debugAnalysis = null;
        if (tab?.DebugOverlay == true)
            tab.AnalysisCache.TryGetValue(tab.CurrentPage, out debugAnalysis);

        return new RailOverlayRenderState(
            Camera: BuildCamera(tab),
            PageW: (float)(tab?.PageWidth ?? 0),
            PageH: (float)(tab?.PageHeight ?? 0),
            CurrentBlock: currentBlock,
            CurrentLine: currentLine,
            DebugOverlay: tab?.DebugOverlay ?? false,
            DebugAnalysis: debugAnalysis,
            Effect: vm.Controller.ActiveColourEffect,
            LineFocusBlur: tab?.LineFocusBlur ?? false,
            LineHighlightEnabled: tab?.LineHighlightEnabled ?? true,
            LinePadding: (float)vm.Config.LinePadding,
            Tint: vm.Config.LineHighlightTint,
            TintOpacity: (float)vm.Config.LineHighlightOpacity);
    }

    private static AnnotationRenderState BuildAnnotationState(MainWindowViewModel vm, TabViewModel? tab)
    {
        List<Annotation>? pageAnnotations = null;
        if (tab is not null)
            tab.Annotations.Pages.TryGetValue(tab.CurrentPage, out pageAnnotations);

        return new AnnotationRenderState(
            Camera: BuildCamera(tab),
            PageAnnotations: pageAnnotations,
            SelectedAnnotation: vm.SelectedAnnotation,
            PreviewAnnotation: vm.PreviewAnnotation,
            TextSelectionRects: vm.TextSelectionRects);
    }

    private SearchRenderState BuildSearchState(MainWindowViewModel vm, TabViewModel? tab)
    {
        var matches = vm.CurrentPageSearchMatches;
        int activeLocalIndex = -1;
        if (matches is { Count: > 0 } && tab is not null)
            activeLocalIndex = OverlayRenderer.ComputeActiveLocalIndex(
                vm.SearchMatches, matches, vm.ActiveMatchIndex, tab.CurrentPage);

        return new SearchRenderState(
            Camera: BuildCamera(tab),
            Matches: matches,
            ActiveLocalIndex: activeLocalIndex);
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

        // Alt+Arrow: navigation history (back/forward)
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            if (e.Key == Key.Left) { vm.NavigateBack(); e.Handled = true; return; }
            if (e.Key == Key.Right) { vm.NavigateForward(); e.Handled = true; return; }
        }

        // When the search TextBox has focus, let text input keys through.
        // Only intercept non-text keys (F-keys, Escape, PgUp/PgDn, etc.).
        bool textInputFocused = (vm.ShowOutline && OutlinePanel.IsSearchInputFocused)
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
                    OverlayLayer.UpdateState(BuildOverlayState(vm, dbgTab));
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
        if (Vm is { IsFullScreen: true } vm)
        {
            var pos = e.GetPosition(this);

            // Top edge: tab bar reveal
            if (!vm.ShowFullScreenHeader && pos.Y <= FullScreenShowThreshold)
                vm.ShowFullScreenHeader = true;
            else if (vm.ShowFullScreenHeader && pos.Y > FullScreenHideThreshold)
                vm.ShowFullScreenHeader = false;

            // Bottom edge: status bar reveal
            double distFromBottom = Bounds.Height - pos.Y;
            if (!vm.ShowFullScreenFooter && distFromBottom <= FullScreenShowThreshold)
                vm.ShowFullScreenFooter = true;
            else if (vm.ShowFullScreenFooter && distFromBottom > FullScreenHideThreshold)
                vm.ShowFullScreenFooter = false;
        }
    }
}
