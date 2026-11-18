using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader2.Library;

/// <summary>Turns a PDF on disk into <see cref="DocumentContent"/> (design §4). Opens its own PDF handle,
/// so it is safe on any thread: every PDFium call goes through Core's services, which take
/// <c>PdfiumGate.Lock</c> per call, so it interleaves with page renders instead of blocking them.</summary>
public static class LibraryIndexer
{
    /// <summary>Extracts a document. An encrypted PDF comes back <see cref="DocumentState.Locked"/> with no
    /// text (design §5.4, Q4: its text would sit in the index in plaintext); a PDF that can't be read
    /// comes back <see cref="DocumentState.Failed"/>. <paramref name="annotations"/> is a snapshot of the
    /// document's annotations, or null to index none.</summary>
    public static DocumentContent Extract(
        string path, string contentId, IPdfServiceFactory factory, AnnotationFile? annotations = null,
        CancellationToken ct = default)
    {
        var fi = new FileInfo(path);
        long size = fi.Exists ? fi.Length : 0, mtime = fi.Exists ? fi.LastWriteTimeUtc.Ticks : 0;

        IPdfService pdf;
        try
        {
            pdf = factory.CreatePdfService(path, null);
        }
        catch (PdfPasswordRequiredException)
        {
            return new DocumentContent(contentId, path, size, mtime, 0, true, [], [], [], DocumentState.Locked);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DocumentContent(contentId, path, size, mtime, 0, false, [], [], [], DocumentState.Failed, ex.Message);
        }

        try
        {
            var text = factory.CreatePdfTextService();
            var pageTexts = new PageText?[pdf.PageCount];
            var pages = new List<PageEntry>(pdf.PageCount);
            for (int p = 0; p < pdf.PageCount; p++)
            {
                ct.ThrowIfCancellationRequested();
                var pt = text.ExtractPageText(pdf.PdfBytes, p, null);
                pageTexts[p] = pt;
                pages.Add(new PageEntry(p, TextNormalizer.Normalize(pt.Text), pt.FromOcr));
            }
            var outline = FlattenOutline(pdf.Outline, pdf.PageCount);
            var notes = annotations is null ? [] : Notes(annotations, p => (uint)p < (uint)pageTexts.Length ? pageTexts[p] : null);
            // Optional backend capability (Core 0.87.0): PDFium's text service has it. Reuses the document
            // the text extraction above already opened (same bytes).
            var info = text is IPdfMetadataService m ? DocumentInfoCleaner.From(m.GetMetadata(pdf.PdfBytes)) : null;
            return new DocumentContent(contentId, path, size, mtime, pdf.PageCount, false, pages, outline, notes, Info: info);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DocumentContent(contentId, path, size, mtime, pdf.PageCount, false, [], [], [], DocumentState.Failed, ex.Message);
        }
        finally
        {
            (pdf as IDisposable)?.Dispose();
        }
    }

    public static IReadOnlyList<OutlineItem> FlattenOutline(IEnumerable<OutlineEntry>? entries, int pageCount)
    {
        var list = new List<OutlineItem>();
        void Walk(IEnumerable<OutlineEntry>? level)
        {
            if (level is null) return;
            foreach (var e in level)
            {
                if (!string.IsNullOrWhiteSpace(e.Title) && e.Page is int p && p >= 0 && p < pageCount)
                    list.Add(new OutlineItem(TextNormalizer.Normalize(e.Title), p));
                Walk(e.Children);
            }
        }
        Walk(entries);
        return list;
    }

    /// <summary>One <see cref="NoteEntry"/> per annotation that carries text (a comment, or text under a
    /// markup) plus one per named bookmark. <paramref name="pageText"/> supplies a page's text so a
    /// highlight indexes the words it covers; it may return null (the highlight then indexes its
    /// comment only).</summary>
    public static IReadOnlyList<NoteEntry> Notes(AnnotationFile file, Func<int, PageText?> pageText)
    {
        var notes = new List<NoteEntry>();
        foreach (var (page, list) in file.Pages)
        {
            foreach (var a in list)
            {
                string? marked = null;
                if (a is TextMarkupAnnotation markup && pageText(page) is { } pt)
                {
                    var parts = new List<string>();
                    foreach (var r in markup.Rects)
                        if (pt.ExtractTextInRect(r.X, r.Y, r.X + r.W, r.Y + r.H) is { Length: > 0 } t)
                            parts.Add(t);
                    marked = parts.Count > 0 ? TextNormalizer.Normalize(string.Join(" ", parts)) : null;
                }
                string? comment = a.EffectiveContents ?? a.Contents;
                comment = string.IsNullOrWhiteSpace(comment) ? null : TextNormalizer.Normalize(comment);
                if (marked is null && comment is null) continue;
                notes.Add(new NoteEntry(page, KindOf(a), marked, comment, a.Author, a.Color, a.NativeId,
                    a.Source != AnnotationSource.InPdf ? NoteSource.RailReader
                        : a.WrittenByRailReader ? NoteSource.InPdfByRailReader : NoteSource.InPdf,
                    string.IsNullOrEmpty(a.InReplyTo) ? null : a.InReplyTo));
            }
        }
        foreach (var b in file.Bookmarks)
            if (!string.IsNullOrWhiteSpace(b.Name))
                notes.Add(new NoteEntry(b.Page, "bookmark", null, TextNormalizer.Normalize(b.Name), null, null, null, NoteSource.RailReader));
        return notes;
    }

    /// <summary>"HighlightAnnotation" → "highlight".</summary>
    public static string KindOf(Annotation a)
    {
        string n = a.GetType().Name;
        if (n.EndsWith("Annotation", StringComparison.Ordinal)) n = n[..^"Annotation".Length];
        return n.ToLowerInvariant();
    }

    /// <summary>A copy of <paramref name="file"/>'s page lists and bookmarks, taken on the thread that owns
    /// it, so a background extraction never enumerates a list the UI is editing. The annotation objects
    /// themselves are shared; only their strings and rects are read.</summary>
    public static AnnotationFile Snapshot(AnnotationFile file)
    {
        var copy = new AnnotationFile();
        foreach (var (page, list) in file.Pages) copy.Pages[page] = [.. list];
        copy.Bookmarks.AddRange(file.Bookmarks);
        return copy;
    }
}
