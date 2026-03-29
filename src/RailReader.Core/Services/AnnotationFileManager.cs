namespace RailReader.Core.Services;

using RailReader.Core.Models;

/// <summary>
/// Manages shared <see cref="AnnotationFile"/> instances so that multiple tabs
/// opening the same PDF share one in-memory object. Reference-counted: the
/// annotation file is loaded on first checkout and saved/released when the last
/// consumer releases it. A single debounced auto-save timer per file prevents
/// redundant writes.
/// </summary>
public sealed class AnnotationFileManager : IDisposable
{
    private readonly IThreadMarshaller _marshaller;
    private readonly Dictionary<string, SharedEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public AnnotationFileManager(IThreadMarshaller marshaller)
    {
        _marshaller = marshaller;
    }

    /// <summary>
    /// Check out the shared <see cref="AnnotationFile"/> for a PDF.
    /// If another tab already has this file open, returns the same instance.
    /// </summary>
    public AnnotationFile Checkout(string pdfPath)
    {
        var key = Path.GetFullPath(pdfPath);

        if (_entries.TryGetValue(key, out var entry))
        {
            entry.RefCount++;
            return entry.Annotations;
        }

        var annotations = AnnotationService.Load(pdfPath) ?? new AnnotationFile
        {
            SourcePdf = Path.GetFileName(pdfPath),
            SourcePdfPath = key,
        };

        _entries[key] = new SharedEntry
        {
            PdfPath = pdfPath,
            Annotations = annotations,
            RefCount = 1,
        };
        return annotations;
    }

    /// <summary>
    /// Release a reference. When the last consumer releases, the file is
    /// flushed to disk (if dirty) and the entry is removed.
    /// </summary>
    public void Release(string pdfPath)
    {
        var key = Path.GetFullPath(pdfPath);
        if (!_entries.TryGetValue(key, out var entry)) return;

        entry.RefCount--;
        if (entry.RefCount <= 0)
        {
            FlushEntry(entry);
            entry.AutoSaveTimer?.Dispose();
            _entries.Remove(key);
        }
    }

    /// <summary>
    /// Mark the annotation file as dirty and start/restart the debounced
    /// auto-save timer (1 second).
    /// </summary>
    public void MarkDirty(string pdfPath)
    {
        var key = Path.GetFullPath(pdfPath);
        if (!_entries.TryGetValue(key, out var entry)) return;

        entry.Dirty = true;
        if (entry.AutoSaveTimer is not null)
            entry.AutoSaveTimer.Change(1000, Timeout.Infinite);
        else
            entry.AutoSaveTimer = new Timer(
                _ => _marshaller.Post(() => FlushEntry(entry)),
                null, 1000, Timeout.Infinite);
    }

    /// <summary>Flush all dirty files to disk. Call on app shutdown.</summary>
    public void FlushAll()
    {
        foreach (var entry in _entries.Values)
            FlushEntry(entry);
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
        {
            FlushEntry(entry);
            entry.AutoSaveTimer?.Dispose();
        }
        _entries.Clear();
    }

    private static void FlushEntry(SharedEntry entry)
    {
        if (!entry.Dirty) return;

        bool hasContent = entry.Annotations.Pages.Values.Any(list => list.Count > 0)
            || entry.Annotations.Bookmarks.Count > 0;

        if (hasContent)
            AnnotationService.Save(entry.PdfPath, entry.Annotations);
        else
            AnnotationService.Delete(entry.PdfPath);

        entry.Dirty = false;
    }

    private sealed class SharedEntry
    {
        public required string PdfPath;
        public required AnnotationFile Annotations;
        public int RefCount;
        public bool Dirty;
        public Timer? AutoSaveTimer;
    }
}
