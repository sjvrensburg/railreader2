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
// window Opened → StartLibraryBackfill; Core's AnnotationFileManager.Saved → NotesSaved (each auto-save,
// the last tab closing, and the flush at shutdown).
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
            _controller.AnnotationManager.Saved += OnAnnotationsSaved;
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

    /// <summary>Core's <c>AnnotationFileManager.Saved</c>: re-index the saved notes. Raised synchronously
    /// where the save ran, which in this app is always the UI thread (auto-save is posted through
    /// <see cref="AvaloniaThreadMarshaller"/>; Release/FlushAll are called from the UI thread), so the
    /// snapshot of the live model is taken where the UI edits it. Encrypted documents are skipped by the
    /// writer (indexed as locked).</summary>
    private void OnAnnotationsSaved(object? sender, AnnotationsSavedEventArgs e)
    {
        if (_library is null) return;
        if (DocumentIdentity.Peek(e.PdfPath) is not { } id) return;
        _library.NotesSaved(e.PdfPath, id, LibraryIndexer.Snapshot(e.Annotations));
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
        _library.DocumentClosed(model.FilePath, id, encrypted, ocr, page);
    }

    /// <summary>Queues documents opened before the index existed (§5.3): the identity map's paths plus
    /// recent files. Called once after the main window opens; the writer runs them at low priority.</summary>
    public void StartLibraryBackfill() => QueueKnownDocuments((library, paths) => library.Backfill(paths), "backfill");

    /// <summary>Hands every document the reader has opened (recent files, then the identity map's paths,
    /// read on the thread pool) to <paramref name="start"/>.</summary>
    private void QueueKnownDocuments(Action<LibraryWriter, IEnumerable<string>> start, string what)
    {
        if (_library is not { } library) return;
        var recents = _appConfig.RecentFiles.Select(r => r.FilePath).Where(p => !string.IsNullOrEmpty(p)).ToList();
        _ = Task.Run(() =>
        {
            // Core's snapshot of the identity map. No Refresh(): this process owns the map (it calls
            // Resolve), so its in-memory copy is already the newest.
            try { start(library, recents.Concat(DocumentIdentity.KnownDocuments().Select(d => d.Path))); }
            catch (Exception ex) { _logger.Error($"[Library] {what} could not start", ex); }
        });
    }

    /// <summary>Holds the backfill back while the focused view is rail reading. Called from the frame
    /// loop and on every focus change, since a tab switch or close may not run another frame.</summary>
    private void SyncLibraryBusy()
    {
        if (_library is { } library) library.IsBusy = IsRailReading || _searchWork > 0;
    }

    // ------------------------------------------------------------ Library window (Phase 2)

    private LibraryWindow? _libraryWindow;
    private int _libraryChangePosted;

    /// <summary>Raised on the UI thread when the index may have changed (a document indexed, removed,
    /// backfill progress), coalesced so a burst of writer jobs posts once.</summary>
    public event Action? LibraryChanged;

    /// <summary>False when the index writer isn't running (turned off with RR_DISABLE_LIBRARY_INDEX, or the
    /// database couldn't be opened): the window can still search, but nothing new is indexed.</summary>
    public bool IsLibraryIndexing => _library is { IsAvailable: true };

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
        if (hit.Source == HitSource.Note) return;
        // The words the index matched on that page (stemmed forms and siblings included), else the
        // query's own words with a crude suffix strip.
        string? pattern = hit.Source == HitSource.Page
            ? await Task.Run(() => MatchedFormsPattern(hit.ContentId, hit.Page.Value, query))
            : null;
        if ((pattern ?? query.HighlightPattern) is { } p)
            RequestSearch(p, focus: false, useRegex: true, startPage: page);
    }

    private static string? MatchedFormsPattern(string contentId, int page, LibraryQuery query)
    {
        try
        {
            using var index = LibraryIndex.OpenReadOnly(LibraryIndex.DefaultPath);
            return index is null ? null : LibraryQuery.HighlightPatternFor(index.MatchedForms(contentId, page, query));
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Warn($"[Library] Matched forms for highlighting failed: {ex.Message}");
            return null;
        }
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
            // Already the active tab, but input may be on another document's pane, tear-off or portal:
            // the page jump and search below act on the focused view, so focus this document's.
            else if (!ReferenceEquals(_controller.FocusedViewport?.Owner, Tabs[i].State)) FocusViewport(Tabs[i].Viewport);
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
    public void RebuildLibrary() => QueueKnownDocuments((library, paths) => library.Rebuild(paths), "rebuild");

    /// <summary>Settings ▸ Library ▸ Clear: empties the index; documents come back as they are opened.</summary>
    public void ClearLibrary() => _library?.Clear();

    // ------------------------------------------------------------ recents dashboard (Phase 3)

    private readonly SemaphoreSlim _thumbnailGate = new(1, 1);

    /// <summary>The welcome screen's recent documents (design §7.2): the recent-files list de-duplicated
    /// by content id and joined to the index. Thread pool; never writes anything.</summary>
    public Task<IReadOnlyList<RecentDocument>> LoadRecentDocumentsAsync()
    {
        var recents = _appConfig.RecentFiles.Select(r => (r.FilePath, r.Page)).ToList();
        return Task.Run(() =>
        {
            try
            {
                using var index = LibraryIndex.OpenReadOnly(LibraryIndex.DefaultPath);
                return RecentDocuments.Build(recents, DocumentIdentity.Peek, index);
            }
            catch (Exception ex)
            {
                _logger.Error("[Library] recent documents failed", ex);
                return (IReadOnlyList<RecentDocument>)[];
            }
        });
    }

    /// <summary>Raised on the UI thread after the recent-files list was edited here (not on every open):
    /// the File ▸ Recent menu and the welcome screen's cards rebuild from it.</summary>
    public event Action? RecentFilesChanged;

    /// <summary>"Remove from recent documents" (#303): drops <paramref name="path"/> from
    /// <c>AppConfig.RecentFiles</c>, along with every other entry for the same document (the dashboard
    /// shows one card per content id, so leaving an older path behind would bring the card straight
    /// back). Touches only the list: the library index, notes, portals and thumbnail stay — that is
    /// Remove from Library's job.</summary>
    public void RemoveRecentFile(string path)
    {
        string? id = PeekId(path);
        int removed = _appConfig.RecentFiles.RemoveAll(r =>
            string.Equals(r.FilePath, path, StringComparison.Ordinal)
            || (id is not null && PeekId(r.FilePath) == id));
        if (removed == 0) return;
        _appConfig.Save();
        RecentFilesChanged?.Invoke();

        static string? PeekId(string? p)
        {
            if (string.IsNullOrEmpty(p)) return null;
            try { return DocumentIdentity.Peek(p); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
        }
    }

    /// <summary>A current first-page thumbnail for <paramref name="doc"/>, rendering it once if needed
    /// (thread pool, one at a time). Null for a missing, locked or encrypted file — an encrypted
    /// document's page is never written to disk — or when the index is off this session.</summary>
    public async Task<string?> EnsureThumbnailAsync(RecentDocument doc)
    {
        if (doc.ContentId is not { } id) return null;
        string thumb = RecentDocuments.ThumbnailPath(id);
        if (RecentDocuments.ThumbnailIsCurrent(thumb, doc.Path)) return thumb;
        if (_library is null || doc.Missing || doc.Locked) return File.Exists(thumb) ? thumb : null;

        await _thumbnailGate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                if (RecentDocuments.ThumbnailIsCurrent(thumb, doc.Path)) return thumb;
                return RenderThumbnail(doc.Path, thumb) ? thumb : null;
            });
        }
        finally { _thumbnailGate.Release(); }
    }

    private bool RenderThumbnail(string pdfPath, string thumbPath)
    {
        const int Width = 240; // ~2× the card's thumbnail width, for high-DPI screens
        IPdfService? pdf = null;
        try
        {
            pdf = _thumbnailFactory.CreatePdfService(pdfPath, null);
            if (pdf.PageCount == 0) return false;
            var (pageWidth, _) = pdf.GetPageSize(0); // points
            int dpi = (int)Math.Clamp(Math.Round(Width * 72.0 / Math.Max(1, pageWidth)), 12, 150);
            using var rendered = pdf.RenderPage(0, dpi) as RailReader.Renderer.Skia.SkiaRenderedPage;
            if (rendered?.Bitmap is not { } bitmap) return false;
            using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
            Directory.CreateDirectory(Path.GetDirectoryName(thumbPath)!);
            string tmp = thumbPath + $".{Environment.ProcessId}.tmp";
            using (var fs = File.Create(tmp)) data.SaveTo(fs);
            File.Move(tmp, thumbPath, overwrite: true);
            return true;
        }
        catch (PdfPasswordRequiredException) { return false; }
        catch (Exception ex)
        {
            _logger.Error($"[Library] thumbnail failed for {Path.GetFileName(pdfPath)}", ex);
            return false;
        }
        finally { (pdf as IDisposable)?.Dispose(); }
    }

    private readonly RailReader.Renderer.Skia.SkiaPdfServiceFactory _thumbnailFactory = new();

    /// <summary>A card was clicked: opens the document at its saved position.</summary>
    public async Task OpenRecentDocument(RecentDocument doc)
    {
        if (doc.Missing)
        {
            ShowStatusToast($"{Path.GetFileName(doc.Path)} is no longer where it was. Search the library (Ctrl+Shift+F) to locate it.");
            return;
        }
        await OpenDocument(doc.Path);
    }

    /// <summary>Indexes notes/OCR text of every open document, then stops the writer (bounded wait).</summary>
    private void ShutdownLibrary()
    {
        _libraryWindow?.Close();
        if (_library is null) return;
        // Save notes edited since the last auto-save now, while the writer is still running: Saved
        // re-indexes them. (Core's own flush in DocumentController.Dispose comes after the writer stops.)
        try { _controller.AnnotationManager.FlushAll(); }
        catch (Exception ex) { _logger.Error("[Library] annotation flush at shutdown failed", ex); }
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
