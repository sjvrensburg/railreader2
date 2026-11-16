using RailReader.Core;
using RailReader.Core.Services;
using RailReader2.Library;

namespace RailReader2.ViewModels;

// Library search index (docs/library-search-design.md, M7 Phase 1): keeps ConfigDir/library/library.db in
// step with the documents the reader opens. All writes go through one LibraryWriter thread; this file only
// takes UI-thread snapshots and hands them over. No search UI yet (Phase 2); the CLI's `library search`
// reads the same database.
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

    /// <summary>Indexes notes/OCR text of every open document, then stops the writer (bounded wait).</summary>
    private void ShutdownLibrary()
    {
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
