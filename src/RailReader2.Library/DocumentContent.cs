namespace RailReader2.Library;

/// <summary>One page's indexable text, already normalised.</summary>
public sealed record PageEntry(int Page, string Body, bool FromOcr);

/// <summary>One outline (bookmark-tree) entry: its title and target page.</summary>
public sealed record OutlineItem(string Title, int Page);

/// <summary>Where an annotation came from: authored in RailReader2 and kept in its sidecar, written into
/// the PDF (possibly by a reviewer), or written into the PDF by RailReader (Core 0.88.0's origin mark).
/// Stored as its number in <c>note_fts.source</c>; <see cref="InPdfByRailReader"/> needed no schema change.</summary>
public enum NoteSource { RailReader = 0, InPdf = 1, InPdfByRailReader = 2 }

/// <summary>One annotation or named bookmark, as indexed. <see cref="Kind"/> is a lower-case name
/// ("highlight", "textnote", "freetext", "bookmark", …).</summary>
public sealed record NoteEntry(
    int Page, string Kind, string? MarkedText, string? Comment, string? Author, string? Color,
    string? NativeId, NoteSource Source, string? InReplyTo = null);

/// <summary>A note as read back from the index (<see cref="LibraryIndex.GetNotes"/>). <see cref="Category"/> is
/// its colour-category name, if the reader named its colour.</summary>
public sealed record IndexedNote(
    int Page, string Kind, string? MarkedText, string? Comment, string? Author, string? Color,
    string? Category, string? NativeId, NoteSource Source, string? InReplyTo = null);

/// <summary>A document's descriptive metadata (the PDF Info dictionary), normalised. <see cref="Title"/>
/// is null when the PDF's title is missing or is clearly not a title (see <see cref="DocumentInfoCleaner"/>).</summary>
public sealed record DocumentInfo(string? Title, string? Author, string? Subject, string? Keywords)
{
    public static readonly DocumentInfo Empty = new(null, null, null, null);
    public bool IsEmpty => this == Empty;
}

/// <summary>Everything indexed for one document. <see cref="Pages"/> is empty for a locked (encrypted)
/// or unreadable document; <see cref="Info"/> is null when no metadata was read.</summary>
public sealed record DocumentContent(
    string ContentId, string Path, long FileSize, long FileMtimeTicks, int PageCount, bool Encrypted,
    IReadOnlyList<PageEntry> Pages, IReadOnlyList<OutlineItem> Outline, IReadOnlyList<NoteEntry> Notes,
    DocumentState State = DocumentState.Indexed, string? Error = null, DocumentInfo? Info = null);

public enum DocumentState { Indexed, Locked, Failed }

/// <summary>A row of the <c>documents</c> table.</summary>
public sealed record LibraryDocument(
    long Key, string ContentId, string? LastPath, string FileName, int PageCount, long FileSize,
    long FileMtimeTicks, int IndexerVersion, bool Encrypted, DocumentState State,
    DateTime? IndexedUtc, DateTime? LastOpenedUtc, int? LastPage, string? Title = null, string? Authors = null);

/// <summary>Where a hit was found. <see cref="Details"/> is the document's metadata (title, author,
/// subject, keywords); like <see cref="Name"/>, it is a whole-document hit with no page.</summary>
public enum HitSource { Page, Outline, Note, Name, Details }

/// <summary>One search result. <see cref="Snippet"/> marks matched terms with the caller's markers.
/// <see cref="Page"/> is 0-based; null for a document-name hit.</summary>
public sealed record LibraryHit(
    string ContentId, string? LastPath, string FileName, int? Page, HitSource Source, string Snippet,
    double Score, bool FromOcr = false, string? NoteKind = null, string? Author = null,
    NoteSource? NoteOrigin = null, string? NativeId = null, string? Category = null,
    string? Title = null, string? Authors = null);

public sealed record LibraryStatus(int Documents, int Locked, int Failed, long Pages, long Notes, long DatabaseBytes);
