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
        if (IsScanAllActive) return;
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
                DocumentState? state = null;
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

                tab = new TabViewModel(state!);
            }

            _logger.Debug($"[OpenDocument] Loaded: {tab.PageCount} pages, {tab.PageWidth}x{tab.PageHeight}");
            tab.LoadAnnotations(_controller.AnnotationManager);
            // Linked-context portals (shell sidecar, keyed by PDF SHA-256). Each tab holds its own
            // set; duplicate tabs of the same PDF are last-writer-wins (documented, not solved in v1).
            tab.Portals = Services.PortalSet.Load(tab.FilePath);

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

            // Navigate duplicate tab to the source tab's page
            if (_pendingDuplicatePage is { } dupPage)
            {
                _pendingDuplicatePage = null;
                _controller.GoToPage(dupPage);
            }

            InvalidateAll();

            Dispatcher.UIThread.Post(() => InvalidatePage(), DispatcherPriority.Background);
            RequestAnimationFrame();
            StartBackgroundAnalysis();

            _logger.Debug("[OpenDocument] Tab added successfully");
        }
        catch (Exception ex)
        {
            _pendingDuplicatePage = null;
            _logger.Error($"Failed to open {path}", ex);
            ShowStatusToast($"Failed to open: {Path.GetFileName(path)}");
        }
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
        _controller.CloseDocument(_controller.Documents.IndexOf(tab.State));
        Tabs.RemoveAt(index);
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

            ActiveTabIndex = index;
            _controller.SelectDocument(index);
            RestoreSidebarState(Tabs[index]);
            ResetTableStateForTabSwitch();

            OnPropertyChanged(nameof(ActiveTab));
            // Core's SelectDocument fires neither PageChanged nor ReadingPositionChanged, so evaluate
            // portals here — otherwise the previous tab's target crop lingers on a quiescent switch.
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
        _controller.MoveDocument(fromIndex, toIndex);

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
