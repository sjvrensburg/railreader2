using System.Collections.Concurrent;
using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader2.Library;

/// <summary>The app's single writer to the library index (design §5): one background thread owns the
/// writable <see cref="LibraryIndex"/>, and every change is a queued job, so nothing ever writes from
/// the UI thread or from two threads at once.
/// <para>Two queues: <b>foreground</b> jobs (a document just opened or closed) run as soon as possible;
/// <b>backfill</b> jobs (documents opened before the index existed, §5.3) run only when the foreground
/// queue is empty and <see cref="IsBusy"/> is false (the app sets it while rail reading, to keep PDFium
/// free for the page being read), one document at a time with a pause between them.</para>
/// <para>No timers: the thread blocks on the queues. Failures are reported through
/// <see cref="OnError"/> and never stop the thread; if the database can't be opened at all the writer
/// turns itself off for the session.</para></summary>
public sealed class LibraryWriter : IDisposable
{
    readonly string _dbPath;
    readonly IPdfServiceFactory _factory;
    readonly BlockingCollection<Action<LibraryIndex>> _foreground = new();
    readonly ConcurrentQueue<string> _backfill = new();
    readonly SemaphoreSlim _signal = new(0);
    readonly CancellationTokenSource _cts = new();
    // Cancelled as soon as Dispose starts: an in-flight backfill document must not hold up the
    // foreground jobs (notes edited this session) queued behind it within Dispose's bounded wait.
    readonly CancellationTokenSource _backfillCts = new();
    readonly Thread _thread;
    int _started;
    readonly ManualResetEventSlim _idle = new(true);
    int _pendingForeground;
    volatile bool _disabled;

    /// <summary>Set from any thread (the app sets it from its frame loop) to hold back the backfill.</summary>
    public volatile bool IsBusy;

    /// <summary>Colour-category names (upper-case hex → name), replaced wholesale by the UI thread
    /// whenever the reader renames one; read by the writer thread when indexing notes.</summary>
    public volatile IReadOnlyDictionary<string, string> Categories = new Dictionary<string, string>();

    /// <summary>Called on the writer thread. Message, exception.</summary>
    public Action<string, Exception>? OnError;

    /// <summary>Called on the writer thread when the database had to be rebuilt.</summary>
    public Action? OnRecovered;

    /// <summary>Pause between backfill documents.</summary>
    public TimeSpan BackfillPause { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long to wait before re-checking <see cref="IsBusy"/>.</summary>
    public TimeSpan BusyRecheck { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Documents still waiting in the backfill queue.</summary>
    public int BackfillRemaining => _backfill.Count;

    int _batchTotal, _batchDone;

    /// <summary>Backfill progress for the current batch ("indexing <c>Done</c> of <c>Total</c>"); both 0
    /// when nothing is queued. Most entries are skipped in milliseconds when their index is current.</summary>
    public (int Done, int Total) BackfillProgress
    {
        get
        {
            int total = Volatile.Read(ref _batchTotal);
            return total == 0 ? (0, 0) : (Math.Min(Volatile.Read(ref _batchDone), total), total);
        }
    }

    /// <summary>Raised on the writer thread after each job or backfill document that may have changed the
    /// index, so an open Library window can refresh. Handlers must be quick and must not block.</summary>
    public event Action? Changed;

    public LibraryWriter(string dbPath, IPdfServiceFactory factory)
    {
        _dbPath = dbPath;
        _factory = factory;
        _thread = new Thread(Run) { IsBackground = true, Name = "Library index", Priority = ThreadPriority.BelowNormal };
    }

    // Started on first use, not in the constructor, so OnError/Categories set by an object initializer
    // are in place before the thread can report an open failure.
    void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0) _thread.Start();
    }

    // ------------------------------------------------------------ jobs (any thread)

    /// <summary>A document was opened. Re-extracts it if the index is stale (§5.1), else just refreshes
    /// its notes from <paramref name="annotations"/> (a snapshot, see <see cref="LibraryIndexer.Snapshot"/>);
    /// either way records the open. An encrypted document indexes nothing but its name (§5.4).</summary>
    public void DocumentOpened(string path, string contentId, bool encrypted, AnnotationFile? annotations, int? page)
        => Enqueue(index =>
        {
            if (encrypted)
            {
                if (index.NeedsIndex(contentId, path)) index.ReplaceDocument(Locked(path, contentId));
            }
            else if (index.NeedsIndex(contentId, path))
            {
                index.ReplaceDocument(LibraryIndexer.Extract(path, contentId, _factory, annotations, _cts.Token), CategoryOf);
            }
            else if (annotations is not null)
            {
                index.ReplaceNotes(contentId, NotesWithMarkupText(path, annotations), CategoryOf);
            }
            index.RecordOpen(contentId, path, page, DateTime.UtcNow);
        });

    /// <summary>A document's last tab closed (or the app is closing): re-index its notes and add any page
    /// text that only this session's OCR produced (design Q6).</summary>
    public void DocumentClosed(string path, string contentId, bool encrypted, AnnotationFile? annotations,
        IReadOnlyList<PageEntry> ocrPages, int? page)
        => Enqueue(index =>
        {
            if (index.GetDocument(contentId) is null) return;
            if (!encrypted)
            {
                if (annotations is not null) index.ReplaceNotes(contentId, NotesWithMarkupText(path, annotations), CategoryOf);
                if (ocrPages.Count > 0) index.AddOcrPages(contentId, ocrPages);
            }
            index.RecordOpen(contentId, path, page, DateTime.UtcNow);
        });

    /// <summary>A colour category was renamed (null = name removed).</summary>
    public void CategoryRenamed(string hexColour, string? name) => Enqueue(index => index.SetCategory(hexColour, name));

    /// <summary>Drops a document from the library (design §3): its index rows only.</summary>
    public void Remove(string contentId) => Enqueue(index =>
    {
        index.Remove(contentId);
        TryDelete(() => File.Delete(RecentDocuments.ThumbnailPath(contentId, _dbPath)));
    });

    /// <summary>The reader located a moved file (the caller checked it is the same document): point the
    /// entry at it and re-index it if the file differs from what was indexed.</summary>
    public void Relocate(string contentId, string path) => Enqueue(index =>
    {
        index.SetPath(contentId, path);
        if (index.NeedsIndex(contentId, path)) IndexFromDisk(index, path, contentId, _cts.Token);
    });

    /// <summary>Empties the index (Settings ▸ Library ▸ Clear). Earlier documents stay out until the
    /// reader opens them again or chooses Rebuild.</summary>
    public void Clear()
    {
        ClearBackfill();
        Enqueue(index =>
        {
            index.Clear(backfill: false);
            DeleteThumbnails();
        });
    }

    /// <summary>Empties the index and indexes <paramref name="paths"/> again in the background.</summary>
    public void Rebuild(IEnumerable<string> paths)
    {
        ClearBackfill();
        Enqueue(index =>
        {
            index.Clear(backfill: true);
            DeleteThumbnails();
        });
        Backfill(paths);
    }

    // Thumbnails are part of the index (they show the documents' first pages), so they go with it.
    void DeleteThumbnails()
    {
        var dir = RecentDocuments.ThumbnailDirectory(_dbPath);
        TryDelete(() => { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); });
    }

    void TryDelete(Action delete)
    {
        try { delete(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            OnError?.Invoke("Couldn't delete a library thumbnail", ex);
        }
    }

    void ClearBackfill()
    {
        _backfill.Clear();
        Volatile.Write(ref _batchTotal, 0);
        Volatile.Write(ref _batchDone, 0);
    }

    /// <summary>Queues documents the reader opened before (paths; ids are looked up on the writer thread).
    /// Skips ones whose file is gone or whose index entry is current.</summary>
    public void Backfill(IEnumerable<string> paths)
    {
        int n = 0;
        foreach (var p in paths.Distinct(StringComparer.Ordinal)) { _backfill.Enqueue(p); n++; }
        if (n == 0) return;
        Interlocked.Add(ref _batchTotal, n);
        EnsureStarted();
        _signal.Release();
    }

    /// <summary>Blocks until every queued foreground job has run (tests, shutdown).</summary>
    public bool WaitForForeground(TimeSpan timeout) => _idle.Wait(timeout);

    void Enqueue(Action<LibraryIndex> job)
    {
        if (_disabled || _foreground.IsAddingCompleted) return;
        Interlocked.Increment(ref _pendingForeground);
        _idle.Reset();
        try
        {
            _foreground.Add(job);
            EnsureStarted();
            _signal.Release();
        }
        catch (InvalidOperationException)
        {
            if (Interlocked.Decrement(ref _pendingForeground) == 0) _idle.Set();
        }
    }

    // ------------------------------------------------------------ writer thread

    void Run()
    {
        LibraryIndex index;
        try
        {
            index = LibraryIndex.OpenWritable(_dbPath, out bool recovered);
            if (recovered) OnRecovered?.Invoke();
        }
        catch (Exception ex)
        {
            _disabled = true;
            OnError?.Invoke($"Library index unavailable ({_dbPath}); library search is off this session", ex);
            DrainWithoutRunning();
            return;
        }

        using (index)
        {
            var token = _cts.Token;
            while (!token.IsCancellationRequested)
            {
                if (_foreground.TryTake(out var job))
                {
                    RunJob(index, job);
                    continue;
                }
                if (_foreground.IsAddingCompleted) break;

                if (!_backfill.IsEmpty && !IsBusy)
                {
                    if (_backfill.TryDequeue(out var path))
                    {
                        bool indexed = BackfillOne(index, path, _backfillCts.Token);
                        Interlocked.Increment(ref _batchDone);
                        if (_backfill.IsEmpty) { Volatile.Write(ref _batchTotal, 0); Volatile.Write(ref _batchDone, 0); }
                        if (indexed || _backfill.IsEmpty) RaiseChanged();
                        if (!indexed) continue; // skipped in milliseconds: no pause needed
                    }
                    if (Wait(BackfillPause, token)) continue;
                    break;
                }
                // Nothing runnable: sleep until a job arrives, or re-check a busy backfill later.
                try
                {
                    if (!_backfill.IsEmpty) _signal.Wait(BusyRecheck, token);
                    else _signal.Wait(token);
                }
                catch (OperationCanceledException) { break; }
            }
        }
        DrainWithoutRunning();
    }

    void RunJob(LibraryIndex index, Action<LibraryIndex> job)
    {
        try { job(index); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { OnError?.Invoke("Library index job failed", ex); }
        finally
        {
            RaiseChanged(); // before the job counts as done, so a WaitForForeground caller has seen it
            if (Interlocked.Decrement(ref _pendingForeground) == 0) _idle.Set();
        }
    }

    void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { OnError?.Invoke("Library change handler failed", ex); }
    }

    /// <summary>True when it indexed something (false: skipped, current, or failed).</summary>
    bool BackfillOne(LibraryIndex index, string path, CancellationToken token)
    {
        try
        {
            if (!index.BackfillEnabled) return false;
            if (!File.Exists(path)) return false;
            if (DocumentIdentity.Peek(path) is not { } id) return false;
            if (!index.NeedsIndex(id, path)) return false;
            IndexFromDisk(index, path, id, token);
            return true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { OnError?.Invoke($"Library backfill failed for {Path.GetFileName(path)}", ex); }
        return false;
    }

    void IndexFromDisk(LibraryIndex index, string path, string id, CancellationToken token)
    {
        AnnotationFile? annotations = null;
        try { annotations = CompositeAnnotationStore.Default.Load(path, null); }
        catch (Exception) { /* encrypted or unreadable: index without notes */ }
        index.ReplaceDocument(LibraryIndexer.Extract(path, id, _factory, annotations, token), CategoryOf);
    }

    void DrainWithoutRunning()
    {
        while (_foreground.TryTake(out _))
            if (Interlocked.Decrement(ref _pendingForeground) == 0) _idle.Set();
        _idle.Set();
    }

    static bool Wait(TimeSpan t, CancellationToken token)
    {
        try { Task.Delay(t, token).Wait(token); return true; }
        catch (OperationCanceledException) { return false; }
    }

    string? CategoryOf(string? hex)
        => hex is not null && Categories.TryGetValue(hex.Trim().ToUpperInvariant(), out var n) && n.Length > 0 ? n : null;

    /// <summary>Notes for an already-indexed document: page text is read only for pages carrying markup.</summary>
    IReadOnlyList<NoteEntry> NotesWithMarkupText(string path, AnnotationFile annotations)
    {
        var markupPages = annotations.Pages.Where(kv => kv.Value.Any(a => a is TextMarkupAnnotation)).Select(kv => kv.Key).ToHashSet();
        if (markupPages.Count == 0) return LibraryIndexer.Notes(annotations, _ => null);
        IPdfService? pdf = null;
        try
        {
            pdf = _factory.CreatePdfService(path, null);
            var text = _factory.CreatePdfTextService();
            var cache = new Dictionary<int, PageText>();
            foreach (var p in markupPages)
                if (p >= 0 && p < pdf.PageCount) cache[p] = text.ExtractPageText(pdf.PdfBytes, p, null);
            return LibraryIndexer.Notes(annotations, p => cache.GetValueOrDefault(p));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unreadable file or page: index the comments without the text under highlights, rather than
            // failing the whole job (which would also skip recording the open).
            return LibraryIndexer.Notes(annotations, _ => null);
        }
        finally
        {
            (pdf as IDisposable)?.Dispose();
        }
    }

    static DocumentContent Locked(string path, string contentId)
    {
        var fi = new FileInfo(path);
        return new DocumentContent(contentId, path, fi.Exists ? fi.Length : 0, fi.Exists ? fi.LastWriteTimeUtc.Ticks : 0,
            0, true, [], [], [], DocumentState.Locked);
    }

    /// <summary>Stops the writer: finishes the foreground jobs already queued (up to
    /// <paramref name="timeout"/>, so a close never hangs on a big document), abandons the backfill.</summary>
    public void Dispose(TimeSpan timeout)
    {
        _backfill.Clear();
        _backfillCts.Cancel();
        _foreground.CompleteAdding();
        if (Volatile.Read(ref _started) == 0) { _cts.Cancel(); return; }
        _signal.Release();
        if (!_thread.Join(timeout))
        {
            _cts.Cancel();
            _signal.Release();
            _thread.Join(TimeSpan.FromSeconds(1));
        }
        _cts.Cancel();
    }

    public void Dispose() => Dispose(TimeSpan.FromSeconds(3));
}
