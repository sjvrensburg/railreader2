using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader2.Library;

/// <summary>A labelled known-item query (tests/library/queries.json). <see cref="Expect"/> lists the
/// documents (and optionally 0-based pages) that should rank first; an <see cref="Absent"/> query must
/// return nothing.</summary>
public sealed class BenchQuerySpec
{
    [JsonPropertyName("q")] public string Q { get; set; } = "";
    [JsonPropertyName("expect")] public List<BenchExpectation> Expect { get; set; } = [];
    [JsonPropertyName("absent")] public bool Absent { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
}

public sealed class BenchExpectation
{
    [JsonPropertyName("doc")] public string Doc { get; set; } = "";
    [JsonPropertyName("page")] public int? Page { get; set; }
}

public sealed class BenchQuerySet
{
    [JsonPropertyName("queries")] public List<BenchQuerySpec> Queries { get; set; } = [];
}

[JsonSerializable(typeof(BenchQuerySet))]
[JsonSourceGenerationOptions(ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
internal partial class LibraryJsonContext : JsonSerializerContext;

public sealed record BenchDocument(string Name, int Pages, long Chars, int Notes, double ExtractMs, DocumentState State, string? Error);

/// <summary>One query's outcome: <see cref="Rank"/> is the 1-based rank of the first expected hit
/// (null if not in the top <see cref="LibraryBench.TopK"/>), or for an absent query, null when it
/// correctly returned nothing.</summary>
public sealed record BenchQueryResult(BenchQuerySpec Spec, int Hits, int? Rank, bool Passed, double MedianMs, string? TopHit);

public sealed record BenchResult(
    IReadOnlyList<BenchDocument> Documents, int TotalPages, long TotalChars, double ExtractSeconds,
    double InsertSeconds, long DatabaseBytes, IReadOnlyList<BenchQueryResult> Queries)
{
    public IEnumerable<BenchQueryResult> Known => Queries.Where(q => !q.Spec.Absent);
    public IEnumerable<BenchQueryResult> Absent => Queries.Where(q => q.Spec.Absent);
    public double RecallAtK => Known.Any() ? Known.Count(q => q.Rank is not null) / (double)Known.Count() : double.NaN;
    public double Mrr => Known.Any() ? Known.Average(q => q.Rank is int r ? 1.0 / r : 0) : double.NaN;
    public double EmptyPrecision => Absent.Any() ? Absent.Count(q => q.Passed) / (double)Absent.Count() : double.NaN;
    public double P95QueryMs
    {
        get
        {
            var t = Queries.Select(q => q.MedianMs).Order().ToList();
            return t.Count == 0 ? 0 : t[(int)Math.Min(t.Count - 1, Math.Ceiling(0.95 * t.Count) - 1)];
        }
    }
}

/// <summary>Builds a throwaway index from a folder through the real indexer (design §8) and scores a
/// labelled query set: recall@10, MRR, empty-result precision, plus throughput, size and latency.
/// Content ids are file names here, so labels can name files.</summary>
public static class LibraryBench
{
    public const int TopK = 10;

    public static BenchQuerySet LoadQueries(string path)
    {
        using var s = File.OpenRead(path);
        return JsonSerializer.Deserialize(s, LibraryJsonContext.Default.BenchQuerySet) ?? new BenchQuerySet();
    }

    public static BenchResult Run(
        IEnumerable<string> pdfPaths, string dbPath, IPdfServiceFactory factory, BenchQuerySet queries,
        Func<string, AnnotationFile?>? loadAnnotations = null, Action<string>? progress = null)
    {
        foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm", dbPath + ".unreadable" })
            if (File.Exists(f)) File.Delete(f);

        var docs = new List<BenchDocument>();
        double extractMs = 0, insertMs = 0;
        int totalPages = 0;
        long totalChars = 0;
        using (var index = LibraryIndex.OpenWritable(dbPath, out _))
        {
            foreach (var path in pdfPaths)
            {
                string name = Path.GetFileName(path);
                progress?.Invoke(name);
                var sw = Stopwatch.StartNew();
                AnnotationFile? annotations = null;
                try { annotations = loadAnnotations?.Invoke(path); } catch (Exception) { /* index without notes */ }
                var content = LibraryIndexer.Extract(path, name, factory, annotations);
                double ems = sw.Elapsed.TotalMilliseconds;
                extractMs += ems;
                sw.Restart();
                index.ReplaceDocument(content);
                insertMs += sw.Elapsed.TotalMilliseconds;
                long chars = content.Pages.Sum(p => (long)p.Body.Length);
                totalPages += content.PageCount;
                totalChars += chars;
                docs.Add(new BenchDocument(name, content.PageCount, chars, content.Notes.Count, ems, content.State, content.Error));
            }
            index.Optimize();
        }
        long size = new FileInfo(dbPath).Length;

        var results = new List<BenchQueryResult>();
        using (var index = LibraryIndex.OpenReadOnly(dbPath) ?? throw new InvalidOperationException("Bench index unreadable"))
        {
            foreach (var spec in queries.Queries)
            {
                var query = LibraryQuery.Parse(spec.Q);
                var times = new List<double>();
                IReadOnlyList<LibraryHit> hits = [];
                for (int run = 0; run < 5; run++)
                {
                    var sw = Stopwatch.StartNew();
                    hits = index.Search(query, TopK);
                    times.Add(sw.Elapsed.TotalMilliseconds);
                }
                times.Sort();
                int? rank = null;
                if (!spec.Absent)
                {
                    for (int i = 0; i < hits.Count && rank is null; i++)
                        if (spec.Expect.Any(e => Matches(hits[i], e))) rank = i + 1;
                }
                bool passed = spec.Absent ? hits.Count == 0 : rank is not null;
                string? top = hits.Count > 0 ? $"{hits[0].FileName} p{(hits[0].Page is int p ? p.ToString() : "-")} ({hits[0].Source})" : null;
                results.Add(new BenchQueryResult(spec, hits.Count, rank, passed, times[times.Count / 2], top));
            }
        }
        return new BenchResult(docs, totalPages, totalChars, extractMs / 1000, insertMs / 1000, size, results);
    }

    static bool Matches(LibraryHit hit, BenchExpectation e) =>
        string.Equals(hit.FileName, e.Doc, StringComparison.OrdinalIgnoreCase) && (e.Page is null || hit.Page == e.Page);
}
