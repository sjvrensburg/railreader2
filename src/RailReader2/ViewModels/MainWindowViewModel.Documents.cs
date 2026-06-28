using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using RailReader.Core;
using RailReader.Core.Services;
using RailReader2.Views;

namespace RailReader2.ViewModels;

// Document management: open, close, tabs, sidebar state
public sealed partial class MainWindowViewModel
{
    private int? _pendingDuplicatePage;

    /// <summary>Save sidebar visibility and width to the given tab.</summary>
    private void SaveSidebarState(TabViewModel tab)
    {
        tab.ShowSidePanel = ShowOutline;
        if (ShowOutline && ReadSidePanelWidth is { } getWidth)
            tab.SidePanelWidth = getWidth();
    }

    /// <summary>Restore sidebar visibility and width from the given tab.</summary>
    private void RestoreSidebarState(TabViewModel tab)
    {
        ShowOutline = tab.ShowSidePanel;
        // Width is applied by UpdateSidebarColumnWidth via the ShowOutline PropertyChanged handler
    }

    public async Task OpenDocument(string path)
    {
        // Consume the duplicate-source page up front so it can never leak into a later, unrelated
        // open (e.g. if this call early-returns below). Only the duplicate-tab dedup path uses it.
        int? duplicatePage = _pendingDuplicatePage;
        _pendingDuplicatePage = null;

        if (IsScanAllActive) return;

        // Decision #1: if this file is already open in another tab, don't open a second copy —
        // add a viewport to the SHARED DocumentModel (one PDF handle + analysis/text caches +
        // annotations across all its tabs; no duplicate ONNX work). The new tab is kept as a
        // separate tab with its own camera / page / rail.
        var full = Path.GetFullPath(path);
        foreach (var t in Tabs)
        {
            if (string.Equals(Path.GetFullPath(t.FilePath), full, StringComparison.Ordinal))
            {
                OpenSharedViewportTab(t, duplicatePage);
                return;
            }
        }

        try
        {
            _logger.Debug($"[OpenDocument] Opening: {path}");

            // Encrypted PDFs throw PdfPasswordRequiredException from CreateDocument (on the
            // background thread). Dialogs must be shown on the UI thread, so the open attempt
            // lives in Task.Run while the password *resolution* loops out here. The resolved
            // password is held only inside the opened IPdfService — never persisted, and never
            // passed again after CreateDocument (LoadAnnotations etc. read IPdfService.Password).
            TabViewModel? tab = null;
            string? password = null;
            while (tab is null)
            {
                var attemptPassword = password;
                DocumentModel? state = null;
                try
                {
                    await Task.Run(() =>
                    {
                        state = _controller.CreateDocument(path, attemptPassword);
                        if (!state.LoadPageBitmap())
                            throw new InvalidOperationException($"Failed to render first page of {Path.GetFileName(path)}");
                    });
                }
                catch (PdfPasswordRequiredException ex)
                {
                    if (_window is null) return;
                    var entered = await new PasswordDialog(Path.GetFileName(path), ex.WrongPassword)
                        .ShowDialog<string?>(_window);
                    // Cancel = "changed my mind", not a failure — abort quietly (no toast).
                    if (entered is null) return;
                    password = entered;
                    continue;
                }

                tab = new TabViewModel(state!, state!.Primary);
            }

            _logger.Debug($"[OpenDocument] Loaded: {tab.PageCount} pages, {tab.PageWidth}x{tab.PageHeight}");
            tab.LoadAnnotations(_controller.AnnotationManager);
            // Linked-context portals (shell sidecar, keyed by PDF SHA-256). One reference-counted set is
            // shared across all tabs/panes of the same PDF, so saves from duplicate tabs don't clobber
            // each other (released in CloseTab).
            tab.Portals = Services.PortalSetManager.Default.Checkout(tab.FilePath);

            // Save sidebar state from outgoing tab before switching
            if (ActiveTab is { } oldTab)
                SaveSidebarState(oldTab);

            _controller.AddDocument(tab.State);
            Tabs.Add(tab);

            // New tab inherits the current sidebar state
            tab.ShowSidePanel = ShowOutline;
            if (ReadSidePanelWidth is { } getWidth)
                tab.SidePanelWidth = getWidth();

            ActiveTabIndex = Tabs.Count - 1;
            OnPropertyChanged(nameof(ActiveTab));
            // Route focus to this tab's own view (wires its reading-context signals + focus visuals).
            FocusViewport(tab.Viewport);

            InvalidateAll();

            Dispatcher.UIThread.Post(() => InvalidatePage(), DispatcherPriority.Background);
            RequestAnimationFrame();
            StartBackgroundAnalysis();

            _logger.Debug("[OpenDocument] Tab added successfully");
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to open {path}", ex);
            ShowStatusToast($"Failed to open: {Path.GetFileName(path)}");
        }
    }

    /// <summary>Open a new tab that shares <paramref name="existing"/>'s <see cref="DocumentModel"/>
    /// via a fresh <see cref="Viewport"/> (railreader2#180 decision #1). The new view starts on the
    /// duplicate-source page (or the existing tab's current page), seeded + sized like a split pane so
    /// its rail seats and it centres correctly when shown. Caches + annotations are shared with the
    /// existing tab; the new tab navigates independently.</summary>
    private void OpenSharedViewportTab(TabViewModel existing, int? duplicatePage)
    {
        var model = existing.State;
        var vp = model.AddViewport();
        int page = duplicatePage ?? existing.CurrentPage;
        vp.CurrentPage = Math.Clamp(page, 0, model.PageCount - 1);
        vp.IsLive = true;

        // Size from the surface the user currently sees so the initial centre/zoom is right; the
        // DocumentView re-sizes it on its next layout pass once this tab is shown.
        var (w, h) = FocusedViewportSize();
        if (w > 0 && h > 0) vp.SetSize(w, h);
        vp.LoadPageBitmap();
        vp.CenterPage(vp.Width, vp.Height);
        vp.UpdateRailZoom(vp.Width, vp.Height);
        // Seat this view's rail: a cache hit (the page the existing view already analysed) seats
        // synchronously; otherwise analysis is scheduled and the fan-out seats it on arrival.
        model.SubmitAnalysis(vp, _controller.Worker, _controller.Config.NavigableRoles);
        model.QueueLookahead(vp, _controller.Config.AnalysisLookaheadPages);

        // Shares the model (PDF/caches/annotations already loaded). Portals are shared too via the
        // reference-counted manager, so this tab sees and saves the same set as its sibling.
        var tab = new TabViewModel(model, vp) { Portals = Services.PortalSetManager.Default.Checkout(model.FilePath) };

        if (ActiveTab is { } oldTab) SaveSidebarState(oldTab);
        tab.ShowSidePanel = ShowOutline;
        if (ReadSidePanelWidth is { } getWidth) tab.SidePanelWidth = getWidth();

        Tabs.Add(tab);
        ActiveTabIndex = Tabs.Count - 1;
        OnPropertyChanged(nameof(ActiveTab));
        FocusViewport(vp);
        InvalidateAll();
        RequestAnimationFrame();
        StartBackgroundAnalysis();
    }

    [RelayCommand]
    public async Task OpenFile()
    {
        if (_window is null) return;
        var files = await _window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open PDF",
            FileTypeFilter = [new FilePickerFileType("PDF Files") { Patterns = ["*.pdf"] }],
            AllowMultiple = false,
        });
        if (files is { Count: > 0 })
        {
            var path = files[0].TryGetLocalPath()
                       ?? files[0].Path.LocalPath;
            _logger.Debug($"[OpenFile] Selected: {path}");
            if (path is not null) await OpenDocument(path);
        }
    }

    [RelayCommand]
    public void CloseTab(int index)
    {
        if (IsScanAllActive) return;
        if (index < 0 || index >= Tabs.Count) return;
        var tab = Tabs[index];
        Tabs.RemoveAt(index);

        // Model lifecycle (decision #1): several tabs may share one DocumentModel. Dispose the model
        // only when its LAST tab closes; otherwise just free this tab's own viewport (a duplicate
        // tab's secondary view). RailReaderCore 0.44.0 (#77): RemoveViewport promotes a sibling when
        // the removed view is the model's Primary, so closing the Primary-view tab while siblings
        // remain no longer leaves an orphaned Primary pinning its page caches (was decision-#1 limit b).
        bool modelStillUsed = false;
        foreach (var t in Tabs)
            if (ReferenceEquals(t.State, tab.State)) { modelStillUsed = true; break; }

        // Re-point any secondary surface (split pane / tear-off) created from this tab to a surviving
        // sibling tab of the same model BEFORE disposing the tab — otherwise the orphaned surface would
        // keep reading per-tab prefs from the now-frozen disposed tab. Its own viewport is untouched.
        if (modelStillUsed)
        {
            var sibling = Tabs.First(t => ReferenceEquals(t.State, tab.State));
            foreach (var s in _surfaces)
                if (ReferenceEquals(s.BoundTab, tab))
                    s.RebindTab(sibling);
        }

        tab.Dispose(); // unsubscribe + free this tab's images (not the shared model / its viewport)
        Services.PortalSetManager.Default.Release(tab.FilePath); // drop our portal-set checkout
        if (!modelStillUsed)
        {
            // Tear the live portal view down FIRST if its viewport sits on this about-to-be-disposed
            // model: unregister its surface (stop ticking) + remove its viewport while the model is still
            // alive, so CloseDocument can't dispose the portal viewport out from under a still-registered
            // surface — and so the ActiveTab notification below can't re-sync against a disposed view.
            if (_portalViewport?.Owner is { } pOwner && ReferenceEquals(pOwner, tab.State))
                RequestPortalViewTeardown();
            int docIdx = _controller.Documents.IndexOf(tab.State);
            if (docIdx >= 0) _controller.CloseDocument(docIdx); // disposes model + all its viewports
        }
        else
        {
            // Free this tab's own viewport. If it happened to be the model's Primary, Core 0.44.0
            // promotes a surviving sibling to Primary instead of throwing (the sibling tabs each own a
            // live viewport, so at least one always remains).
            tab.State.RemoveViewport(tab.Viewport);
        }

        if (Tabs.Count == 0)
        {
            ActiveTabIndex = 0;
            ShowOutline = false;
        }
        else
        {
            if (ActiveTabIndex >= Tabs.Count) ActiveTabIndex = Tabs.Count - 1;
            RestoreSidebarState(Tabs[ActiveTabIndex]);
        }
        OnPropertyChanged(nameof(ActiveTab));
        if (Tabs.Count > 0) FocusViewport(Tabs[ActiveTabIndex].Viewport);
        else UnwireFocusedSignals(); // no viewport to re-point to — release the disposed one

        // Free the closed view's frozen-pane crops (#180) only AFTER the Document pane has rebound to the
        // now-active tab above — by then its FreezePaneLayer no longer references these crops, so this
        // UI-thread dispose can't free a bitmap the compositor is still drawing for the closed viewport.
        DisposeFreezeFor(tab.Viewport);
        // Re-evaluate portals for the now-active tab (or clear them when the last tab closed); also
        // close any pop-out window once no document remains so it can't linger over an empty app.
        EvaluatePortals();
        if (Tabs.Count == 0) DismissPortalWindow();
        InvalidateAll();
    }

    public void SaveAllReadingPositions() => _controller.SaveAllReadingPositions();

    [RelayCommand]
    public void SelectTab(int index)
    {
        if (IsScanAllActive) return;
        if (index >= 0 && index < Tabs.Count)
        {
            if (ActiveTab is { } oldTab)
                SaveSidebarState(oldTab);

            // Drop out of any annotation mode when switching tabs
            if (IsAnnotating)
                CancelAnnotationTool();

            // Quiesce the tab we're leaving (its viewport is still the focused one at this point) —
            // matches the old SelectDocument behaviour now that tab focus is viewport-based (#1):
            // end any in-progress free-pan (else its RailPause would stay set on the hidden view and
            // leave it stuck in the free-pan camera on return) and stop its auto-scroll.
            if (RailPaused) ResumeRailFromPause();
            _controller.StopAutoScroll();

            ActiveTabIndex = index;
            OnPropertyChanged(nameof(ActiveTab)); // → Document.SetTab rebinds to this tab's own viewport
            // Route focus to the selected tab's viewport (replaces the old document-index SelectDocument).
            FocusViewport(Tabs[index].Viewport);
            RestoreSidebarState(Tabs[index]);
            ResetArmStateForTabSwitch();

            // Focusing fires neither PageChanged nor ReadingPositionChanged, so evaluate portals here —
            // otherwise the previous tab's target crop lingers on a quiescent switch.
            EvaluatePortals();
            InvalidateAll();
        }
    }

    public void MoveTab(int fromIndex, int toIndex)
    {
        if (IsScanAllActive) return;
        if (fromIndex == toIndex) return;
        if (fromIndex < 0 || fromIndex >= Tabs.Count) return;
        if (toIndex < 0 || toIndex >= Tabs.Count) return;

        var selectedTab = ActiveTab;
        Tabs.Move(fromIndex, toIndex);
        // Tab order is independent of Core's Documents order now (#1): focus is viewport-based, not
        // document-index-based, so there's no MoveDocument to keep in lock-step.

        if (selectedTab is not null)
            ActiveTabIndex = Tabs.IndexOf(selectedTab);

        OnPropertyChanged(nameof(ActiveTab));
        InvalidateAll();
    }

    [RelayCommand]
    public async Task DuplicateTab()
    {
        if (ActiveTab is { } tab)
        {
            _pendingDuplicatePage = tab.CurrentPage;
            await OpenDocument(tab.FilePath);
        }
    }
}
