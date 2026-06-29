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
    // The live confined DocumentView hosted in the pop-out (Core 0.45.0 FocusBlock). Created when the
    // window opens over a pinned target, re-aimed when the target changes, torn down on dock/close/
    // tab-switch. Shares the secondary-surface lifecycle (BuildSecondarySurface/DisposeSecondarySurface)
    // with split panes and tear-offs (#192).
    private DocumentView? _portalView;
    // The (page, block) the portal view is currently aimed at — so a re-sync for the SAME target leaves
    // the user's pan/zoom/rail inside the portal undisturbed; only a different target re-frames.
    private (int Page, int Block)? _portalAimed;

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
            vm.RegisterSurface(Document); // the primary, always-present docked surface
            InitPanes(vm);
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
            vm.PortalViewChanged += OnPortalViewChanged;
            vm.PortalViewTeardownRequested += OnPortalViewTeardownRequested;
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
        // Layer rendering is delegated to the viewport surfaces (each DocumentView owns its layers and
        // builds state for its own viewport). A document-wide invalidation broadcasts to every live
        // surface (primary pane + split panes + tear-off windows); cross-cutting chrome (status bar,
        // search panel) stays here. The per-frame loop applies each surface's own TickResult directly.
        InvalidateCamera = () =>
        {
            foreach (var s in vm.Surfaces) s.RenderCamera();
            StatusBar.UpdateZoom();
        },
        InvalidatePage = () => { foreach (var s in vm.Surfaces) s.RenderPage(); },
        InvalidateOverlay = () => { foreach (var s in vm.Surfaces) s.RenderOverlay(); },
        // The Search pane refreshes its own "N of M" display via the VM's
        // SearchInvalidated event; here we only repaint the highlight layer.
        InvalidateSearch = () => { foreach (var s in vm.Surfaces) s.RenderSearch(); },
        InvalidateAnnotations = () => { foreach (var s in vm.Surfaces) s.RenderAnnotations(); },
        InvalidatePortalMarkers = () => { foreach (var s in vm.Surfaces) s.RenderPortalMarkers(); },
        AnnounceAccessibility = () => { foreach (var s in vm.Surfaces) s.NotifyAccessibility(); },
        UpdateZoomDisplay = () => StatusBar.UpdateZoom(),
    };

    protected override void OnUnloaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Close the detached portal window first — a lingering non-modal window would keep the app
        // process alive after the main window unloads.
        ClosePortalWindow();
        if (_subscribedVm is { } vm)
        {
            TeardownPanes(vm); // closes tear-off windows + unsubscribes pane commands
            vm.PropertyChanged -= OnVmPropertyChanged;
            vm.ViewportFocusRequested -= OnViewportFocusRequested;
            vm.PortalViewChanged -= OnPortalViewChanged;
            vm.PortalViewTeardownRequested -= OnPortalViewTeardownRequested;
            Document.Teardown();
            _subscribedVm = null;
        }
        base.OnUnloaded(e);
    }

    // --- Detached portal window (Phase 2) ---

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Close the floating portal window before the Closing-wired vm.Dispose() runs
        // DisposePortalImages — otherwise the still-open window would briefly hold disposed bitmaps as
        // its Image.Source. ClosePortalWindow is idempotent (OnUnloaded also calls it).
        ClosePortalWindow();
        base.OnClosing(e);
    }

    private void UpdatePortalWindow(MainWindowViewModel vm)
    {
        if (vm.ShouldShowPortalWindow)
        {
            // Open the window once (do NOT Activate() an already-open one — that would steal focus back
            // to the floating window on every peek change; live font-scale reaches it via CurrentFontSize).
            if (_portalWindow is null)
            {
                var win = new PortalWindow { DataContext = vm };
                win.ApplyFontScale(vm.CurrentFontSize);
                var settings = Services.PortalWindowSettings.Load();
                win.Width = settings.Width;     // Load() guarantees a positive size (defaults live there)
                win.Height = settings.Height;
                win.SyncTopmostToggle(settings.Topmost);
                if (settings.HasPosition)
                {
                    win.WindowStartupLocation = WindowStartupLocation.Manual;
                    win.Position = new PixelPoint(settings.X, settings.Y);
                }
                win.Closed += OnPortalWindowClosed;
                // Forward keys to the shared handler (acts on the focused viewport) so rail/freeze/
                // annotation shortcuts work in the portal once it's focused — same as a tear-off window.
                win.KeyHandler = e => TryHandleKey(vm, e);
                win.KeyUpHandler = e => TryHandleKeyUp(vm, e);
                // Any click in the portal window focuses its live viewport, so the floating toolbar +
                // keys target the portal (not the main view). Tunnel so it runs before child handlers;
                // never marks the event handled, so the chrome drag / buttons still work.
                win.AddHandler(InputElement.PointerPressedEvent, OnPortalPointerPressed,
                    Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
                _portalWindow = win;
                // Non-modal, no owner so the user can drag it to another monitor and keep it on top.
                win.Show();
            }
            // Host / re-aim the live confined viewport for the current target (or fall back to the crop).
            SyncPortalView(vm);
        }
        else
        {
            ClosePortalWindow();
        }
    }

    private bool _portalSyncQueued;

    // The VM is about to dispose a model that owns the live portal viewport — tear the hosted view down
    // synchronously first (unregister surface + remove viewport while the model is still alive).
    private void OnPortalViewTeardownRequested()
    {
        if (Vm is { } vm) TeardownPortalView(vm);
    }

    private void OnPortalViewChanged()
    {
        if (Vm is not { } vm || _portalWindow is null) return;
        // Re-aiming an ALREADY-hosted portal view to a changed target touches only that viewport's
        // camera/focus — never the _surfaces registry — so it's safe to run synchronously even mid-frame,
        // and it MUST: a pin fires from EvaluatePortals inside the tick, and deferring the re-aim at
        // Background priority starves it during continuous rail reading (the frame clock keeps the
        // dispatcher busy above Background), freezing the pop-out on the first float until reading stops.
        // Try the in-frame re-aim first; fall back to the deferred sync only when STRUCTURAL work
        // (create / teardown / tab-switch) is needed, which can't safely mutate _surfaces mid-frame.
        if (vm.IsInAnimationFrame && TryReaimHostedPortalView(vm)) return;
        if (vm.IsInAnimationFrame) QueuePortalSync();
        else SyncPortalView(vm);
    }

    /// <summary>Fast path for <see cref="OnPortalViewChanged"/>: if the portal view is already hosted on
    /// the current target's document, re-aim it to the (possibly changed) target and return true. Returns
    /// false when STRUCTURAL work is needed instead — no view yet (create), the target was cleared
    /// (teardown), or the active document changed (tab-switch rebuild) — which the caller defers out of
    /// the frame. Safe mid-frame: never registers/unregisters a surface.</summary>
    private bool TryReaimHostedPortalView(MainWindowViewModel vm)
    {
        if (_portalView is not { } view || view.SurfaceViewport?.Owner is not { } owner) return false;
        if (vm.ComputePortalLiveTarget() is not { } t || vm.PortalReadingDoc is not { } doc
            || !ReferenceEquals(owner, doc))
            return false;
        ReaimPortalView(vm, t);
        return true;
    }

    /// <summary>Aim the hosted portal viewport at <paramref name="t"/>, but only when the target actually
    /// changed — so a re-sync for the SAME target leaves the user's pan/zoom/rail inside the portal
    /// undisturbed. Shared by the in-frame fast path and the deferred <see cref="SyncPortalView"/>.</summary>
    private void ReaimPortalView(MainWindowViewModel vm, (int Page, int Block, RailReader.Core.Models.BBox Bounds) t)
    {
        if (_portalAimed == (t.Page, t.Block)) return;
        _portalAimed = (t.Page, t.Block);
        vm.AimPortalViewport(t);
        vm.RequestAnimationFrame();
    }

    /// <summary>Defer the (structural) portal-view sync to a dispatcher job, coalescing bursts. The
    /// trigger (EvaluatePortals → pin) often fires from inside the animation frame, where creating /
    /// registering / hosting a surface synchronously is unsafe (re-entrancy + collection mutation). A
    /// Background-priority post runs it cleanly between frames.</summary>
    private void QueuePortalSync()
    {
        if (_portalSyncQueued) return;
        _portalSyncQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _portalSyncQueued = false;
            if (Vm is { } vm) SyncPortalView(vm);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    // Focus the portal's live viewport on any click in the pop-out, so its floating toolbar (annotate /
    // freeze / start-rail-here) and forwarded keys act on the portal rather than the main view. The
    // reading-position sync is unaffected (EvaluatePortals tracks doc.Primary, never the focused view).
    private void OnPortalPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Input focus only — wireReadingSignals:false keeps a11y + the portal pin loop tracking the main
        // reading view (the portal is a confined satellite, not the document's reading position).
        if (Vm is { } vm && _portalView is { SurfaceViewport: { } vp })
            vm.FocusSurface(_portalView, vp, wireReadingSignals: false);
    }

    /// <summary>Reconcile the pop-out's live confined viewport with the current portal target: host a
    /// DocumentView (bound to a fresh viewport on the active model) when a target is pinned, re-aim it
    /// when the target CHANGES (leaving user pan/zoom alone otherwise), rebuild it on a tab switch, and
    /// tear it down when nothing is pinned. No-op when the window is closed.</summary>
    private void SyncPortalView(MainWindowViewModel vm)
    {
        var target = _portalWindow is null ? null : vm.ComputePortalLiveTarget();
        if (target is not { } t || vm.PortalReadingDoc is not { } doc)
        {
            TeardownPortalView(vm);
            return;
        }

        // Tab switch: the existing portal viewport sits on the previous model — rebuild on the new one.
        if (_portalView is { } pv && pv.SurfaceViewport?.Owner is { } owner && !ReferenceEquals(owner, doc))
            TeardownPortalView(vm);

        if (_portalView is null)
        {
            var vp = vm.CreatePortalViewport(doc);
            // Same shared surface core as split panes / tear-offs (#192) — but NOT focused on create: the
            // confined portal takes input focus only on click (OnPortalPointerPressed), so the
            // reading-position sync keeps tracking the main view.
            if (BuildSecondarySurface(vp) is not { } view)
            {
                // No active tab (unreachable here): no surface was built to dispose, so remove the bare
                // viewport directly before dropping the VM ref (#8 — TeardownPortalViewport no longer removes).
                vm.SafeRemoveViewport(vp);
                vm.TeardownPortalViewport();
                return;
            }
            _portalView = view;
            _portalWindow!.Host(view);
            vm.UpdatePortalLive(true);
            _portalAimed = null;
        }

        ReaimPortalView(vm, t);
    }

    private void TeardownPortalView(MainWindowViewModel vm)
    {
        vm.UpdatePortalLive(false);
        _portalAimed = null;
        if (_portalView is not { } view) return;
        // If the user had clicked into the portal, it is the focused viewport — re-home focus to the
        // main surface after removing it so input doesn't dangle on a removed viewport.
        bool wasFocused = ReferenceEquals(vm.Controller.FocusedViewport, view.SurfaceViewport);
        _portalView = null;
        _portalWindow?.Unhost();
        DisposeSecondarySurface(view);   // shared unregister + image-retire + viewport-remove (#192)
        vm.TeardownPortalViewport();     // drop the VM's _portalViewport ref (surface dispose did the remove, #8)
        if (wasFocused && Document.SurfaceViewport is { } primaryVp)
            vm.FocusSurface(Document, primaryVp);
    }

    // The window's own teardown (covers app shutdown). The dock path goes through ClosePortalWindow,
    // which unsubscribes first, so this only runs on an externally-driven close.
    private void OnPortalWindowClosed(object? sender, EventArgs e)
    {
        // Tear down the live confined viewport while the window (and its host panel) still exist.
        if (Vm is { } vm0) TeardownPortalView(vm0);
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
        if (Vm is { } vm) TeardownPortalView(vm);   // before _portalWindow is nulled (Unhost needs it)
        SavePortalWindowGeometry(win);
        win.Closed -= OnPortalWindowClosed;
        _portalWindow = null;
        win.Close();
    }

    private static void SavePortalWindowGeometry(PortalWindow win)
    {
        // Persist the actual rendered size (Bounds), restored into Width/Height on the next pop-out.
        // For a borderless window these match, so the window no longer drifts smaller each cycle the
        // way saving ClientSize and reloading into Width/Height could.
        new Services.PortalWindowSettings
        {
            X = win.Position.X,
            Y = win.Position.Y,
            Width = win.Bounds.Width > 0 ? win.Bounds.Width : win.Width,
            Height = win.Bounds.Height > 0 ? win.Bounds.Height : win.Height,
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
                CollapseExtrasIfDocumentChanged(vm); // split panes / tear-offs belong to one document
                // ActiveTab is re-raised from INSIDE the animation frame (per-frame overlay/page change),
                // not only on real tab switches — defer the structural portal-surface sync out of the
                // frame (mirrors OnPortalViewChanged) so it can't tear down / rebuild the surface mid-tick.
                if (vm.IsInAnimationFrame) QueuePortalSync();
                else SyncPortalView(vm); // rebuild the portal viewport on the now-active model (or tear down)
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
            case nameof(MainWindowViewModel.CurrentFontSize):
                // Live UI font-scale change from Settings — reach the open pop-out + tear-off windows too.
                _portalWindow?.ApplyFontScale(vm.CurrentFontSize);
                foreach (var win in _documentWindows) win.ApplyFontScale(vm.CurrentFontSize);
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
        // Reflect the focused pane's rail (with splits the rail-reader may be a secondary viewport),
        // falling back to the active document's primary rail when nothing is focused.
        bool shouldShow = (Vm?.Controller.FocusedViewport?.Rail.Active ?? Vm?.ActiveTab?.Rail.Active) == true;
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
        if (Vm is { } vm && TryHandleKey(vm, e)) return;
        base.OnKeyDown(e);
    }

    /// <summary>Run the window's key-handling tiers — Scan-All guard, Ctrl shortcuts, Alt history,
    /// rail/navigation keys, global keys — returning true when the key was handled (or swallowed).
    /// Shared by <see cref="OnKeyDown"/> and the tear-off document windows, which forward their keys
    /// here so a focused detached pane gets the same shortcuts (input routes through FocusedViewport).</summary>
    internal bool TryHandleKey(MainWindowViewModel vm, KeyEventArgs e)
    {
        // During Scan All, only Escape is allowed (to cancel the scan); all other keys are swallowed.
        if (vm.IsScanAllActive)
        {
            if (e.Key == Key.Escape)
            {
                vm.CancelScanAll();
                e.Handled = true;
            }
            return true;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && HandleCtrlShortcut(vm, e))
            { RailToolBar.SyncState(); return true; }

        // Alt+Arrow: navigation history (back/forward)
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            if (e.Key == Key.Left) { vm.NavigateBack(); e.Handled = true; return true; }
            if (e.Key == Key.Right) { vm.NavigateForward(); e.Handled = true; return true; }
        }

        // When the search TextBox has focus, let text input keys through. Also require the
        // Search section to actually be the open pane, so a stale focus flag (Search collapsed
        // while the box held focus) can't keep swallowing nav keys.
        bool textInputFocused = (vm.ShowOutline && vm.IsSearchInputFocused && vm.ActivePane == SidePane.Search)
            || StatusBar.IsEditing;

        if (!textInputFocused && HandleNavigationKey(vm, e))
            { RailToolBar.SyncState(); return true; }

        if (HandleGlobalKey(vm, e))
            { RailToolBar.SyncState(); return true; }

        return false;
    }

    /// <summary>Ctrl+ keyboard shortcuts. Returns true if the key was handled.</summary>
    private bool HandleCtrlShortcut(MainWindowViewModel vm, KeyEventArgs e)
    {
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        // Viewport splitting (VS Code: Ctrl+\ split, Ctrl+Shift+\ close the focused pane/window).
        // The backslash key's Key enum value is keyboard-layout dependent (OemBackslash vs OemPipe vs
        // others across X11/Windows/layouts), so match the layout-independent physical key position.
        if (e.PhysicalKey == PhysicalKey.Backslash)
        {
            if (shift) vm.RequestCloseSurface(); else vm.RequestSplitRight();
            e.Handled = true; return true;
        }

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
        // Semi-auto scroll: while parked on a stop unit, the forward/advance keys resume flow
        // rather than boosting / navigating manually. Shift+D stays the debug-overlay toggle.
        if (vm.AutoScrollParked
            && e.Key is Key.Down or Key.S or Key.Right or Key.D or Key.Space
            && !(e.Key == Key.D && e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
        {
            vm.ResumeAutoScrollFromPark();
            e.Handled = true;
            return true;
        }

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
            case Key.R:
                // Click-free "start rail here" — force rail at the viewport centre (toggles off if forced).
                vm.StartRailHere(); e.Handled = true; return true;
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
            case Key.Z when vm.CanFreeze || vm.IsFrozen || vm.FreezeArmMode != FreezeMode.None:
                // Freeze panes: Unfreeze if frozen, else arm a "both" placement — the pointer becomes a
                // crossing guide; click to drop the page-wide split (rows above + columns left). The
                // Table-Reading panel offers rows-only / columns-only. Z again cancels the arm.
                if (vm.IsFrozen) vm.Unfreeze(); else vm.ArmFreeze(FreezeMode.Both);
                e.Handled = true; return true;
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
            // Disarm a pending "start rail here" click first — it's a one-shot action mode, so a single
            // Escape should always cancel it rather than being shadowed by the other Escape handlers.
            case Key.Escape when vm.FreezeArmMode != FreezeMode.None:
                vm.FreezeArmMode = FreezeMode.None; e.Handled = true; return true;
            case Key.Escape when vm.ArmActivateRailClick:
                vm.ArmActivateRailClick = false; e.Handled = true; return true;
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
            case Key.Escape when vm.ForcedRailActive:
                vm.ExitForcedRail(); e.Handled = true; return true;
            default: return false;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (Vm is { } vm) TryHandleKeyUp(vm, e);
        if (!e.Handled)
            base.OnKeyUp(e);
    }

    /// <summary>Shared key-release handling (rail scroll stop, edge-hold clear, rail resume). Forwarded
    /// from the portal / tear-off windows too, so releasing an arrow while a floating window is focused
    /// stops its viewport's hold-to-scroll — otherwise the scroll free-runs after release.</summary>
    private bool TryHandleKeyUp(MainWindowViewModel vm, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.A or Key.D)
        {
            vm.HandleArrowRelease(true);
            e.Handled = true;
        }

        // Clear non-rail edge-hold on vertical key release
        if (e.Key is Key.Down or Key.Up or Key.S or Key.W or Key.Space)
        {
            vm.Controller.ClearPageEdgeHold();
            e.Handled = true;
        }

        // Resume rail mode when Ctrl is released after free pan
        if (vm is { RailPaused: true } && e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            vm.ResumeRailFromPause();
            e.Handled = true;
        }

        return e.Handled;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Vm is not { IsFullScreen: true } vm) return;

        // Suppress hover-reveal entirely while the user is actively rail-scrolling.
        // A chrome toggle reflows the layout and may force a GPU texture re-upload
        // on the page layer; that's exactly what we don't want during scroll. The
        // user isn't trying to surface the chrome mid-scroll anyway.
        var doc = vm.Controller.FocusedViewport?.Owner;
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
