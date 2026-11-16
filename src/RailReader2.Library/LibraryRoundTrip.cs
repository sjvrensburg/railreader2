using System.Diagnostics;
using System.Text;
using RailReader.Core.Services;

namespace RailReader2.Library;

/// <summary>An end-to-end check that library search works in the process it runs in: the bundled SQLite
/// loads and has FTS5, a PDF's text extracts through PDFium, the index takes it, and a search finds it.
/// The app runs this headlessly (<c>RailReader2 --library-probe</c>) so CI can check the published and
/// installed Windows app, whose native libraries load differently from the CLI's (railreader2#298).
/// Writes only to <paramref name="dbPath"/>, never the reader's index.</summary>
public static class LibraryRoundTrip
{
    /// <summary>Runs the check. <paramref name="term"/> must occur in the PDF's first page with text.
    /// Returns whether every step passed and a readable report of each step.</summary>
    public static (bool Ok, string Report) Run(string pdfPath, string term, IPdfServiceFactory factory, string dbPath)
    {
        var report = new StringBuilder();
        void Line(string s) => report.AppendLine(s);
        try
        {
            var sqlite = SqliteProbe.Run();
            Line($"SQLite {sqlite.SqliteVersion} ({sqlite.NativeLibrary}), FTS5: {(sqlite.Fts5 ? "yes" : "NO " + sqlite.Fts5Error)}");
            Line($"Native SQLite loaded from: {LoadedModule("e_sqlite3.dll", "libe_sqlite3.so", "libe_sqlite3.dylib") ?? "(not found among loaded modules)"}");
            if (!sqlite.Fts5) return (false, report.ToString());

            foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" }) File.Delete(f);
            var content = LibraryIndexer.Extract(pdfPath, "library-probe", factory, null, CancellationToken.None);
            Line($"Extracted {Path.GetFileName(pdfPath)}: state {content.State}, {content.Pages.Count} of {content.PageCount} pages with text"
                 + (content.Error is { } e ? $", error: {e}" : ""));
            if (content.State != DocumentState.Indexed || content.Pages.Count == 0) return (false, report.ToString());

            using (var index = LibraryIndex.OpenWritable(dbPath, out _))
                index.ReplaceDocument(content);
            using (var index = LibraryIndex.OpenReadOnly(dbPath))
            {
                if (index is null) { Line("Read-only open of the new index failed"); return (false, report.ToString()); }
                var hits = index.Search(LibraryQuery.Parse(term));
                Line($"Search \"{term}\": {hits.Count} hit(s)" + (hits.Count > 0 ? $", first on page {hits[0].Page + 1} ({hits[0].Source})" : ""));
                if (hits.Count == 0) return (false, report.ToString());
            }
            Line("OK");
            return (true, report.ToString());
        }
        catch (Exception ex)
        {
            Line($"FAILED: {ex}");
            return (false, report.ToString());
        }
    }

    // Where a native library was loaded from: shows the app's own copy is the one in use. Exact file names:
    // on Windows the module list also holds managed assemblies (SQLitePCLRaw.provider.e_sqlite3.dll).
    static string? LoadedModule(params string[] names)
    {
        try
        {
            foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
                if (names.Any(n => string.Equals(m.ModuleName, n, StringComparison.OrdinalIgnoreCase))) return m.FileName;
        }
        catch (Exception) { }
        return null;
    }
}
