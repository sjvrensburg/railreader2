using System.Text.Json;
using RailReader.Core;
using RailReader.Core.Services;
using RailReader2.Library;

namespace RailReader.Cli.Commands;

/// <summary><c>library probe|search|status|bench</c> (docs/library-search-design.md §7.3, §8).
/// <c>search</c> and <c>status</c> read the app's index read-only; the CLI never writes it.
/// <c>bench</c> builds a throwaway index elsewhere.</summary>
public static class LibraryCommand
{
    public static int Execute(string[] args, IPdfServiceFactory factory, ILogger logger)
    {
        if (args.Length == 0 || (Program.HasHelpFlag(args) && args.Length == 1)) { PrintHelp(); return 0; }
        if (Program.HasHelpFlag(args)) { PrintHelp(); return 0; }
        return args[0] switch
        {
            "probe" => Probe(),
            "search" => Search(args[1..]),
            "status" => Status(args[1..]),
            "bench" => Bench(args[1..], factory),
            _ => Program.Fail($"Unknown library subcommand '{args[0]}'. Run 'library --help'."),
        };
    }

    static int Probe()
    {
        var r = SqliteProbe.Run();
        Console.WriteLine($"SQLite {r.SqliteVersion} ({r.NativeLibrary})");
        Console.WriteLine($"FTS5: {(r.Fts5 ? "yes" : "NO")}{(r.Fts5Error is null ? "" : $" — {r.Fts5Error}")}");
        Console.WriteLine($"Compile options: {string.Join(' ', r.CompileOptions)}");
        return r.Fts5 ? 0 : 1;
    }

    static string DbPath(string[] args) => Program.GetOption(args, "db") ?? LibraryIndex.DefaultPath;

    /// <summary>First argument that is neither a flag nor a flag's value.</summary>
    static string? Positional(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--db" or "--limit" or "--queries") { i++; continue; }
            if (args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            return args[i];
        }
        return null;
    }

    static int Search(string[] args)
    {
        string? text = Positional(args);
        if (string.IsNullOrWhiteSpace(text)) return Program.Fail("library search needs a query (quote it if it has spaces).");
        int limit = int.TryParse(Program.GetOption(args, "limit"), out int n) && n > 0 ? n : 20;
        string db = DbPath(args);
        using var index = LibraryIndex.OpenReadOnly(db);
        if (index is null) return Program.Fail($"No readable library index at {db}. Open some documents in RailReader2 first.");

        var query = LibraryQuery.Parse(text);
        // Bold only on a terminal: piped or JSON output gets plain [brackets], not escape codes.
        bool plainMarks = Program.HasFlag(args, "json") || Console.IsOutputRedirected;
        var hits = index.Search(query, limit, open: plainMarks ? "[" : "\u001b[1m", close: plainMarks ? "]" : "\u001b[0m");
        string me = SafeAuthor();

        if (Program.HasFlag(args, "json"))
        {
            using var w = new Utf8JsonWriter(Console.OpenStandardOutput(), new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            w.WriteStartObject();
            w.WriteString("query", text);
            w.WriteString("match", query.MatchExpression);
            w.WriteStartArray("hits");
            foreach (var h in hits)
            {
                w.WriteStartObject();
                w.WriteString("content_id", h.ContentId);
                w.WriteString("path", h.LastPath);
                w.WriteString("file", h.FileName);
                if (h.Page is int p) w.WriteNumber("page", p); else w.WriteNull("page");
                w.WriteString("source", Label(h, me));
                w.WriteString("snippet", h.Snippet);
                w.WriteNumber("score", Math.Round(h.Score, 4));
                if (h.FromOcr) w.WriteBoolean("ocr_unverified", true);
                if (h.NoteKind is not null) w.WriteString("note_kind", h.NoteKind);
                if (h.Author is not null) w.WriteString("author", h.Author);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
            w.Flush();
            Console.WriteLine();
            return 0;
        }

        if (query.IsEmpty) { Console.WriteLine("Nothing to search for."); return 0; }
        if (hits.Count == 0) { Console.WriteLine("No matches."); return 0; }
        foreach (var h in hits)
        {
            string where = h.Page is int p ? $"p. {p + 1}" : h.Source == HitSource.Details ? "details" : "name";
            Console.WriteLine($"{h.FileName}{(h.Title is { } t ? $" ({t})" : "")} — {where} — {Label(h, me)}{(h.FromOcr ? " (OCR text, unverified)" : "")}");
            Console.WriteLine($"    {h.Snippet}");
        }
        return 0;
    }

    /// <summary>Design Q2: the reader's own notes vs a reviewer's comments. An in-PDF annotation whose
    /// author is the reader's name counts as theirs (their saved notes read back as in-PDF).</summary>
    static string Label(LibraryHit h, string me) => h.Source switch
    {
        HitSource.Page => "page text",
        HitSource.Outline => "outline",
        HitSource.Name => "file name",
        HitSource.Details => "title, author or keywords",
        HitSource.Note when h.NoteKind == "bookmark" => "bookmark",
        HitSource.Note when LibraryResults.IsReviewer(h, me)
            => $"reviewer comment{(h.Author is null ? "" : $" ({h.Author})")}",
        _ => "your note",
    };

    static string SafeAuthor()
    {
        try { return AppConfig.Load().AnnotationAuthor ?? ""; } catch (Exception) { return ""; }
    }

    static int Status(string[] args)
    {
        string db = DbPath(args);
        using var index = LibraryIndex.OpenReadOnly(db);
        if (index is null) return Program.Fail($"No readable library index at {db}.");
        var s = index.GetStatus();
        Console.WriteLine($"Index: {db}");
        Console.WriteLine($"Documents: {s.Documents} ({s.Locked} locked, {s.Failed} failed)");
        Console.WriteLine($"Pages with text: {s.Pages}, notes: {s.Notes}");
        Console.WriteLine($"Size on disk: {s.DatabaseBytes / 1048576.0:F1} MB");
        if (Program.HasFlag(args, "documents"))
            foreach (var d in index.GetDocuments())
                Console.WriteLine($"  {d.State,-7} {d.PageCount,5} p  {d.FileName}  {(d.LastPath is { } p && !File.Exists(p) ? "(missing)" : "")}");
        return 0;
    }

    static int Bench(string[] args, IPdfServiceFactory factory)
    {
        string? folder = Positional(args);
        if (folder is null || !Directory.Exists(folder)) return Program.Fail("library bench needs a folder of PDFs.");
        string db = Program.GetOption(args, "db") ?? Path.Combine(Path.GetTempPath(), "railreader2-library-bench.db");
        // Case-insensitive where the file system usually is: a differently-cased path is the same file.
        var pathComparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(Path.GetFullPath(db), Path.GetFullPath(LibraryIndex.DefaultPath), pathComparison))
            return Program.Fail("library bench rebuilds its database from scratch; point --db somewhere other than the app's index.");
        string? queriesPath = Program.GetOption(args, "queries");
        var queries = queriesPath is null ? new BenchQuerySet() : LibraryBench.LoadQueries(queriesPath);
        var pdfs = Directory.EnumerateFiles(Path.GetFullPath(folder), "*.pdf", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();

        var r = LibraryBench.Run(pdfs, db, factory, queries,
            loadAnnotations: p => CompositeAnnotationStore.Default.Load(p, null),
            progress: n => Console.Error.WriteLine($"  {n}"));
        bool verbose = Program.HasFlag(args, "verbose");

        Console.WriteLine();
        if (verbose)
        {
            foreach (var d in r.Documents)
                Console.WriteLine($"{d.Pages,5} p {d.Chars / 1024.0,7:F0} KB {d.Notes,4} notes {d.ExtractMs,7:F0} ms  {d.Name}{(d.State == DocumentState.Indexed ? "" : $"  {d.State}: {d.Error}")}");
            Console.WriteLine();
        }
        Console.WriteLine($"Documents: {r.Documents.Count}, pages: {r.TotalPages}, text: {r.TotalChars / 1048576.0:F2} M chars, notes: {r.Documents.Sum(d => d.Notes)}");
        Console.WriteLine($"Indexing: {r.ExtractSeconds + r.InsertSeconds:F2} s ({r.TotalPages / Math.Max(r.ExtractSeconds + r.InsertSeconds, 1e-9):F0} pages/s; extract {r.ExtractSeconds:F2} s, insert {r.InsertSeconds:F2} s)");
        Console.WriteLine($"Database: {r.DatabaseBytes / 1048576.0:F2} MB ({r.DatabaseBytes / 1024.0 / Math.Max(r.TotalPages, 1):F1} KB/page)");
        if (r.Queries.Count == 0) { Console.WriteLine("\nNo --queries file: index built, nothing scored."); return 0; }

        Console.WriteLine();
        foreach (var q in r.Queries.Where(q => verbose || !q.Passed))
        {
            string outcome = q.Spec.Absent ? (q.Passed ? "ok (empty)" : $"FAIL: {q.Hits} hits")
                : q.Rank is int rank ? $"rank {rank}" : "FAIL: not in top 10";
            Console.WriteLine($"{outcome,-20} {q.MedianMs,6:F2} ms  {q.Spec.Q}{(q.Passed || q.TopHit is null ? "" : $"  [top: {q.TopHit}]")}");
        }
        Console.WriteLine();
        Console.WriteLine($"Known-item queries: {r.Known.Count()}, recall@{LibraryBench.TopK}: {r.RecallAtK:P1}, MRR: {r.Mrr:F3}");
        Console.WriteLine($"Absent queries: {r.Absent.Count()}, empty-result precision: {r.EmptyPrecision:P1}");
        Console.WriteLine($"Query latency p95: {r.P95QueryMs:F2} ms");
        return 0;
    }

    static void PrintHelp()
    {
        Console.WriteLine("Usage: railreader2-cli library probe");
        Console.WriteLine("       railreader2-cli library search <query> [--json] [--limit N] [--db <path>]");
        Console.WriteLine("       railreader2-cli library status [--documents] [--db <path>]");
        Console.WriteLine("       railreader2-cli library bench <folder> [--queries <file>] [--db <path>] [--verbose]");
        Console.WriteLine();
        Console.WriteLine("  probe    Check the bundled SQLite supports FTS5 (exit 1 if not)");
        Console.WriteLine("  search   Search the app's library index (read-only). Words are all required;");
        Console.WriteLine("           \"exact phrase\", prefix*, and -excluded are understood");
        Console.WriteLine("  status   Document, page and note counts, and the index's size");
        Console.WriteLine("  bench    Index every PDF under <folder> into a throwaway database; with --queries,");
        Console.WriteLine("           score a labelled query set (recall@10, MRR, empty-result precision)");
    }
}
