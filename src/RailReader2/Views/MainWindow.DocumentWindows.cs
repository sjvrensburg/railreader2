using System;
using System.Collections.Generic;
using System.Linq;
using RailReader2.ViewModels;

namespace RailReader2.Views;

/// <summary>
/// Tear-off document windows: floating <see cref="DocumentWindow"/>s each hosting a detached
/// viewport of the active document, coexisting with the docked split panes. Follows the
/// PortalWindow lifecycle precedent (non-modal, no owner, cleaned up on close / window unload).
/// </summary>
public partial class MainWindow
{
    private readonly List<DocumentWindow> _documentWindows = new();

    /// <summary>Move the focused docked split pane into its own floating window (keeping its viewport,
    /// page, and zoom), or — when the focus is the primary pane / a window — open a fresh viewport window.</summary>
    private void OnMoveSurfaceToWindow()
    {
        if (Vm is not { } vm || vm.ActiveTab is null) return;

        if (vm.FocusedSurface is DocumentView pane && !ReferenceEquals(pane, Document) && _panes.Contains(pane))
        {
            // Relocate the existing pane: pull it out of the grid (reparented into the window).
            _panes.Remove(pane);
            RebuildPaneGrid();
            HostInDocumentWindow(pane);
        }
        else if (CreateSecondaryView() is { } view) // rail seated in CreateSecondaryView
        {
            HostInDocumentWindow(view);
        }
    }

    private void HostInDocumentWindow(DocumentView view)
    {
        if (Vm is not { } vm) return;

        var win = new DocumentWindow
        {
            DataContext = vm,
            FontSize = vm.CurrentFontSize,
            Title = vm.ActiveTab is { } t ? $"railreader2 — {t.Title}" : "railreader2",
            KeyHandler = e => TryHandleKey(vm, e),
            KeyUpHandler = e => TryHandleKeyUp(vm, e),
        };
        win.Host(view);
        win.Closed += OnDocumentWindowClosed;
        // Activating the window (click / alt-tab) focuses its pane so input routes there. A named
        // handler (not a lambda) so it can be detached symmetrically in OnDocumentWindowClosed.
        win.Activated += OnDocumentWindowActivated;

        _documentWindows.Add(win);
        // Non-modal, no owner — independent on any monitor (matches the portal tear-off).
        win.Show();

        if (view.SurfaceViewport is { } focusVp)
        {
            vm.FocusSurface(view, focusVp);
            vm.RequestAnimationFrame();
        }
    }

    private void OnDocumentWindowActivated(object? sender, EventArgs e)
    {
        if (sender is DocumentWindow win && win.HostedView is { } view
            && view.SurfaceViewport is { } vp && Vm is { } vm)
            vm.FocusSurface(view, vp);
    }

    private void OnDocumentWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not DocumentWindow win) return;
        win.Closed -= OnDocumentWindowClosed;
        win.Activated -= OnDocumentWindowActivated;
        _documentWindows.Remove(win);
        if (win.HostedView is not { } view) return;

        // Was this window's viewport the focused one? Capture before disposal re-points focus.
        bool wasFocused = Vm is { } vm0
            && ReferenceEquals(vm0.Controller.FocusedViewport, view.SurfaceViewport);
        DisposeSecondarySurface(view);

        // Closing the FOCUSED window: Core re-points FocusedViewport to the primary, but the desktop
        // focus projections (ambient size, status-bar zoom, rail toolbar, menu gating) are only
        // re-synced by FocusSurface — mirror RemovePane / OnCloseExtraSurfaces and re-focus the
        // primary pane. (When a non-focused window is closed, focus is already correct.)
        if (wasFocused && Vm is { } vm && Document.SurfaceViewport is { } primaryVp)
            vm.FocusSurface(Document, primaryVp);
    }

    /// <summary>If a tear-off window hosts the focused viewport, close it. Returns true if one was closed.</summary>
    private bool CloseFocusedDocumentWindow()
    {
        if (Vm is not { } vm) return false;
        var focused = vm.Controller.FocusedViewport;
        foreach (var win in _documentWindows)
            if (win.HostedView is { } v && ReferenceEquals(v.SurfaceViewport, focused))
            {
                win.Close(); // → OnDocumentWindowClosed → DisposeSecondarySurface
                return true;
            }
        return false;
    }

    private void CloseAllDocumentWindows()
    {
        // Snapshot: Close() mutates _documentWindows via its Closed handler.
        foreach (var win in _documentWindows.ToArray())
            win.Close();
        _documentWindows.Clear();
    }
}
