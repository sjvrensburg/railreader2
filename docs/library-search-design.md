# Library search and recents dashboard (design)

**Status:** Design agreed 2026-11-16 (open questions decided, §12). Phase 0 spike done (§11a); one packaging decision open (glibc floor).
**Roadmap item:** M7 (features adapted from the Cauchy viewer): library-wide full-text search and a recents dashboard keyed by content id.
**Depends on:** content-based document identity (`DocumentIdentity`, Core 0.75.0+), sidecar annotations keyed by content id (Core 0.72.0+).

---

## 1. Motivation

A reader with a few hundred papers on disk remembers *what* a paper said long after forgetting *which* paper said it or where the file lives. Today RailReader2 searches one open document at a time (`SearchService`, the accordion's Search section). There is no way to ask "which of my papers mention the Marchenko–Pastur law?" or "where did I write that note about the bootstrap band?".

M7 adds:

1. **Library search**: one query across every document the reader has opened, covering page text, outlines, and the reader's own annotations and comments. Results open at the right page with the hit highlighted.
2. **A recents dashboard**: the welcome panel becomes a list of recent documents (title, first-page thumbnail, reading progress, note count), keyed by content id so a moved or renamed file is still the same entry.

### Non-goals (v1)

- **Semantic or "concept" search** (embeddings). See §9: the schema leaves room for it as a later opt-in download, but v1 is lexical.
- Indexing PDFs the reader has never opened (no folder crawling). See §3 and Q1.
- Running OCR just to index a scanned document.
- Question answering over the library (that is M5's territory).

## 2. Why lexical (FTS5) first

SQLite's FTS5 gives word, phrase, prefix and `NEAR` queries ranked by BM25, with a Porter stemmer and diacritic folding. For the queries a reader of technical papers types (author names, named results, notation words, a remembered phrase), lexical matching is exact, explainable and instant. It never returns a confident-looking false hit, which matches the project's "abstain rather than guess" rule.

An embedding model (e.g. Google's EmbeddingGemma, ~300M parameters) would add concept matching, but at a cost: hundreds of MB of weights (several times the bundled layout model), CPU inference over every page of every document, poor handling of mathematical notation, and a chunking scheme to keep in step with the text index. The same size-versus-value trade led to declining a bundled NL-search model in June 2026. It belongs behind an opt-in download, if anywhere (§9).

## 3. What "the library" is

The library is every document RailReader2 has resolved an identity for, i.e. every PDF the reader has opened (the entries of `ConfigDir/document_ids.json`), plus any future one at its first open.

- No folder crawling or watched folders in v1. They raise permission, scale and "why is this paper here?" questions, and most readers' working set is what they have opened.
- Backfill on first run: documents already in the identity map are indexed in the background from their last known path (§5.3).
- A reader can **remove a document from the library**. This drops its index rows only. It never touches the identity map, the annotation sidecar, portals or view rotation, which are all still keyed by the same content id and come back if the file is opened again.

## 4. What gets indexed

Each source is its own FTS table (or column) so a result can say *where* it matched and the ranking can weight them.

| Source | Unit | Notes |
|---|---|---|
| Page text | one row per page | From `IPdfTextService`, the same `PageText` the rest of the app uses. Normalised (§4.1). |
| OCR text | one row per page | Only when OCR has *already* produced it (live OCR or a baked `RailReaderOCR` layer). Flagged `from_ocr` so results show "OCR text, unverified", same as the Index pane. |
| Outline | one row per outline entry | Title + target page. Lets "Section 4.2 Identification" find the section, not just its mentions. |
| Annotations | one row per annotation | Highlighted text (text under the quads), the comment/`/Contents`, reply bodies, author, colour-category name. Includes in-PDF reviewer comments. |
| Document metadata | one row per document | PDF Title/Author/Subject/Keywords, file name, first-page title guess (largest `doc_title` block if analysed). |

Bookmarks (named) are indexed with annotations, kind `bookmark`.

### 4.1 Text normalisation

Applied once when a page is indexed, never to the stored PDF:

- Unicode NFKC (folds ligatures `ﬁ`→`fi`, full-width forms).
- De-hyphenate at line ends: `estima-⏎tion` → `estimation` when the joined form is a word in the document (or, simpler, when the hyphen is the last character of a line and the next line starts lower-case).
- Join lines with spaces, collapse whitespace.
- Keep digits and Greek letters. Mathematical glyphs from PDFium are kept as tokens. Searching for `Σ` works; searching for an equation does not, and is not a goal.

Tokenizer: `porter unicode61 remove_diacritics 2`. The Porter stemmer is English-only, so a non-English page still matches on exact words, just without stemming. A second, trigram-tokenized table for substring search is a possible later addition if readers miss partial-word matches.

## 5. When indexing happens

### 5.1 On open

When `OpenDocument` resolves the content id, a background job checks the `documents` row:

- no row, or `indexer_version` older than the current one → index the whole document;
- `(size, mtime)` of the file unchanged since it was indexed → nothing to do;
- changed → re-extract page text and replace the page rows. Annotation write-back changes the bytes but not the page text, so re-extraction is the simple, correct answer. It costs milliseconds per page.

Text extraction is cheap (PDFium text, no layout model). The job opens its **own** PDF handle on the thread pool. Every PDFium call goes through Core's services, which take `PdfiumGate.Lock`, so it serialises with page renders but never runs on the UI thread. It yields between pages so a 600-page book does not hold the gate for long.

### 5.2 Annotation changes

Re-index a document's annotation rows when its annotations are saved (hook the existing debounced auto-save, one write per save, not per edit). Colour-category renames re-index the category field of affected rows (cheap: an `UPDATE` by colour).

### 5.3 Backfill

At startup, after the main window opens, one background `Task` walks the identity map for documents without a current index row whose last path still exists, indexing them one at a time at low priority. Rules:

- **No `DispatcherTimer`** (X11 cost, see CLAUDE.md §Debugging). One task, `await Task.Yield()`/short delays between documents, cancelled on shutdown.
- Pauses while rail mode is active, as background analysis already does, to avoid PDFium contention during reading.
- Progress is visible in the library window ("Indexing 37 of 212") and nowhere else. No toast.

### 5.4 Encrypted PDFs

The password is never persisted, and the index would store the document's text in plaintext. The page text of encrypted PDFs is not indexed. Only metadata available without the password (file name) is indexed, and the entry is marked "locked: open to read". Indexing after the reader opens it with the password, behind a per-document opt-in, is deferred (Q4) until a reader asks for it.

## 6. Storage

- `ConfigDir/library/library.db`, SQLite in WAL mode, via `Microsoft.Data.Sqlite`. Its SQLitePCLRaw `e_sqlite3` bundle is built with FTS5. **Phase 0 must confirm** the native library is present and loads in the AppImage, the Inno install, the MSIX package and the CLI tarball (~1–2 MB per RID).
- Thumbnails: `ConfigDir/library/thumbs/<content-id>.png`, first page at ~200 px width, rendered once and cached.
- The app is the only writer. The CLI opens the database read-only (WAL lets it read while the app writes), the same split as `DocumentIdentity`.
- Settings ▸ (advanced) "Library": index size on disk, "Rebuild index", "Clear index". `CleanupService` leaves the database alone; it is user data, not cache.
- An unreadable database (corrupt, newer schema) is renamed to `library.db.unreadable` and rebuilt. Like `document_ids.json`, it is only an index of things that are stored elsewhere.

### 6.1 Schema sketch

```sql
CREATE TABLE documents (
  content_id      TEXT PRIMARY KEY,
  last_path       TEXT,
  title           TEXT,           -- PDF metadata or first-page guess
  authors         TEXT,
  page_count      INTEGER,
  file_size       INTEGER,
  file_mtime      INTEGER,
  indexer_version INTEGER,
  encrypted       INTEGER,
  last_opened_utc TEXT,
  last_page       INTEGER,        -- for the dashboard's progress
  state           TEXT            -- indexed | locked | missing | failed
);

CREATE VIRTUAL TABLE page_fts USING fts5(
  body,
  content_id UNINDEXED, page UNINDEXED, from_ocr UNINDEXED,
  tokenize = 'porter unicode61 remove_diacritics 2'
);

CREATE VIRTUAL TABLE note_fts USING fts5(
  marked_text, comment, author, category,
  content_id UNINDEXED, page UNINDEXED, annot_nm UNINDEXED, kind UNINDEXED,
  tokenize = 'porter unicode61 remove_diacritics 2'
);

CREATE VIRTUAL TABLE outline_fts USING fts5(
  title, content_id UNINDEXED, page UNINDEXED,
  tokenize = 'porter unicode61 remove_diacritics 2'
);

CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);  -- schema_version
```

Size estimate to check in Phase 0: FTS5 is typically 30–60 % of the raw text size; a 20-page paper is ~60–100 KB of text, so ~1,000 papers is on the order of 50–100 MB with thumbnails. That is acceptable but worth measuring.

## 7. User interface

### 7.1 Library search

- **Entry points:** `Ctrl+Shift+F` (currently falls through to `Ctrl+F`), File ▸ "Search Library…", and a search box on the dashboard.
- **Where it lives:** a non-modal Library window (like `DocumentWindow`), so it works with no document open and does not crowd the accordion. The accordion's Search section gains a "Search all documents ↗" link that carries the current query over.
- **Query syntax:** plain words, ANDed. `"quoted phrase"`, trailing `*` for prefix. Raw FTS5 syntax (`NEAR`, `OR`, column filters) is passed through only when the query contains it. A parse error shows "couldn't read that query", never an exception.
- **Results:** grouped by document (title, authors, path, hit count), up to three snippets each (FTS5 `snippet()` with the matched words bolded), each snippet labelled *page N*, *your note*, *reviewer comment*, *outline* or *OCR text, unverified*. Filters: source (text / notes / outline), colour category, "only documents with notes".
- **Ranking:** BM25 per table, then a fixed weight per source (outline and notes above body text, OCR text below), then recency of last open as a tie-break. Tune against the query set (§8), not by eye.
- **Opening a hit:** opens (or focuses) the document, goes to the page with `recordHistory: true`, and runs the in-document `SearchService` with the query's plain terms so matches on the page are highlighted. A note hit selects the annotation as the Comments pane does.
- **Missing files:** shown greyed with their last known path and two actions, "Locate…" (file picker; the file is accepted only if it resolves to the same content id) and "Remove from library".
- **Accessibility:** results are a list with real automation names ("Smith 2019, page 4: …snippet…"), reachable by keyboard, Enter opens.

### 7.2 Recents dashboard

The welcome panel keeps its logo, Open button and shortcut hints, and gains, above the hints when there is any history:

- a search box (library search, same as `Ctrl+Shift+F`);
- **Recent documents**: up to 12 cards, newest first. Each card has the thumbnail, the title (metadata, else file name), "page 41 of 230", the note count, and a greyed state if the file is missing. Click opens at the saved position (`RecentFileEntry`).

Recents are the existing `AppConfig.RecentFiles`, **de-duplicated by content id** (two paths to one file are one card) and joined to `documents` for title/thumbnail/progress. The File ▸ Recent menu stays as it is.

The AppImage catalogue screenshot launches with no history, so it is unchanged.

### 7.3 CLI

`library search <query> [--json] [--limit N]` reads the app's database read-only and prints hits (content id, last path, page, source, snippet). `library status` prints document/page counts and index size. The CLI never writes the index.

## 8. Measuring it

Same discipline as the references work (`tests/references/`): decide by measurement on our own corpus.

- **Known-item query set** `tests/library/queries.json`: ~50 queries over `experiments/PDFs/`, each labelled with the document(s) and page(s) that should come first. Mix of exact phrases, single rare terms, author names, stemmed forms ("estimators" for "estimation"), notes text, and a few deliberately absent terms (must return nothing).
- **Metrics:** recall@10, mean reciprocal rank, empty-result precision on the absent terms.
- **Performance:** indexing throughput (pages/s, single thread), database size per 1,000 pages, query latency p95 (target < 50 ms on 1,000 documents), and no measurable frame-time change during rail reading while backfill runs (`RR_FRAME_TIMING=1`).
- A `library bench` CLI subcommand runs the query set against a throwaway database built from a folder.

## 9. Later: semantic layer (not in v1)

If lexical search proves too literal for real queries, add concept search as an **opt-in download** (Settings ▸ Models, same pattern as the OCR packs), never bundled:

- a `chunks` table (content id, page, span, vector BLOB) filled by background indexing only when the model is installed;
- hybrid ranking by reciprocal-rank fusion of BM25 and vector similarity, so lexical hits keep their place;
- the query set in §8 gains concept queries first, so the layer is judged on whether it raises recall without hurting precision.

The v1 schema needs nothing for this beyond `indexer_version`.

## 10. Code layout

- `src/RailReader2.Library/` (new, Core-only, no Avalonia, shared by app and CLI like `RailReader2.References`): `LibraryIndex` (open/migrate/rebuild), `LibraryIndexer` (document → rows, normalisation), `LibraryQuery` (parse, search, snippet, rank), `LibraryDocument` records. Carries the `Microsoft.Data.Sqlite` dependency so it stays out of `RailReader2.Shared`.
- App: `ViewModels/MainWindowViewModel.Library.cs` (open hooks, annotation-save hook, backfill task), `Views/LibraryWindow.axaml(.cs)`, welcome-panel changes in `MainWindow.axaml`.
- CLI: `library` command.
- Tests: a new `tests/RailReader2.Library.Tests` for normalisation, query parsing, ranking order on a tiny fixture corpus, schema migration and the read-only CLI open.

## 11. Phases

0. **Spike (half a day):** add `Microsoft.Data.Sqlite`, create an FTS5 table, confirm it loads in all four packages (CI publishes); measure extraction throughput and database size on `experiments/PDFs/`. Stop if FTS5 is not available everywhere.
1. **Index + CLI:** `RailReader2.Library`, indexing on open and backfill, annotation re-index, `library search|status|bench`, the query set and first metrics. No GUI search yet. The CLI is enough to tune ranking.
2. **Library window:** search UI, opening hits with in-document highlighting, missing-file handling, Settings ▸ Library.
3. **Dashboard:** welcome-panel recents with thumbnails and progress.
4. **Docs:** user guide section, privacy page (the index is local and holds document text; how to clear it), CLAUDE.md.

## 11a. Phase 0 results (2026-11-16)

Spike: `src/RailReader2.Library` (`SqliteProbe`, `TextNormalizer`, `LibraryBench`) and the CLI's `library probe` / `library bench <folder>`. Measured on `experiments/PDFs/` (28 PDFs, 861 pages), Linux x64, single thread.

- **FTS5 is available.** `Microsoft.Data.Sqlite` 10.0.15 → SQLitePCLRaw 2.1.13 → SQLite 3.53.3. `porter unicode61 remove_diacritics 2` works: `estimation AND cafe` matches "estimators … café". `SQLitePCL.Batteries_V2.Init()` is called explicitly, so it doesn't depend on reflection surviving AOT.
- **Loads in the AOT CLI** (linux-x64 publish: probe and bench both run, no new trim/AOT warnings). The app's linux-x64 and win-x64 publishes carry the native library (`libe_sqlite3.so` / `e_sqlite3.dll`). The AppImage, Inno and both CLI archives pick it up through their existing wildcards; the MSIX holds the publish folder. Adds ~2.1 MB (Linux) / ~2.6 MB (Windows) per package.
- **Windows loading** is checked in CI: `library probe` runs in test.yml on both OSes and after both AOT CLI publishes in release.yml. The GUI packages (Inno, MSIX) are not exercised until Phase 2 first uses the index from the app.
- **Extraction throughput:** ~210–220 pages/s (4 s for the corpus, same under JIT and AOT). Outliers: MOMENT.pdf 38 ms/page, Programme_Final1.pdf 10 ms/page. One scanned PDF has no text layer (0 chars), as expected. Insert is ~10 % of extraction time.
- **Size:** 4.1 MB after `optimize` + `VACUUM` for 2.45 M characters, **4.9 KB/page**. The FTS index itself is ~1.0 MB (~42 % of text, in line with the §6.1 estimate). The rest is FTS5's stored copy of the text (needed for `snippet()`) and per-row ids. So ~1,000 papers × 20 pages ≈ 100 MB before thumbnails, about twice the §6.1 estimate. Phase 1: store an integer document key in FTS rows, not the 64-character content id.
- **Query latency:** 0.01–5.4 ms median on 861 pages (prefix `contrast*` slowest). Far inside the 50 ms target; re-measure at 20k pages in Phase 1.
- **Query syntax:** raw FTS5 syntax errors throw (`NEAR/5` is not FTS5 syntax). Phase 1's `LibraryQuery` must parse user input and quote terms itself, never pass it to `MATCH` as is.
- **Blocker to decide: glibc floor.** SQLitePCLRaw 2.1.12+ ships a `libe_sqlite3.so` built against **glibc 2.34** (`dlopen`/`pthread_*` symbols). Everything RailReader2 ships today needs at most 2.28 (ONNX Runtime; .NET itself needs 2.27). Using it unchanged drops Debian 11, Ubuntu 20.04 and RHEL/Alma/Rocky 8 (all past standard support except EL8, supported to 2029). 2.1.11's native library needs 2.28 but carries SQLite 3.49.1.

## 12. Decisions

| # | Question | Decision (2026-11-16) |
|---|---|---|
| Q1 | Library = opened documents only, or also watched folders? | Opened only for v1. Folders later if asked for. |
| Q2 | Index reviewers' in-PDF comments, or only the reader's own notes? | Both, labelled ("your note" / "reviewer comment") and filterable. |
| Q3 | Missing files: keep greyed, or prune automatically? | Keep greyed with "Locate…" and "Remove from library". No automatic pruning. A file on an unplugged drive is not gone. |
| Q4 | Encrypted PDFs: never index text, or index after an opt-in once opened with the password? | Never in v1; revisit only if a reader asks. |
| Q5 | Library window, or a scope toggle inside the accordion's Search section? | Separate window (works with no document open), with a link from the accordion. |
| Q6 | Index OCR text that exists only in the live session (not baked into the file)? | Yes, flagged unverified. It is lost otherwise once the session ends. |
