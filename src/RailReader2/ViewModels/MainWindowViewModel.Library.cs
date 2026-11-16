using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader2.Library;
using RailReader2.Views;

namespace RailReader2.ViewModels;

// Library search index (docs/library-search-design.md, M7 Phase 1): keeps ConfigDir/library/library.db in
// step with the documents the reader opens. All writes go through one LibraryWriter thread; this file only
// takes UI-thread snapshots and hands them over. The Library window (Phase 2, Views/LibraryWindow) and the
// CLI's `library search` read the same database through their own read-only connections.
//
// Hooks: OpenDocument (after the content identity and annotations are loaded) → DocumentOpened;
// CloseTab of a model's last tab and Dispose → DocumentClosed (notes edited this session, OCR-only text);
// SetColourName → CategoryRenamed; RunAnimationFrame → IsBusy while rail reading (holds back backfill);
// window Opened → StartLibraryBackfill. Core has no "annotations saved" event, so notes are re-indexed on
// close rather than on each auto-save.
public partial class MainWindowViewModel
{
    private LibraryWriter? _library;

    private void InitLibrary()
    {
        if (Environment.GetEnvironmentVariable("RR_DISABLE_LIBRARY_INDEX") is { Length: > 0 }) return;
        try
        {
            _library = new LibraryWriter(LibraryIndex.DefaultPath, new RailReader.Renderer.Skia.SkiaPdfServiceFactory())
            {
                OnError = (msg, ex) => _logger.Error($"[Library] {msg}", ex),
                OnRecovered = () => _logger.Info("[Library] index was unreadable; moved aside to library.db.unreadable and rebuilt"),
            };
            _library.Changed += OnLibraryWriterChanged;
            PublishLibraryCategories();
        }
        catch (Exception ex)
        {
            _logger.Error("[Library] could not start the index writer", ex);
            _library = null;
        }
    }

    private void PublishLibraryCategories()
    {
        if (_library is null) return;
        _library.Categories = new Dictionary<string, string>(_annotationPrefs.ColourNames.Select(kv =>
            new KeyValuePair<string, string>(kv.Key.Trim().ToUpperInvariant(), kv.Value)));
    }

    private void LibraryCategoryRenamed(string hexColour)
    {
        if (_library is null) return;
        PublishLibraryCategories();
        _library.CategoryRenamed(hexColour, ColourName(hexColour));
    }

    /// <summary>UI thread, after the tab's identity and annotations are loaded.</summary>
    private void LibraryDocumentOpened(TabViewModel tab)
    {
        if (_library is null) return;
        if (DocumentIdentity.Peek(tab.FilePath) is not { } id) return;
        var model = tab.State;
        bool encrypted = model.Pdf.Password is not null;
        _library.DocumentOpened(tab.FilePath, id, encrypted,
            encrypted || model.Annotations is null ? null : LibraryIndexer.Snapshot(model.Annotations), tab.CurrentPage);
    }

    /// <summary>UI thread, while <paramref name="model"/> is still alive (before CloseDocument/Dispose).</summary>
    private void LibraryDocumentClosed(DocumentModel model, int? page)
    {
        if (_library is null) return;
        if (DocumentIdentity.Peek(model.FilePath) is not { } id) return;
        bool encrypted = model.Pdf.Password is not null;
        var ocr = new List<PageEntry>();
        if (!encrypted)
            foreach (var (p, text) in model.TextCache)
                if (text.FromOcr && !string.IsNullOrWhiteSpace(text.Text))
                    ocr.Add(new PageEntry(p, TextNormalizer.Normalize(text.Text), true));
        _library.DocumentClosed(model.FilePath, id, encrypted,
            encrypted || model.Annotations is null ? null : LibraryIndexer.Snapshot(model.Annotations), ocr, page);
    }

    /// <summary>Queues documents opened before the index existed (§5.3): the identity map's paths plus
    /// recent files. Called once after the main window opens; the writer runs them at low priority.</summary>
    public void StartLibraryBackfill()
    {
        if (_library is null) return;
        var recents = _appConfig.RecentFiles.Select(r => r.FilePath).Where(p => !string.IsNullOrEmpty(p)).ToList();
        var library = _library;
        _ = Task.Run(() =>
        {
            try { library.Backfill(recents.Concat(KnownDocuments.FromIdentityMap())); }
            catch (Exception ex) { _logger.Error("[Library] backfill could not start", ex); }
        });
    }

    // ------------------------------------------------------------ Library window (Phase 2)

    private LibraryWindow? _libraryWindow;
    private int _libraryChangePosted;

    /// <summary>Raised on the UI thread when the index may have changed (a document indexed, removed,
    /// backfill progress), coalesced so a burst of writer jobs posts once.</summary>
    public event Action? LibraryChanged;

    /// <summary>False when the index writer isn't running (turned off with RR_DISABLE_LIBRARY_INDEX, or the
    /// database couldn't be opened): the window can still search, but nothing new is indexed.</summary>
    public bool IsLibraryIndexing => _library is not null;

    /// <summary>Backfill progress for the Library window ("Indexing 3 of 12"); (0, 0) when idle.</summary>
    public (int Done, int Total) LibraryBackfillProgress => _library?.BackfillProgress ?? (0, 0);

    private void OnLibraryWriterChanged()
    {
        if (Interlocked.Exchange(ref _libraryChangePosted, 1) == 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref _libraryChangePosted, 0);
            LibraryChanged?.Invoke();
        }, DispatcherPriority.Background);
    }

    /// <summary>Ctrl+Shift+F / File ▸ Search Library…: shows the (single) Library window, optionally
    /// carrying a query over from the in-document Search pane.</summary>
    public void ShowLibrary(string? query = null)
    {
        if (_window is null) return;
        if (_libraryWindow is { } open)
        {
            if (open.WindowState == Avalonia.Controls.WindowState.Minimized)
                open.WindowState = Avalonia.Controls.WindowState.Normal;
            open.Activate();
            open.SetQuery(query);
            return;
        }
        var win = new LibraryWindow(this) { FontSize = CurrentFontSize };
        _libraryWindow = win;
        win.Closed += (_, _) => _libraryWindow = null;
        // Not owned by the main window: an owned window always stays above its owner, so the document a
        // result opens would be hidden behind it. Closed with the app in ShutdownLibrary.
        win.Show();
        win.SetQuery(query);
    }

    /// <summary>The reader's own name, for telling their notes from reviewers' (design Q2).</summary>
    public string? LibraryReaderName => AnnotationAuthorName;

    /// <summary>Opens a library result: the document (an open tab of it if there is one), then the hit's
    /// page. A note hit selects the annotation, as the Comments pane does; other hits run the in-document
    /// search for the query's words so the matches on the page are highlighted (design §7.1).</summary>
    public async Task OpenLibraryHit(LibraryHit hit, LibraryQuery query)
    {
        if (await EnsureLibraryDocumentOpen(hit.ContentId, hit.LastPath) is not { } tab) return;
        _window?.Activate();
        if (hit.Page is not int page) return; // a file-name hit opens at the saved position
        page = Math.Clamp(page, 0, Math.Max(0, tab.PageCount - 1));

        if (hit.Source == HitSource.Note && hit.NoteKind != "bookmark"
            && FindAnnotation(tab, page, hit.NativeId) is { } annotation)
        {
            NavigateToAnnotation(page, annotation);
            return;
        }
        GoToPage(page);
        if (hit.Source != HitSource.Note && query.HighlightPattern is { } pattern)
            RequestSearch(pattern, focus: false, useRegex: true, startPage: page);
    }

    /// <summary>Opens a library document at its saved reading position.</summary>
    public async Task OpenLibraryDocument(string contentId, string? path)
    {
        if (await EnsureLibraryDocumentOpen(contentId, path) is not null) _window?.Activate();
    }

    private async Task<TabViewModel?> EnsureLibraryDocumentOpen(string contentId, string? path)
    {
        for (int i = 0; i < Tabs.Count; i++)
        {
            if (DocumentIdentity.Peek(Tabs[i].FilePath) != contentId) continue;
            if (i != ActiveTabIndex) SelectTab(i);
            return Tabs[i];
        }
        if (path is null || !File.Exists(path))
        {
            ShowStatusToast("That file is no longer where it was. Use Locate… in the Library window to find it.");
            return null;
        }
        int before = Tabs.Count;
        await OpenDocument(path);
        return Tabs.Count > before ? ActiveTab : null;
    }

    private static Annotation? FindAnnotation(TabViewModel tab, int page, string? nativeId)
    {
        if (nativeId is not { Length: > 0 } || tab.State.Annotations is not { } file) return null;
        return file.Pages.TryGetValue(page, out var list)
            ? list.FirstOrDefault(a => string.Equals(a.NativeId, nativeId, StringComparison.Ordinal))
            : null;
    }

    /// <summary>"Locate…" on a missing document (design Q3): the picked file is accepted only when it is
    /// the same document (its bytes match the content id, or a version of it the identity map knows).
    /// Returns null when the reader cancelled, else whether it was accepted.</summary>
    public async Task<bool?> LocateLibraryDocument(string contentId, string fileName, Avalonia.Controls.Window owner)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Locate {fileName}",
            FileTypeFilter = [new FilePickerFileType("PDF Files") { Patterns = ["*.pdf"] }],
            AllowMultiple = false,
        });
        if (files is not { Count: > 0 } || (files[0].TryGetLocalPath() ?? files[0].Path.LocalPath) is not { } picked)
            return null;
        // Lookup hashes the file without touching the identity map; only a match is resolved (recorded).
        string? id = await Task.Run(() =>
        {
            try { return DocumentIdentity.Lookup(picked); }
            catch (Exception ex) { _logger.Error("[Library] locate: could not read the picked file", ex); return null; }
        });
        if (id != contentId) return false;
        await Task.Run(() => DocumentIdentity.Resolve(picked));
        _library?.Relocate(contentId, picked);
        return true;
    }

    /// <summary>"Remove from library": drops the document's index rows only (design §3).</summary>
    public void RemoveFromLibrary(string contentId) => _library?.Remove(contentId);

    /// <summary>Settings ▸ Library ▸ Rebuild: empties the index and indexes every known document again.</summary>
    public void RebuildLibrary()
    {
        if (_library is null) return;
        var recents = _appConfig.RecentFiles.Select(r => r.FilePath).Where(p => !string.IsNullOrEmpty(p)).ToList();
        var library = _library;
        _ = Task.Run(() =>
        {
            try { library.Rebuild(recents.Concat(KnownDocuments.FromIdentityMap())); }
            catch (Exception ex) { _logger.Error("[Library] rebuild could not start", ex); }
        });
    }

    /// <summary>Settings ▸ Library ▸ Clear: empties the index; documents come back as they are opened.</summary>
    public void ClearLibrary() => _library?.Clear();

    /// <summary>Indexes notes/OCR text of every open document, then stops the writer (bounded wait).</summary>
    private void ShutdownLibrary()
    {
        _libraryWindow?.Close();
        if (_library is null) return;
        try
        {
            var seen = new HashSet<DocumentModel>();
            foreach (var t in Tabs)
                if (seen.Add(t.State)) LibraryDocumentClosed(t.State, t.CurrentPage);
        }
        catch (Exception ex) { _logger.Error("[Library] shutdown snapshot failed", ex); }
        _library.Dispose(TimeSpan.FromSeconds(3));
        _library = null;
    }
}
