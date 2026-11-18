using System.Globalization;
using System.Text.Json;
using RailReader2.Library;

namespace RailReader.Cli.Mcp;

/// <summary>The read-only tools the MCP server offers over the library index (roadmap M8): the reader's
/// notes and the indexed page text of the documents they have opened.
/// <para>What a tool may return is narrow on purpose. Documents are named by title, file name and an
/// opaque short id, never by path or folder. Nothing at all is returned for an encrypted document (the
/// index keeps only its file name, and that stays out too) or for one that failed to index. No PDF bytes
/// or images. The index is opened read-only for each call and never written.</para></summary>
public sealed class LibraryTools
{
    /// <summary>Length of the opaque document id: a prefix of the content id, which is the SHA-256 of the
    /// file's bytes when first seen. Long enough to be unique in any real library.</summary>
    public const int IdLength = 12;
    public const int DefaultLimit = 20, MaxLimit = 100;

    // Snippet markers: Markdown bold reads naturally to an agent; brackets would clash with citations.
    const string Open = "**", Close = "**";

    readonly Func<LibraryIndex?> _open;
    readonly Func<string?> _readerName;

    /// <param name="open">Opens the index read-only; null when there is none (nothing opened in the app yet).</param>
    /// <param name="readerName">The reader's annotation author name, to tell their notes from a reviewer's.</param>
    public LibraryTools(Func<LibraryIndex?> open, Func<string?> readerName)
    {
        _open = open;
        _readerName = readerName;
    }

    public sealed record ToolSpec(string Name, string Title, string Description, string InputSchema);

    public static IReadOnlyList<ToolSpec> Specs { get; } =
    [
        new("search_notes", "Search my notes",
            "Full-text search over the reader's annotations in every document they have opened in RailReader2: " +
            "highlighted text, comments, replies and named bookmarks, including reviewers' comments written into the PDFs. " +
            "Words are stemmed; \"quoted phrases\", prefix* and -excluded words work. Matched words are wrapped in **bold**. " +
            "Each hit names the document (title, file name, id), the 1-based page, the note's kind, author, colour category " +
            "and whether it is the reader's own note or a reviewer's.",
            """
            {"type":"object","properties":{
              "query":{"type":"string","description":"Words to search for."},
              "kind":{"type":"string","description":"Only this kind of note, e.g. highlight, underline, textnote, freetext, bookmark."},
              "document":{"type":"string","description":"Only this document: its id, or its exact title or file name."},
              "limit":{"type":"integer","minimum":1,"maximum":100,"description":"Most hits to return (default 20)."}},
             "required":["query"],"additionalProperties":false}
            """),
        new("search_pages", "Search page text",
            "Full-text search over the page text of the documents the reader has opened in RailReader2, so a passage can be " +
            "quoted, not only the reader's notes about it. Same query syntax as search_notes. Text that came from OCR is " +
            "flagged ocr_unverified: it may contain recognition errors.",
            """
            {"type":"object","properties":{
              "query":{"type":"string","description":"Words to search for."},
              "document":{"type":"string","description":"Only this document: its id, or its exact title or file name."},
              "limit":{"type":"integer","minimum":1,"maximum":100,"description":"Most hits to return (default 20)."}},
             "required":["query"],"additionalProperties":false}
            """),
        new("list_documents", "List documents",
            "Lists the documents in the reader's RailReader2 library, most recently opened first: id, title, file name, " +
            "authors, page count, number of notes and when it was last opened.",
            """{"type":"object","properties":{},"additionalProperties":false}"""),
        new("get_document_notes", "Get a document's notes",
            "Every note in one document, in page order: the highlighted (marked) text, the comment, author, colour category, " +
            "and whether it is the reader's own note or a reviewer's. Replies appear as their own notes (kind textnote).",
            """
            {"type":"object","properties":{
              "document":{"type":"string","description":"The document's id (from list_documents or a search hit), or its exact title or file name."}},
             "required":["document"],"additionalProperties":false}
            """),
    ];

    /// <summary>Thrown for a call the agent should correct (bad argument, unknown document); its message is
    /// returned as a tool error, which the agent sees.</summary>
    public sealed class ToolException(string message) : Exception(message);

    /// <summary>Runs a tool and returns its result as compact JSON text. Throws <see cref="ToolException"/>
    /// for a call the agent can fix, <see cref="ArgumentException"/> for an unknown tool.</summary>
    public string Call(string name, JsonElement args)
    {
        if (!Specs.Any(s => s.Name == name)) throw new ArgumentException($"Unknown tool: {name}");
        using var index = _open() ?? throw new ToolException(
            "The RailReader2 library is empty: no library index exists yet. Open some documents in RailReader2 first.");
        var docs = Visible(index);
        return name switch
        {
            "search_notes" => SearchNotes(index, docs, args),
            "search_pages" => SearchPages(index, docs, args),
            "list_documents" => ListDocuments(index, docs),
            "get_document_notes" => DocumentNotes(index, docs, args),
            _ => throw new ArgumentException($"Unknown tool: {name}"),
        };
    }

    /// <summary>The documents a tool may mention, by content id: indexed and not encrypted.</summary>
    static Dictionary<string, LibraryDocument> Visible(LibraryIndex index)
        => index.GetDocuments().Where(d => d.State == DocumentState.Indexed && !d.Encrypted)
            .ToDictionary(d => d.ContentId, StringComparer.Ordinal);

    string SearchNotes(LibraryIndex index, Dictionary<string, LibraryDocument> docs, JsonElement args)
    {
        var query = ParseQuery(args);
        string? kind = OptString(args, "kind")?.Trim().ToLowerInvariant();
        int limit = Limit(args);
        string? only = OptString(args, "document") is { } d ? Resolve(docs, d).ContentId : null;
        string? me = SafeReaderName();
        // Ask for more than needed: hits on hidden documents or of another kind are dropped afterwards.
        var hits = index.Search(query, Math.Min(limit * 5, 1000), Open, Close, new HashSet<HitSource> { HitSource.Note }, only)
            .Where(h => docs.ContainsKey(h.ContentId) && (kind is null or "" || h.NoteKind == kind))
            .Take(limit).ToList();
        return Json(w =>
        {
            w.WriteStartObject();
            w.WriteString("query", Str(args, "query"));
            w.WriteStartArray("hits");
            foreach (var h in hits)
            {
                w.WriteStartObject();
                WriteDocRef(w, docs[h.ContentId]);
                if (h.Page is int p) w.WriteNumber("page", p + 1);
                if (h.NoteKind is { } k) w.WriteString("kind", k);
                w.WriteString("snippet", h.Snippet);
                if (h.Author is { } a) w.WriteString("author", a);
                if (h.Category is { Length: > 0 } c) w.WriteString("category", c);
                if (h.NoteKind != "bookmark") w.WriteString("owner", LibraryResults.IsReviewer(h, me) ? "reviewer" : "reader");
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    static string SearchPages(LibraryIndex index, Dictionary<string, LibraryDocument> docs, JsonElement args)
    {
        var query = ParseQuery(args);
        int limit = Limit(args);
        string? only = OptString(args, "document") is { } d ? Resolve(docs, d).ContentId : null;
        var hits = index.Search(query, Math.Min(limit * 5, 1000), Open, Close, new HashSet<HitSource> { HitSource.Page }, only)
            .Where(h => docs.ContainsKey(h.ContentId)).Take(limit).ToList();
        return Json(w =>
        {
            w.WriteStartObject();
            w.WriteString("query", Str(args, "query"));
            w.WriteStartArray("hits");
            foreach (var h in hits)
            {
                w.WriteStartObject();
                WriteDocRef(w, docs[h.ContentId]);
                if (h.Page is int p) w.WriteNumber("page", p + 1);
                w.WriteString("snippet", h.Snippet);
                if (h.FromOcr) w.WriteBoolean("ocr_unverified", true);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    static string ListDocuments(LibraryIndex index, Dictionary<string, LibraryDocument> docs)
    {
        var counts = index.NoteCounts();
        // GetDocuments is most recently opened first; keep that order.
        var ordered = index.GetDocuments().Where(d => docs.ContainsKey(d.ContentId)).ToList();
        return Json(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("documents");
            foreach (var d in ordered)
            {
                w.WriteStartObject();
                WriteDocRef(w, d);
                if (d.Authors is { } a) w.WriteString("authors", a);
                w.WriteNumber("pages", d.PageCount);
                w.WriteNumber("notes", counts.GetValueOrDefault(d.ContentId));
                if (d.LastOpenedUtc is { } t)
                    w.WriteString("last_opened", t.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    string DocumentNotes(LibraryIndex index, Dictionary<string, LibraryDocument> docs, JsonElement args)
    {
        var doc = Resolve(docs, Str(args, "document"));
        string? me = SafeReaderName();
        var notes = index.GetNotes(doc.ContentId);
        return Json(w =>
        {
            w.WriteStartObject();
            w.WritePropertyName("document");
            w.WriteStartObject();
            WriteDocRef(w, doc, nested: true);
            if (doc.Authors is { } a) w.WriteString("authors", a);
            w.WriteNumber("pages", doc.PageCount);
            w.WriteEndObject();
            w.WriteStartArray("notes");
            foreach (var n in notes)
            {
                w.WriteStartObject();
                w.WriteNumber("page", n.Page + 1);
                w.WriteString("kind", n.Kind);
                if (n.MarkedText is { } m) w.WriteString(n.Kind == "bookmark" ? "name" : "marked_text", m);
                if (n.Comment is { } c) w.WriteString(n.Kind == "bookmark" ? "name" : "comment", c);
                if (n.Author is { } au) w.WriteString("author", au);
                if (n.Category is { } cat) w.WriteString("category", cat);
                if (n.Kind != "bookmark")
                {
                    bool inPdf = n.Source is NoteSource.InPdf or NoteSource.InPdfByRailReader;
                    bool reviewer = NoteOwnership.IsReviewer(inPdf, n.Source == NoteSource.InPdfByRailReader, n.Author, me);
                    w.WriteString("owner", reviewer ? "reviewer" : "reader");
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    /// <summary>Finds a document by id prefix (at least 6 characters), or by exact title or file name
    /// (case-insensitive). Ambiguity is an error naming the candidates, never a guess.</summary>
    static LibraryDocument Resolve(Dictionary<string, LibraryDocument> docs, string key)
    {
        key = key.Trim();
        if (key.Length == 0) throw new ToolException("document is empty.");
        List<LibraryDocument> matches = [];
        if (key.Length >= 6 && key.All(Uri.IsHexDigit))
            matches = docs.Values.Where(d => d.ContentId.StartsWith(key, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
            matches = docs.Values.Where(d => string.Equals(d.Title, key, StringComparison.OrdinalIgnoreCase)
                || string.Equals(d.FileName, key, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileNameWithoutExtension(d.FileName), key, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new ToolException($"No document matches \"{key}\". Use list_documents to see the documents and their ids."),
            _ => throw new ToolException($"\"{key}\" matches {matches.Count} documents: "
                + string.Join("; ", matches.Take(10).Select(d => $"{d.Title ?? d.FileName} (id {ShortId(d.ContentId)})"))
                + ". Pass the id instead."),
        };
    }

    public static string ShortId(string contentId) => contentId.Length > IdLength ? contentId[..IdLength] : contentId;

    static void WriteDocRef(Utf8JsonWriter w, LibraryDocument d, bool nested = false)
    {
        w.WriteString(nested ? "id" : "document_id", ShortId(d.ContentId));
        if (d.Title is { } t) w.WriteString("title", t);
        w.WriteString("file_name", d.FileName);
    }

    static LibraryQuery ParseQuery(JsonElement args)
    {
        var query = LibraryQuery.Parse(Str(args, "query"));
        if (query.IsEmpty) throw new ToolException("query has no words to search for.");
        return query;
    }

    static int Limit(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("limit", out var v)) return DefaultLimit;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int n) || n < 1)
            throw new ToolException("limit must be a positive whole number.");
        return Math.Min(n, MaxLimit);
    }

    static string Str(JsonElement args, string name)
        => OptString(args, name) ?? throw new ToolException($"{name} is required.");

    static string? OptString(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
            return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : throw new ToolException($"{name} must be a string.");
    }

    string? SafeReaderName()
    {
        try { return _readerName(); } catch (Exception) { return null; }
    }

    static string Json(Action<Utf8JsonWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            write(w);
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}
