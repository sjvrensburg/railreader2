namespace RailReader2.Library;

/// <summary>One page's indexable text, already normalised.</summary>
public sealed record PageEntry(int Page, string Body, bool FromOcr);

/// <summary>One outline (bookmark-tree) entry: its title and target page.</summary>
public sealed record OutlineItem(string Title, int Page);

/// <summary>Where an annotation came from: written into the PDF (possibly by a reviewer), or authored
/// in RailReader2 and kept in its sidecar.</summary>
public enum NoteSource { RailReader = 0, InPdf = 1 }

/// <summary>One annotation or named bookmark, as indexed. <see cref="Kind"/> is a lower-case name
/// ("highlight", "textnote", "freetext", "bookmark", …).</summary>
public sealed record NoteEntry(
    int Page, string Kind, string? MarkedText, string? Comment, string? Author, string? Color,
    string? NativeId, NoteSource Source);

/// <summary>Everything indexed for one document. <see cref="Pages"/> is empty for a locked (encrypted)
/// or unreadable document.</summary>
public sealed record DocumentContent(
    string ContentId, string Path, long FileSize, long FileMtimeTicks, int PageCount, bool Encrypted,
    IReadOnlyList<PageEntry> Pages, IReadOnlyList<OutlineItem> Outline, IReadOnlyList<NoteEntry> Notes,
    DocumentState State = DocumentState.Indexed, string? Error = null);

public enum DocumentState { Indexed, Locked, Failed }

/// <summary>A row of the <c>documents</c> table.</summary>
public sealed record LibraryDocument(
    long Key, string ContentId, string? LastPath, string FileName, int PageCount, long FileSize,
    long FileMtimeTicks, int IndexerVersion, bool Encrypted, DocumentState State,
    DateTime? IndexedUtc, DateTime? LastOpenedUtc, int? LastPage);

public enum HitSource { Page, Outline, Note, Name }

/// <summary>One search result. <see cref="Snippet"/> marks matched terms with the caller's markers.
/// <see cref="Page"/> is 0-based; null for a document-name hit.</summary>
public sealed record LibraryHit(
    string ContentId, string? LastPath, string FileName, int? Page, HitSource Source, string Snippet,
    double Score, bool FromOcr = false, string? NoteKind = null, string? Author = null,
    NoteSource? NoteOrigin = null, string? NativeId = null, string? Category = null);

public sealed record LibraryStatus(int Documents, int Locked, int Failed, long Pages, long Notes, long DatabaseBytes);
