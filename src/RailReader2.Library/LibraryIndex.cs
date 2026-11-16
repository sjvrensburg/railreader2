using System.Globalization;
using Microsoft.Data.Sqlite;

namespace RailReader2.Library;

/// <summary>The library search index (docs/library-search-design.md §6): <c>ConfigDir/library/library.db</c>,
/// SQLite + FTS5 in WAL mode. The app is the only writer; the CLI opens it read-only.
/// <para>Not thread-safe: one instance per thread. The app keeps its writable instance on one worker
/// (<c>LibraryService</c>-style queue); readers open their own.</para>
/// <para>It is only an index of things stored elsewhere, so a database that can't be read (corrupt,
/// newer schema) is moved aside to <c>library.db.unreadable</c> and rebuilt rather than repaired.</para></summary>
public sealed class LibraryIndex : IDisposable
{
    /// <summary>Bumped when extraction or normalisation changes, so documents are re-extracted.</summary>
    public const int IndexerVersion = 1;
    /// <summary>Bumped when the table layout changes. A newer schema than this is "unreadable".</summary>
    public const int SchemaVersion = 1;

    const string Tokenizer = "tokenize = 'porter unicode61 remove_diacritics 2'";

    // How much a match counts by where it was found. bm25 is per table, so these only nudge the merge;
    // tuned with `library bench` against tests/library/queries.json.
    const double PageWeight = 1.0, OutlineWeight = 1.6, NoteWeight = 1.4, NameWeight = 2.0;
    // A paper's title, authors and abstract are on its first page; a citing paper's reference list
    // mentions the same names more densely. 1.5 took the author queries in the bench set from MRR 0.929 to 0.938; 3.0 started to cost section-title queries.
    const double FirstPageWeight = 1.5;

    readonly SqliteConnection _conn;
    public string Path { get; }
    public bool ReadOnly { get; }

    LibraryIndex(SqliteConnection conn, string path, bool readOnly)
    {
        _conn = conn; Path = path; ReadOnly = readOnly;
    }

    /// <summary>The app's database: <c>ConfigDir/library/library.db</c>.</summary>
    public static string DefaultPath =>
        System.IO.Path.Combine(RailReader.Core.Services.AppConfig.ConfigDir, "library", "library.db");

    /// <summary>Opens (creating or migrating) a writable index. An unreadable database is moved to
    /// <c>.unreadable</c> and replaced with an empty one; <paramref name="recovered"/> says so.</summary>
    public static LibraryIndex OpenWritable(string path, out bool recovered)
    {
        SqliteProbe.EnsureInitialised();
        recovered = false;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        try
        {
            return OpenAndMigrate(path);
        }
        catch (Exception ex) when (ex is SqliteException or UnreadableIndexException)
        {
            recovered = true;
            string aside = path + ".unreadable";
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                if (!File.Exists(path + suffix)) continue;
                if (suffix == "") File.Move(path, aside, overwrite: true);
                else File.Delete(path + suffix);
            }
            return OpenAndMigrate(path);
        }
    }

    /// <summary>Opens an existing index read-only, or returns null when there is none (or it can't be
    /// read). Never creates, migrates or moves anything.</summary>
    public static LibraryIndex? OpenReadOnly(string path)
    {
        SqliteProbe.EnsureInitialised();
        if (!File.Exists(path)) return null;
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        try
        {
            conn.Open();
            if (ReadSchemaVersion(conn) != SchemaVersion) { conn.Dispose(); return null; }
            return new LibraryIndex(conn, path, readOnly: true);
        }
        catch (SqliteException)
        {
            conn.Dispose();
            return null;
        }
    }

    static LibraryIndex OpenAndMigrate(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false,
        }.ToString());
        try
        {
            conn.Open();
            Exec(conn, "pragma journal_mode = wal");
            Exec(conn, "pragma synchronous = normal");
            int version = ReadSchemaVersion(conn);
            if (version > SchemaVersion)
                throw new UnreadableIndexException($"schema {version} is newer than {SchemaVersion}");
            if (version == 0) CreateSchema(conn);
            // Quick structural check: a damaged file usually fails here rather than mid-search.
            if (Scalar(conn, "pragma quick_check") is not "ok")
                throw new UnreadableIndexException("quick_check failed");
            return new LibraryIndex(conn, path, readOnly: false);
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    sealed class UnreadableIndexException(string message) : Exception(message);

    static int ReadSchemaVersion(SqliteConnection conn)
    {
        if (Scalar(conn, "select count(*) from sqlite_schema where type = 'table' and name = 'meta'") is not "1")
            return 0;
        return int.TryParse(Scalar(conn, "select value from meta where key = 'schema_version'"), out int v) ? v : 0;
    }

    static void CreateSchema(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        foreach (var sql in new[]
        {
            "create table meta (key text primary key, value text)",
            """
            create table documents (
              doc_key         integer primary key,
              content_id      text not null unique,
              last_path       text,
              file_name       text not null,
              page_count      integer not null default 0,
              file_size       integer not null default 0,
              file_mtime      integer not null default 0,
              indexer_version integer not null default 0,
              encrypted       integer not null default 0,
              state           integer not null default 0,
              indexed_utc     text,
              last_opened_utc text,
              last_page       integer)
            """,
            // Integer doc_key, not the 64-character content id, in every FTS row (Phase 0: per-row ids
            // were a visible share of the database).
            $"create virtual table page_fts using fts5(body, doc_key unindexed, page unindexed, from_ocr unindexed, {Tokenizer})",
            $"create virtual table outline_fts using fts5(title, doc_key unindexed, page unindexed, {Tokenizer})",
            $"""
             create virtual table note_fts using fts5(
               marked_text, comment, author, category,
               doc_key unindexed, page unindexed, kind unindexed, color unindexed, native_id unindexed, source unindexed,
               {Tokenizer})
             """,
            $"create virtual table name_fts using fts5(name, doc_key unindexed, {Tokenizer})",
            $"insert into meta(key, value) values ('schema_version', '{SchemaVersion}')",
        })
            Exec(conn, sql, tx);
        tx.Commit();
    }

    // ---------------------------------------------------------------- reading

    public LibraryDocument? GetDocument(string contentId)
    {
        using var cmd = Command("select * from documents where content_id = $id");
        cmd.Parameters.AddWithValue("$id", contentId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadDocument(r) : null;
    }

    public IReadOnlyList<LibraryDocument> GetDocuments()
    {
        var list = new List<LibraryDocument>();
        using var cmd = Command("select * from documents order by coalesce(last_opened_utc, indexed_utc) desc");
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadDocument(r));
        return list;
    }

    static LibraryDocument ReadDocument(SqliteDataReader r) => new(
        r.GetInt64(r.GetOrdinal("doc_key")),
        r.GetString(r.GetOrdinal("content_id")),
        Str(r, "last_path"),
        r.GetString(r.GetOrdinal("file_name")),
        r.GetInt32(r.GetOrdinal("page_count")),
        r.GetInt64(r.GetOrdinal("file_size")),
        r.GetInt64(r.GetOrdinal("file_mtime")),
        r.GetInt32(r.GetOrdinal("indexer_version")),
        r.GetInt32(r.GetOrdinal("encrypted")) != 0,
        (DocumentState)r.GetInt32(r.GetOrdinal("state")),
        Date(Str(r, "indexed_utc")),
        Date(Str(r, "last_opened_utc")),
        r.IsDBNull(r.GetOrdinal("last_page")) ? null : r.GetInt32(r.GetOrdinal("last_page")));

    /// <summary>True when <paramref name="contentId"/> has no current index entry for the file as it is
    /// now at <paramref name="path"/>: never indexed, indexed by an older indexer, or the file's size
    /// or modification time changed (an annotation write-back, say).</summary>
    public bool NeedsIndex(string contentId, string path)
    {
        var doc = GetDocument(contentId);
        if (doc is null || doc.IndexerVersion < IndexerVersion || doc.State == DocumentState.Failed) return true;
        try
        {
            var fi = new FileInfo(path);
            return !fi.Exists || fi.Length != doc.FileSize || fi.LastWriteTimeUtc.Ticks != doc.FileMtimeTicks;
        }
        catch (IOException) { return true; }
    }

    public LibraryStatus GetStatus()
    {
        long Count(string sql) => long.TryParse(Scalar(_conn, sql), out long n) ? n : 0;
        long bytes = 0;
        foreach (var suffix in new[] { "", "-wal" })
            if (File.Exists(Path + suffix)) bytes += new FileInfo(Path + suffix).Length;
        return new LibraryStatus(
            (int)Count("select count(*) from documents"),
            (int)Count($"select count(*) from documents where state = {(int)DocumentState.Locked}"),
            (int)Count($"select count(*) from documents where state = {(int)DocumentState.Failed}"),
            Count("select count(*) from page_fts"),
            Count("select count(*) from note_fts"),
            bytes);
    }

    /// <summary>Indexed notes per document (content id → count); documents without notes are absent.</summary>
    public IReadOnlyDictionary<string, int> NoteCounts()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        using var cmd = Command("""
            select d.content_id, count(*) from note_fts f join documents d on d.doc_key = f.doc_key group by d.content_id
            """);
        using var r = cmd.ExecuteReader();
        while (r.Read()) counts[r.GetString(0)] = r.GetInt32(1);
        return counts;
    }

    /// <summary>The colour-category names in use on indexed notes, sorted.</summary>
    public IReadOnlyList<string> Categories()
    {
        var list = new List<string>();
        using var cmd = Command("select distinct category from note_fts where category <> '' order by category collate nocase");
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>False after <see cref="Clear"/>: the backfill (§5.3) then leaves earlier documents out, so
    /// only documents opened from then on come back. <see cref="Clear"/>(backfill: true) turns it back on.</summary>
    public bool BackfillEnabled => GetMeta("backfill") != "off";

    string? GetMeta(string key)
    {
        using var cmd = Command("select value from meta where key = $k");
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar()?.ToString();
    }

    void SetMeta(string key, string value, SqliteTransaction? tx = null)
    {
        using var cmd = Command("insert into meta(key, value) values ($k, $v) on conflict(key) do update set value = excluded.value", tx);
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------- searching

    /// <summary>Searches page text, outlines, notes and file names. Hits are merged across sources by a
    /// weighted bm25 (higher <see cref="LibraryHit.Score"/> is better); at most
    /// <paramref name="limit"/> are returned. An empty query returns nothing.</summary>
    /// <para><paramref name="sources"/> limits the search to some sources (null = all).</para>
    public IReadOnlyList<LibraryHit> Search(LibraryQuery query, int limit = 50, string open = "[", string close = "]",
        IReadOnlySet<HitSource>? sources = null)
    {
        if (query.IsEmpty || limit <= 0) return [];
        var hits = new List<LibraryHit>();
        string match = query.MatchExpression!;
        bool Want(HitSource s) => sources is null || sources.Contains(s);

        if (Want(HitSource.Page)) Collect(hits, $"""
            select d.content_id, d.last_path, d.file_name, f.page, snippet(page_fts, 0, $o, $c, '…', 16),
                   bm25(page_fts), f.from_ocr
            from page_fts f join documents d on d.doc_key = f.doc_key
            where page_fts match $q order by bm25(page_fts) limit $n
            """, match, limit, open, close,
            r => new LibraryHit(r.GetString(0), Str(r, 1), r.GetString(2), r.GetInt32(3), HitSource.Page,
                r.GetString(4), -r.GetDouble(5) * PageWeight * (r.GetInt32(3) == 0 ? FirstPageWeight : 1.0), FromOcr: r.GetInt32(6) != 0));

        if (Want(HitSource.Outline)) Collect(hits, $"""
            select d.content_id, d.last_path, d.file_name, f.page, highlight(outline_fts, 0, $o, $c), bm25(outline_fts)
            from outline_fts f join documents d on d.doc_key = f.doc_key
            where outline_fts match $q order by bm25(outline_fts) limit $n
            """, match, limit, open, close,
            r => new LibraryHit(r.GetString(0), Str(r, 1), r.GetString(2), r.GetInt32(3), HitSource.Outline,
                r.GetString(4), -r.GetDouble(5) * OutlineWeight));

        // Comments count double against the text under a highlight; author/category are labels.
        if (Want(HitSource.Note)) Collect(hits, $"""
            select d.content_id, d.last_path, d.file_name, f.page, snippet(note_fts, -1, $o, $c, '…', 16),
                   bm25(note_fts, 1.0, 2.0, 0.5, 1.0), f.kind, f.author, f.source, f.native_id, f.category
            from note_fts f join documents d on d.doc_key = f.doc_key
            where note_fts match $q order by bm25(note_fts, 1.0, 2.0, 0.5, 1.0) limit $n
            """, match, limit, open, close,
            r => new LibraryHit(r.GetString(0), Str(r, 1), r.GetString(2), r.GetInt32(3), HitSource.Note,
                r.GetString(4), -r.GetDouble(5) * NoteWeight, NoteKind: Str(r, 6), Author: Str(r, 7),
                NoteOrigin: (NoteSource)r.GetInt32(8), NativeId: Str(r, 9), Category: Str(r, 10)));

        if (Want(HitSource.Name)) Collect(hits, $"""
            select d.content_id, d.last_path, d.file_name, highlight(name_fts, 0, $o, $c), bm25(name_fts)
            from name_fts f join documents d on d.doc_key = f.doc_key
            where name_fts match $q order by bm25(name_fts) limit $n
            """, match, limit, open, close,
            r => new LibraryHit(r.GetString(0), Str(r, 1), r.GetString(2), null, HitSource.Name,
                r.GetString(3), -r.GetDouble(4) * NameWeight));

        hits.Sort((a, b) => b.Score.CompareTo(a.Score));
        return hits.Count > limit ? hits.GetRange(0, limit) : hits;
    }

    void Collect(List<LibraryHit> into, string sql, string match, int limit, string open, string close,
        Func<SqliteDataReader, LibraryHit> read)
    {
        using var cmd = Command(sql);
        cmd.Parameters.AddWithValue("$q", match);
        cmd.Parameters.AddWithValue("$n", limit);
        cmd.Parameters.AddWithValue("$o", open);
        cmd.Parameters.AddWithValue("$c", close);
        using var r = cmd.ExecuteReader();
        while (r.Read()) into.Add(read(r));
    }

    // ---------------------------------------------------------------- writing

    /// <summary>Replaces everything indexed for <see cref="DocumentContent.ContentId"/> (pages, outline,
    /// notes, name) in one transaction, keeping its open history.</summary>
    public void ReplaceDocument(DocumentContent doc, Func<string?, string?>? categoryOf = null)
    {
        EnsureWritable();
        using var tx = _conn.BeginTransaction();
        long key = UpsertDocument(doc, tx);
        DeleteRows(key, tx, "page_fts", "outline_fts", "note_fts", "name_fts");

        using (var cmd = Command("insert into page_fts(body, doc_key, page, from_ocr) values ($b, $k, $p, $o)", tx))
        {
            var b = cmd.Parameters.Add("$b", SqliteType.Text);
            cmd.Parameters.AddWithValue("$k", key);
            var p = cmd.Parameters.Add("$p", SqliteType.Integer);
            var o = cmd.Parameters.Add("$o", SqliteType.Integer);
            foreach (var page in doc.Pages)
            {
                if (page.Body.Length == 0) continue;
                b.Value = page.Body; p.Value = page.Page; o.Value = page.FromOcr ? 1 : 0;
                cmd.ExecuteNonQuery();
            }
        }
        using (var cmd = Command("insert into outline_fts(title, doc_key, page) values ($t, $k, $p)", tx))
        {
            var t = cmd.Parameters.Add("$t", SqliteType.Text);
            cmd.Parameters.AddWithValue("$k", key);
            var p = cmd.Parameters.Add("$p", SqliteType.Integer);
            foreach (var item in doc.Outline)
            {
                t.Value = item.Title; p.Value = item.Page;
                cmd.ExecuteNonQuery();
            }
        }
        InsertNotes(key, doc.Notes, categoryOf, tx);
        using (var cmd = Command("insert into name_fts(name, doc_key) values ($n, $k)", tx))
        {
            cmd.Parameters.AddWithValue("$n", NameTerms(doc.Path));
            cmd.Parameters.AddWithValue("$k", key);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Replaces a document's annotation rows only (after the reader edited annotations).
    /// No-op when the document isn't indexed.</summary>
    public void ReplaceNotes(string contentId, IReadOnlyList<NoteEntry> notes, Func<string?, string?>? categoryOf = null)
    {
        EnsureWritable();
        if (KeyOf(contentId) is not { } key) return;
        using var tx = _conn.BeginTransaction();
        DeleteRows(key, tx, "note_fts");
        InsertNotes(key, notes, categoryOf, tx);
        tx.Commit();
    }

    /// <summary>Fills in text that only OCR produced (design Q6), for pages that have no indexed text.
    /// Never replaces a page's native text.</summary>
    public int AddOcrPages(string contentId, IReadOnlyList<PageEntry> pages)
    {
        EnsureWritable();
        if (KeyOf(contentId) is not { } key) return 0;
        using var tx = _conn.BeginTransaction();
        var existing = new HashSet<int>();
        using (var cmd = Command("select page from page_fts where doc_key = $k", tx))
        {
            cmd.Parameters.AddWithValue("$k", key);
            using var r = cmd.ExecuteReader();
            while (r.Read()) existing.Add(r.GetInt32(0));
        }
        int added = 0;
        using (var cmd = Command("insert into page_fts(body, doc_key, page, from_ocr) values ($b, $k, $p, 1)", tx))
        {
            var b = cmd.Parameters.Add("$b", SqliteType.Text);
            cmd.Parameters.AddWithValue("$k", key);
            var p = cmd.Parameters.Add("$p", SqliteType.Integer);
            foreach (var page in pages)
            {
                if (page.Body.Length == 0 || !existing.Add(page.Page)) continue;
                b.Value = page.Body; p.Value = page.Page;
                cmd.ExecuteNonQuery();
                added++;
            }
        }
        tx.Commit();
        return added;
    }

    /// <summary>Relabels every note of <paramref name="color"/> with a colour-category name (null clears).</summary>
    public void SetCategory(string color, string? name)
    {
        EnsureWritable();
        using var cmd = Command("update note_fts set category = $n where color = $c collate nocase");
        cmd.Parameters.AddWithValue("$n", (object?)name ?? "");
        cmd.Parameters.AddWithValue("$c", color);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Records an open: the path it was opened from, and the page the reader was on.</summary>
    public void RecordOpen(string contentId, string path, int? lastPage, DateTime utcNow)
    {
        EnsureWritable();
        using var cmd = Command("""
            update documents set last_path = $p, file_name = $f, last_opened_utc = $t, last_page = coalesce($g, last_page)
            where content_id = $id
            """);
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$f", System.IO.Path.GetFileName(path));
        cmd.Parameters.AddWithValue("$t", utcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$g", (object?)lastPage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", contentId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Drops a document's index rows. Never touches its annotations, portals or identity.</summary>
    public bool Remove(string contentId)
    {
        EnsureWritable();
        if (KeyOf(contentId) is not { } key) return false;
        using var tx = _conn.BeginTransaction();
        DeleteRows(key, tx, "page_fts", "outline_fts", "note_fts", "name_fts");
        using (var cmd = Command("delete from documents where doc_key = $k", tx))
        {
            cmd.Parameters.AddWithValue("$k", key);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return true;
    }

    /// <summary>Points a document at a new file (the reader located a moved file).</summary>
    public void SetPath(string contentId, string path)
    {
        EnsureWritable();
        using var cmd = Command("update documents set last_path = $p, file_name = $f where content_id = $id");
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$f", System.IO.Path.GetFileName(path));
        cmd.Parameters.AddWithValue("$id", contentId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Empties the index. With <paramref name="backfill"/> false, earlier documents are not
    /// indexed again by the backfill (see <see cref="BackfillEnabled"/>); with true, they are.</summary>
    public void Clear(bool backfill)
    {
        EnsureWritable();
        using (var tx = _conn.BeginTransaction())
        {
            foreach (var t in new[] { "page_fts", "outline_fts", "note_fts", "name_fts", "documents" })
                Exec(_conn, $"delete from {t}", tx);
            SetMeta("backfill", backfill ? "on" : "off", tx);
            tx.Commit();
        }
        Exec(_conn, "pragma wal_checkpoint(truncate)");
        Exec(_conn, "vacuum");
    }

    /// <summary>Merges FTS5 segments and compacts the file (after a bulk build).</summary>
    public void Optimize()
    {
        EnsureWritable();
        foreach (var t in new[] { "page_fts", "outline_fts", "note_fts", "name_fts" })
            Exec(_conn, $"insert into {t}({t}) values ('optimize')");
        Exec(_conn, "pragma wal_checkpoint(truncate)");
        Exec(_conn, "vacuum");
    }

    long UpsertDocument(DocumentContent doc, SqliteTransaction tx)
    {
        using var cmd = Command("""
            insert into documents(content_id, last_path, file_name, page_count, file_size, file_mtime,
                                  indexer_version, encrypted, state, indexed_utc)
            values ($id, $p, $f, $pc, $s, $m, $v, $e, $st, $t)
            on conflict(content_id) do update set
              last_path = excluded.last_path, file_name = excluded.file_name, page_count = excluded.page_count,
              file_size = excluded.file_size, file_mtime = excluded.file_mtime,
              indexer_version = excluded.indexer_version, encrypted = excluded.encrypted,
              state = excluded.state, indexed_utc = excluded.indexed_utc
            returning doc_key
            """, tx);
        cmd.Parameters.AddWithValue("$id", doc.ContentId);
        cmd.Parameters.AddWithValue("$p", doc.Path);
        cmd.Parameters.AddWithValue("$f", System.IO.Path.GetFileName(doc.Path));
        cmd.Parameters.AddWithValue("$pc", doc.PageCount);
        cmd.Parameters.AddWithValue("$s", doc.FileSize);
        cmd.Parameters.AddWithValue("$m", doc.FileMtimeTicks);
        cmd.Parameters.AddWithValue("$v", IndexerVersion);
        cmd.Parameters.AddWithValue("$e", doc.Encrypted ? 1 : 0);
        cmd.Parameters.AddWithValue("$st", (int)doc.State);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    void InsertNotes(long key, IReadOnlyList<NoteEntry> notes, Func<string?, string?>? categoryOf, SqliteTransaction tx)
    {
        using var cmd = Command("""
            insert into note_fts(marked_text, comment, author, category, doc_key, page, kind, color, native_id, source)
            values ($m, $c, $a, $cat, $k, $p, $kind, $col, $nm, $src)
            """, tx);
        var m = cmd.Parameters.Add("$m", SqliteType.Text);
        var c = cmd.Parameters.Add("$c", SqliteType.Text);
        var a = cmd.Parameters.Add("$a", SqliteType.Text);
        var cat = cmd.Parameters.Add("$cat", SqliteType.Text);
        cmd.Parameters.AddWithValue("$k", key);
        var p = cmd.Parameters.Add("$p", SqliteType.Integer);
        var kind = cmd.Parameters.Add("$kind", SqliteType.Text);
        var col = cmd.Parameters.Add("$col", SqliteType.Text);
        var nm = cmd.Parameters.Add("$nm", SqliteType.Text);
        var src = cmd.Parameters.Add("$src", SqliteType.Integer);
        foreach (var n in notes)
        {
            if (string.IsNullOrWhiteSpace(n.MarkedText) && string.IsNullOrWhiteSpace(n.Comment)) continue;
            m.Value = n.MarkedText ?? ""; c.Value = n.Comment ?? ""; a.Value = n.Author ?? "";
            cat.Value = categoryOf?.Invoke(n.Color) ?? "";
            p.Value = n.Page; kind.Value = n.Kind; col.Value = (object?)n.Color ?? "";
            nm.Value = (object?)n.NativeId ?? ""; src.Value = (int)n.Source;
            cmd.ExecuteNonQuery();
        }
    }

    void DeleteRows(long key, SqliteTransaction tx, params string[] tables)
    {
        foreach (var t in tables)
        {
            using var cmd = Command($"delete from {t} where doc_key = $k", tx);
            cmd.Parameters.AddWithValue("$k", key);
            cmd.ExecuteNonQuery();
        }
    }

    long? KeyOf(string contentId)
    {
        using var cmd = Command("select doc_key from documents where content_id = $id");
        cmd.Parameters.AddWithValue("$id", contentId);
        return cmd.ExecuteScalar() is long k ? k : null;
    }

    /// <summary>"Breusch-SimpleTestHeteroscedasticity-1979.pdf" → words a search can match.</summary>
    internal static string NameTerms(string path)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        var sb = new System.Text.StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char ch = name[i];
            if (ch is '_' or '-' or '.') { sb.Append(' '); continue; }
            // Split camel case: "SimpleTest" → "Simple Test".
            if (i > 0 && char.IsUpper(ch) && char.IsLower(name[i - 1])) sb.Append(' ');
            sb.Append(ch);
        }
        return sb.ToString();
    }

    void EnsureWritable()
    {
        if (ReadOnly) throw new InvalidOperationException("The library index was opened read-only.");
    }

    SqliteCommand Command(string sql, SqliteTransaction? tx = null)
    {
        var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd;
    }

    static void Exec(SqliteConnection c, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }

    static string? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString();
    }

    static string? Str(SqliteDataReader r, string col) => Str(r, r.GetOrdinal(col));
    static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i) is { Length: > 0 } s ? s : null;
    static DateTime? Date(string? s) =>
        s is not null && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : null;

    public void Dispose() => _conn.Dispose();
}
