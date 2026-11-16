using RailReader.Core;
using RailReader.Core.Services;
using RailReader2.Library;

namespace RailReader.Cli.Commands;

/// <summary><c>library probe|bench</c>: Phase 0 of the library search design. <c>probe</c> checks the
/// bundled SQLite has FTS5; <c>bench</c> indexes a folder into a throwaway database and measures it.
/// Neither touches the app's config directory.</summary>
public static class LibraryCommand
{
    public static int Execute(string[] args, IPdfServiceFactory factory, ILogger logger)
    {
        if (args.Length == 0 || Program.HasHelpFlag(args)) { PrintHelp(); return 0; }
        return args[0] switch
        {
            "probe" => Probe(),
            "bench" => Bench(args[1..], factory),
            _ => Program.Fail($"Unknown library subcommand '{args[0]}'."),
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

    static int Bench(string[] args, IPdfServiceFactory factory)
    {
        string? folder = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (folder is null || !Directory.Exists(folder))
            return Program.Fail("library bench needs a folder of PDFs.");
        string db = Program.GetOption(args, "db") ?? Path.Combine(Path.GetTempPath(), "railreader2-library-bench.db");
        var pdfs = Directory.EnumerateFiles(folder, "*.pdf", SearchOption.AllDirectories).Order().ToList();
        string[] queries =
        [
            "attention", "\"self-attention\"", "estimator", "heteroscedasticity", "NEAR(transformer forecasting, 5)",
            "contrast*", "marchenko", "ridge AND regression", "naïve", "zzqxnotaword",
        ];

        var r = LibraryBench.Run(pdfs, db, factory, queries, n => Console.Error.WriteLine($"  {n}"));
        Console.WriteLine();
        foreach (var d in r.Documents)
            Console.WriteLine($"{d.Pages,5} p {d.RawChars / 1024.0,8:F0} KB {d.ExtractMs,8:F0} ms  {d.Name}{(d.Error is null ? "" : $"  ERROR: {d.Error}")}");
        Console.WriteLine();
        Console.WriteLine($"Documents: {r.Documents.Count}, pages: {r.TotalPages}, text: {r.TotalChars / 1048576.0:F2} M chars");
        Console.WriteLine($"Extraction: {r.ExtractSeconds:F2} s ({r.TotalPages / Math.Max(r.ExtractSeconds, 1e-9):F0} pages/s)");
        Console.WriteLine($"Insert: {r.InsertSeconds:F2} s");
        Console.WriteLine($"Database: {r.DatabaseBytes / 1048576.0:F2} MB ({100.0 * r.DatabaseBytes / Math.Max(r.TotalChars, 1):F0} % of text chars; {r.DatabaseBytes / 1024.0 / Math.Max(r.TotalPages, 1):F1} KB/page)");
        Console.WriteLine();
        foreach (var q in r.Queries)
            Console.WriteLine($"{q.MedianMs,7:F2} ms median {q.MaxMs,7:F2} ms max {q.Hits,3} hits  {q.Query}");
        Console.WriteLine($"\nDatabase left at {db}");
        return 0;
    }

    static void PrintHelp()
    {
        Console.WriteLine("Usage: railreader2-cli library probe");
        Console.WriteLine("       railreader2-cli library bench <folder> [--db <path>]");
        Console.WriteLine();
        Console.WriteLine("  probe   Check the bundled SQLite supports FTS5 (exit 1 if not)");
        Console.WriteLine("  bench   Index every PDF under <folder> into a throwaway database and report");
        Console.WriteLine("          extraction throughput, database size and query latency");
    }
}
