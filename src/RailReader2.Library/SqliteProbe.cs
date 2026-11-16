using Microsoft.Data.Sqlite;

namespace RailReader2.Library;

/// <summary>What the bundled SQLite can do. Phase 0 of the library search design: the index needs
/// FTS5 with the porter + unicode61 tokenizers in every shipped package.</summary>
public sealed record SqliteProbeResult(
    string SqliteVersion, bool Fts5, string? Fts5Error, string NativeLibrary, IReadOnlyList<string> CompileOptions);

public static class SqliteProbe
{
    static int _initialised;

    /// <summary>Initialise SQLitePCLRaw explicitly. Microsoft.Data.Sqlite finds the bundle by reflection,
    /// which a trimmed/AOT build (the CLI) cannot be relied on to keep.</summary>
    public static void EnsureInitialised()
    {
        if (Interlocked.Exchange(ref _initialised, 1) == 0)
            SQLitePCL.Batteries_V2.Init();
    }

    public static SqliteProbeResult Run()
    {
        EnsureInitialised();
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();

        string version = Scalar(conn, "select sqlite_version()") ?? "?";
        var options = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "pragma compile_options";
            using var r = cmd.ExecuteReader();
            while (r.Read()) options.Add(r.GetString(0));
        }

        bool fts5 = false;
        string? error = null;
        try
        {
            Exec(conn, "create virtual table t using fts5(body, tokenize = 'porter unicode61 remove_diacritics 2')");
            Exec(conn, "insert into t(body) values ('Consistent estimators of the café variance')");
            // Stemmed ("estimation" -> estim) and diacritic-folded ("cafe") matches must both hit.
            fts5 = Scalar(conn, "select count(*) from t where t match 'estimation AND cafe'") == "1";
            if (!fts5) error = "FTS5 table created but the stemmed/diacritic query did not match";
        }
        catch (SqliteException ex)
        {
            error = ex.Message;
        }

        return new SqliteProbeResult(version, fts5, error, SQLitePCL.raw.GetNativeLibraryName(), options);
    }

    static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static string? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString();
    }
}
