using System.Diagnostics;
using Microsoft.Data.Sqlite;
using RailReader.Core.Services;

namespace RailReader2.Library;

public sealed record BenchDocument(string Name, int Pages, long RawChars, double ExtractMs, string? Error);

public sealed record BenchQuery(string Query, int Hits, double MedianMs, double MaxMs);

public sealed record BenchResult(
    IReadOnlyList<BenchDocument> Documents, int TotalPages, long TotalChars,
    double ExtractSeconds, double InsertSeconds, long DatabaseBytes, IReadOnlyList<BenchQuery> Queries);

/// <summary>Phase 0 measurement: extract every page of every PDF in a folder, index it into a throwaway
/// FTS5 page table (the §6.1 schema's <c>page_fts</c>), report throughput, size and query latency.</summary>
public static class LibraryBench
{
    public static BenchResult Run(
        IEnumerable<string> pdfPaths, string dbPath, IPdfServiceFactory factory,
        IReadOnlyList<string> queries, Action<string>? progress = null)
    {
        SqliteProbe.EnsureInitialised();
        foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
            if (File.Exists(f)) File.Delete(f);

        var docs = new List<BenchDocument>();
        double extractMs = 0, insertMs = 0;
        int totalPages = 0;
        long totalChars = 0;

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            Exec(conn, "pragma journal_mode = wal");
            Exec(conn, """
                create virtual table page_fts using fts5(
                  body, content_id unindexed, page unindexed, from_ocr unindexed,
                  tokenize = 'porter unicode61 remove_diacritics 2')
                """);

            var text = factory.CreatePdfTextService();
            foreach (var path in pdfPaths)
            {
                string name = Path.GetFileName(path);
                progress?.Invoke(name);
                var sw = Stopwatch.StartNew();
                var pages = new List<(int Page, string Body, bool Ocr)>();
                string? error = null;
                try
                {
                    var pdf = factory.CreatePdfService(path);
                    try
                    {
                        for (int p = 0; p < pdf.PageCount; p++)
                        {
                            var pt = text.ExtractPageText(pdf.PdfBytes, p);
                            pages.Add((p, TextNormalizer.Normalize(pt.Text), pt.FromOcr));
                        }
                    }
                    finally { (pdf as IDisposable)?.Dispose(); }
                }
                catch (Exception ex) { error = ex.Message; }
                double ems = sw.Elapsed.TotalMilliseconds;
                extractMs += ems;

                sw.Restart();
                using (var tx = conn.BeginTransaction())
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "insert into page_fts(body, content_id, page, from_ocr) values ($b, $c, $p, $o)";
                    var b = cmd.Parameters.Add("$b", SqliteType.Text);
                    var c = cmd.Parameters.Add("$c", SqliteType.Text);
                    var pg = cmd.Parameters.Add("$p", SqliteType.Integer);
                    var o = cmd.Parameters.Add("$o", SqliteType.Integer);
                    foreach (var (page, body, ocr) in pages)
                    {
                        b.Value = body; c.Value = name; pg.Value = page; o.Value = ocr ? 1 : 0;
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
                insertMs += sw.Elapsed.TotalMilliseconds;

                long chars = pages.Sum(x => (long)x.Body.Length);
                totalPages += pages.Count;
                totalChars += chars;
                docs.Add(new BenchDocument(name, pages.Count, chars, ems, error));
            }

            Exec(conn, "insert into page_fts(page_fts) values ('optimize')");
            Exec(conn, "pragma wal_checkpoint(truncate)");
            Exec(conn, "vacuum");
        }
        SqliteConnection.ClearAllPools();
        long size = new FileInfo(dbPath).Length;

        var results = new List<BenchQuery>();
        using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
        {
            conn.Open();
            foreach (var q in queries)
            {
                var times = new List<double>();
                int hits = 0;
                for (int run = 0; run < 7; run++)
                {
                    var sw = Stopwatch.StartNew();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = """
                        select content_id, page, snippet(page_fts, 0, '[', ']', '…', 12)
                        from page_fts where page_fts match $q order by bm25(page_fts) limit 20
                        """;
                    cmd.Parameters.AddWithValue("$q", q);
                    hits = 0;
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) { _ = r.GetString(2); hits++; }
                    times.Add(sw.Elapsed.TotalMilliseconds);
                }
                times.Sort();
                results.Add(new BenchQuery(q, hits, times[times.Count / 2], times[^1]));
            }
        }
        SqliteConnection.ClearAllPools();

        return new BenchResult(docs, totalPages, totalChars, extractMs / 1000, insertMs / 1000, size, results);
    }

    static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
