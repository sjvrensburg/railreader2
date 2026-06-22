using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Media;
using RailReader.Core;
using RailReader2.ViewModels;

namespace RailReader2.Views;

/// <summary>
/// Multi-viewport pane management: VS-Code-style N side-by-side split panes (in <c>PaneGrid</c>)
/// plus tear-off floating windows, all over the one active document. Each surface renders its own
/// Core <see cref="Viewport"/>; the VM frame loop ticks them independently and routes host input
/// through the focused one (<c>controller.FocusedViewport</c>, set on pane click). Tear-off windows
/// live in <see cref="MainWindow.DocumentWindow"/>; this partial owns the shared surface lifecycle.
/// </summary>
public partial class MainWindow
{
    // Docked panes, left→right. _panes[0] is always the primary Document (declared in AXAML, never
    // removed). Extra panes are created/destroyed at runtime and laid out via RebuildPaneGrid.
    private readonly List<DocumentView> _panes = new();

    // The pane-splitter handle brush: a faint visible divider that's still easy to grab.
    private static readonly IBrush PaneSplitterBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x88, 0x88, 0x88));

    /// <summary>Wire pane management once the window is loaded (after the primary Document is
    /// initialised + registered). Subscribes the VM's pane commands to the view-layer handlers.</summary>
    private void InitPanes(MainWindowViewModel vm)
    {
        _panes.Clear();
        _panes.Add(Document); // the primary pane, already in PaneGrid at column 0

        vm.SplitRightRequested += OnSplitRight;
        vm.CloseSurfaceRequested += OnCloseFocusedSurface;
        vm.MoveSurfaceToWindowRequested += OnMoveSurfaceToWindow;
        vm.CloseExtraSurfacesRequested += OnCloseExtraSurfaces;
    }

    // The document the extra surfaces currently belong to. When the active document changes, the
    // extras (which render the previous document's viewports) collapse back to the single primary pane.
    private TabViewModel? _surfaceTab;

    /// <summary>Collapse split panes + tear-off windows when the active document changes — they render
    /// the previous document's viewports. The ActiveTab notification also fires on plain overlay frames,
    /// so this no-ops unless the document reference actually changed. Call after <c>Document.SetTab</c>,
    /// so the primary pane is already rebound to the new document's primary view.</summary>
    private void CollapseExtrasIfDocumentChanged(MainWindowViewModel vm)
    {
        if (ReferenceEquals(vm.ActiveTab, _surfaceTab)) return;
        _surfaceTab = vm.ActiveTab;
        if (_panes.Count > 1 || _documentWindows.Count > 0)
            OnCloseExtraSurfaces();
    }

    private void TeardownPanes(MainWindowViewModel vm)
    {
        vm.SplitRightRequested -= OnSplitRight;
        vm.CloseSurfaceRequested -= OnCloseFocusedSurface;
        vm.MoveSurfaceToWindowRequested -= OnMoveSurfaceToWindow;
        vm.CloseExtraSurfacesRequested -= OnCloseExtraSurfaces;
        CloseAllDocumentWindows();
    }

    // ── Shared surface lifecycle (reused by split panes and tear-off windows) ──────────────

    /// <summary>Create a fresh viewport on the active document and a DocumentView bound to it,
    /// opening on the focused view's current page. Registers it as a surface. Null if no document.</summary>
    private DocumentView? CreateSecondaryView()
    {
        if (Vm is not { } vm || vm.ActiveTab is not { } tab) return null;
        var doc = tab.State;

        var vp = doc.AddViewport();
        var focused = vm.Controller.FocusedViewport ?? doc.Primary;
        vp.CurrentPage = focused.CurrentPage; // new surface mirrors the current page, then navigates freely
        vp.IsLive = true;
        vp.LoadPageBitmap();
        // Seat this view's rail with the page's layout. GoToPage(samePage) early-returns before
        // analysing, so submit directly: a cache hit (the page the primary already analysed) seats the
        // rail synchronously, otherwise it schedules analysis and the fan-out seats it when it arrives.
        doc.SubmitAnalysis(vp, vm.Controller.Worker, vm.Controller.Config.NavigableRoles);

        var view = new DocumentView();
        view.Initialize(vm, tab);                                   // model wiring (annotations / prefs)
        view.BindViewport(vp, new ViewportImages(vp), ownsImages: true); // its own page/minimap images
        vm.RegisterSurface(view);
        return view;
    }

    /// <summary>Unregister a secondary surface, dispose its images, and remove + dispose its viewport
    /// from its owning document. Core re-points focus off a removed focused view to that doc's primary.</summary>
    private void DisposeSecondaryView(DocumentView view)
    {
        if (Vm is not { } vm) return;
        var vp = view.SurfaceViewport;
        vm.UnregisterSurface(view);
        view.Teardown(); // disposes its owned ViewportImages + removes handlers
        if (vp is { } v) SafeRemoveViewport(vm, v);
    }

    /// <summary>Remove a viewport from whichever open document owns it. Iterating the open tabs (all
    /// public API) keeps this safe whether the document was merely switched away from (still alive →
    /// frees the bitmap now) or was closed (its DocumentModel.Dispose already freed the viewport, so
    /// it isn't found here — no double-dispose). Avoids the internal Viewport.Owner / IsDisposed.</summary>
    private static void SafeRemoveViewport(MainWindowViewModel vm, Viewport vp)
    {
        foreach (var tab in vm.Tabs)
            if (tab.State.Viewports.Contains(vp))
            {
                tab.State.RemoveViewport(vp);
                return;
            }
    }

    // ── Split panes ────────────────────────────────────────────────────────────────────────

    private void OnSplitRight()
    {
        if (CreateSecondaryView() is not { } view) return;
        _panes.Add(view);
        RebuildPaneGrid();
        // VS Code: the new split becomes the focused pane (rail already seated in CreateSecondaryView).
        if (view.SurfaceViewport is { } vp)
        {
            Vm?.FocusSurface(view, vp);
            Vm?.RequestAnimationFrame();
        }
    }

    private void OnCloseFocusedSurface()
    {
        if (Vm is not { } vm) return;
        // A focused tear-off window closes itself; otherwise close the focused docked pane.
        if (CloseFocusedDocumentWindow()) return;
        if (vm.FocusedSurface is DocumentView pane && !ReferenceEquals(pane, Document))
            RemovePane(pane);
    }

    private void RemovePane(DocumentView pane)
    {
        if (ReferenceEquals(pane, Document)) return; // the primary pane is permanent
        int idx = _panes.IndexOf(pane);
        if (idx < 0) return;

        _panes.RemoveAt(idx);
        DisposeSecondaryView(pane); // Core re-points focus to Primary if this pane was focused
        RebuildPaneGrid();

        // Move focus to a neighbouring remaining pane (Core already fell back to Primary; refine it).
        var target = _panes[System.Math.Min(idx, _panes.Count - 1)];
        if (target.SurfaceViewport is { } vp)
            Vm?.FocusSurface(target, vp);
    }

    private void OnCloseExtraSurfaces()
    {
        CloseAllDocumentWindows();
        // Remove every docked pane except the primary.
        for (int i = _panes.Count - 1; i >= 1; i--)
        {
            DisposeSecondaryView(_panes[i]);
            _panes.RemoveAt(i);
        }
        RebuildPaneGrid();
        if (Document.SurfaceViewport is { } vp)
            Vm?.FocusSurface(Document, vp);
    }

    /// <summary>Rebuild the PaneGrid's columns, splitters, and pane placement from <see cref="_panes"/>.
    /// Layout is [pane, splitter, pane, splitter, …]: even columns are equal-weight panes (MinWidth so
    /// a splitter can't collapse one), odd columns are fixed-width GridSplitter handles that
    /// redistribute the adjacent star widths when dragged.
    /// <para>Non-destructive to the panes: existing pane controls stay attached (re-parenting a
    /// DocumentView would reset its composition layers), only the splitters are recreated. Panes no
    /// longer in <see cref="_panes"/> (closed or moved to a window) are detached so they can be
    /// re-hosted elsewhere.</para></summary>
    private void RebuildPaneGrid()
    {
        // Drop the old splitters (stateless, recreated below).
        for (int i = PaneGrid.Children.Count - 1; i >= 0; i--)
            if (PaneGrid.Children[i] is GridSplitter)
                PaneGrid.Children.RemoveAt(i);

        // Detach panes that are no longer ours (closed / relocated to a tear-off window).
        for (int i = PaneGrid.Children.Count - 1; i >= 0; i--)
            if (PaneGrid.Children[i] is DocumentView dv && !_panes.Contains(dv))
                PaneGrid.Children.RemoveAt(i);

        // Attach any pane not yet in the grid (a freshly-created split pane).
        foreach (var pane in _panes)
            if (!PaneGrid.Children.Contains(pane))
                PaneGrid.Children.Add(pane);

        PaneGrid.ColumnDefinitions.Clear();
        for (int i = 0; i < _panes.Count; i++)
        {
            if (i > 0)
            {
                PaneGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(6)));
                var splitter = new GridSplitter
                {
                    Width = 6,
                    ResizeDirection = GridResizeDirection.Columns,
                    Background = PaneSplitterBrush,
                };
                Grid.SetColumn(splitter, PaneGrid.ColumnDefinitions.Count - 1);
                PaneGrid.Children.Add(splitter);
            }
            PaneGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)) { MinWidth = 120 });
            Grid.SetColumn(_panes[i], PaneGrid.ColumnDefinitions.Count - 1);
        }
    }
}
