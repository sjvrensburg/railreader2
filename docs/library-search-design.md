# Library search and recents dashboard (design)

**Status:** Design agreed 2026-11-16 (open questions decided, §12). Phase 0 spike done (§11a); Phase 1 index + CLI done (§11b); Phase 2 Library window done (§11c); Phase 3 recents dashboard done (§11d); Phase 4 docs done (user guide §Library Search in `user-guide.md` and `guide.html`, privacy page, README, website feature card, about-page credit for SQLite).
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
- **glibc floor (decided 2026-11-16: accept 2.34).** SQLitePCLRaw 2.1.12+ ships a `libe_sqlite3.so` built against **glibc 2.34** (`dlopen`/`pthread_*` symbols). Everything RailReader2 ships today needs at most 2.28 (ONNX Runtime; .NET itself needs 2.27). Using it unchanged drops Debian 11, Ubuntu 20.04 and RHEL/Alma/Rocky 8 (all past standard support except EL8, supported to 2029). 2.1.11's native library needs 2.28 but carries SQLite 3.49.1. **Decision:** take the current package and document glibc 2.34 as the Linux floor (Ubuntu 22.04+, Debian 12+, Fedora 35+, EL9+), rather than pin an older SQLite or build our own. The floor only bites where SQLite is loaded (the CLI's `library` commands now, the app from Phase 2), so the README's Linux requirements change with Phase 2.

## 11b. Phase 1 results (2026-11-16)

Built: `LibraryIndex` (schema §6.1 with an integer `doc_key` in every FTS row, plus a `name_fts` table for file names; read-only open; unreadable or newer-schema databases moved aside and rebuilt), `LibraryIndexer` (PDF → pages, outline, notes), `LibraryQuery` (reader input → quoted FTS5 terms: words, `"phrases"`, `prefix*`, `-excluded`; FTS5 operators are literal words, so no input can raise a syntax error), `LibraryWriter` (the app's single writer thread), CLI `library search|status|bench`, and 50 tests in `tests/RailReader.Export.Tests/Library/`.

**Query set** `tests/library/queries.json`: 48 known-item queries (phrases, word bags, rare terms, authors, stemmed forms, section titles, the reader's notes) and 6 absent terms, over the published papers in `experiments/PDFs/`. Each query was taken from the labelled page by reading it; the absent terms were checked to occur nowhere in the corpus. The corpus's private documents (invoices, letters) are left out.

| | |
|---|---|
| recall@10 | 97.9 % (47/48) |
| MRR | 0.938 |
| empty-result precision (absent terms) | 100 % (6/6) |
| query latency p95, 861 pages | 1.5 ms |
| search at 21,000 pages / 700 documents (corpus cloned 25×, 100 MB) | 10–60 ms per CLI call, process start included |
| indexing incl. annotations | ~125 pages/s (single thread) |

- **Ranking:** bm25 per table, merged with weights (outline 1.6, notes 1.4, file name 2.0) and a 1.5× first-page weight, which moved the "Vaswani" author query from rank 9 (citing papers' reference lists are denser in the name) to rank 1, MRR 0.929 → 0.938. 3.0 started costing section-title queries.
- **The one miss** is a Porter stemmer limit: "diagnosing" (`diagnos`) does not match "diagnosis" (`diagnosi`). Kept in the set as a known miss.
- **Normalisation fix found while labelling:** PDFium reports a line-end hyphen as U+0002 with no line break, so "Trans\u0002formers" had been indexed as two words. Now joined (kept as a hyphen before a capital). Known gap: a glyph with no Unicode mapping (a broken "fi" ligature) arrives as a control character and splits its word.

**Deviations from §5:**
- **Notes are re-indexed when a document's last tab closes and at shutdown**, not on each auto-save: Core has no "annotations saved" event (`AnnotationFileManager` saves on its own timer). Notes are also refreshed on every open. A crash loses only the index's copy of that session's note edits, which the next open restores.
- **Backfill enumerates documents** from `DocumentIdentity`'s map file (read tolerantly: the path keys of its `files` object; a format change finds nothing rather than failing — Phase 1 first read the wrong shape, and only the live-app test caught it, since the unit test had been written to the same assumption) plus the recent-files list. Core has no public "list documents" call; worth adding upstream.
- **No PDF metadata (Title/Author)** yet: the PDF service doesn't expose it and the shell must not call PDFium directly. File names are indexed instead. Another candidate for Core.
- **Live-app check (2026-11-16):** opening a PDF indexed it within ~2 s, the CLI read the index while the app had it open (WAL), a reviewer comment came back as "reviewer comment (Reviewer X)", closing the window shut down in ~1 s, and the backfill indexed all 15 surviving identity-map documents (1,024 pages, incl. a 615-page book) in ~30 s, skipping 10 missing files quietly. The rail-reading pause was not exercised live (covered by `LibraryWriterTests`).
- **Opening a document extracts it on the writer thread** even while rail reading (only the backfill pauses). PDFium calls are per page, so a render waits at most one page's extraction (~5 ms typical, ~70 ms worst seen).
- Library tests live in the existing test project, beside the other shell tests, not a new one.

## 11c. Phase 2 results (2026-11-16)

Built: `Views/LibraryWindow` (non-modal, single instance; Ctrl+Shift+F, File ▸ Search Library…, and "Search all documents ↗" in the Search pane, which carries the query over), Settings ▸ Library (advanced view: counts and size, Rebuild, Clear), and on the library side `LibraryResults` (grouping and labels), source filters, note counts, categories, `Clear`, `SetPath`, and writer jobs `Remove`/`Relocate`/`Clear`/`Rebuild` with a `Changed` event and backfill progress. README states the Linux floor.

- **Results:** grouped by document, best first, at most three snippets each preferring different pages, matched words in bold, labelled *page N*, *outline*, *your note*, *reviewer comment (author)*, *bookmark*, *OCR text, unverified* (italic), plus the colour category. Filters: page text / notes / outline / file names, "only documents with notes", colour category (shown when one is in use; narrows to notes). A flat list (document rows and snippet rows), so arrow keys walk everything; Enter or a click opens; rows carry automation names.
- **Queries run off the UI thread** with their own read-only connection, 200 ms after typing stops (a `Task.Delay`, not a `DispatcherTimer`). A writer change (`Changed`, coalesced and posted at background priority) re-runs the query; the list is only replaced when the rows differ, so the reader keeps their place.
- **Opening a hit:** an open tab of the document (matched by content id) is focused, else the file is opened. A note hit selects the annotation by its `/NM` (as the Comments pane does). Other hits go to the page and run the Search pane with a regex of the query's words (`LibraryQuery.HighlightPattern`: `\b(word|stem\w*|phrase[\s\-]+words)`; a crude suffix strip stands in for the index's stemmer), then show the first match **on the hit's page**, not the document's first match. A file-name hit opens at the saved position.
- **Missing files** (design Q3): greyed, "Not found at …", with Locate… and Remove from Library. Locate hashes the picked file with `DocumentIdentity.Lookup` (no write) and accepts it only if it is the same content id; only then is it `Resolve`d (recorded in the identity map) and the entry re-pointed and re-indexed.
- **Remove sticks** (code review): a removed document is recorded in a `removed` table and the backfill skips it, so it doesn't return just because its file is still known; opening or locating it clears the mark. Clear and Rebuild forget the marks.
- **The Search pane's toggles are the reader's:** a hit turns Regex on (and Case off) for its highlight pattern; the pane restores the reader's own settings the next time they type, press Ctrl+F or clear the search.
- **Clear vs Rebuild:** Clear empties the index and sets `meta.backfill = off`, so earlier documents stay out until opened again (otherwise the next start's backfill would refill everything, and Clear would mean nothing). Rebuild empties it and re-queues every known document.
- **Deviations:** no raw FTS5 pass-through (§7.1 said "only when the query contains it"); Phase 1's rule that no input reaches `MATCH` unquoted stands, and "couldn't read that query" never arises. The window is **not owned** by the main window: an owned window always stays above its owner, so it hid the document a result opened (found in the live test); it is closed in `ShutdownLibrary` instead. Library progress shows in the window's status line and in Settings, nowhere else (§5.3).
- **Live-app check (2026-11-16):** with no document open, a reviewer comment in a moved file was found and shown greyed; Locate refused a different PDF ("its contents differ") and accepted the moved one, re-pointing the entry; Enter on the note hit opened the file and selected the highlight with its comment popup; "kernel ridge" opened a 615-page book at page 243 with both words highlighted on the page (match 1008 of 2212 active). Found and fixed: Enter was swallowed by the list (now a tunnelling handler), and the owned window covered the document.
- **Not done:** the Search pane's own result list still scrolls to page 1 when a hit's match is activated (it doesn't follow `GoToMatch` from code); searching a 615-page book takes ~30 s in that pane, as it always has.

## 11d. Phase 3 results (2026-11-16)

Built: `Views/RecentDocumentsView` in the welcome panel (between the Open button and the shortcut hints; the panel now scrolls), `RecentDocuments` (cards from the recent-files list, de-duplicated by content id, newest entry winning, joined to `documents` and the note counts), thumbnails in `ConfigDir/library/thumbs/<content-id>.png`.

- **Cards:** up to 12, newest first: thumbnail, file name (no PDF metadata is indexed yet), a thin progress bar and "page 41 of 230" (or "page 41" before the document is indexed), note count, greyed "File not found" for a missing file, "Password protected" (no thumbnail) for a locked one. Click opens at the saved position (`OpenDocument`, which restores it); a missing file points the reader to the Library window's Locate. Above them a search box that opens the Library window with the query.
- **Hidden until there is history**, so a first launch and the AppImage catalogue screenshot are unchanged. Loads only while the welcome screen shows: on load, when the last tab closes (with this session's positions), and when the index changes (backfill bringing page/note counts), at most every ~2 s.
- **Thumbnails:** first page at ~240 px wide, rendered on the thread pool one at a time through Core's `RenderPage` (PDFium gate), PNG written via temp file + rename, re-rendered when older than the PDF (an annotation write-back). Never for an encrypted PDF (it would put a page of it on disk), and not at all while indexing is off (`RR_DISABLE_LIBRARY_INDEX`). They are part of the index: Remove deletes the document's, Clear and Rebuild delete the folder. Decoded bitmaps are reused across reloads so cards don't flicker.
- **Live-app check (2026-11-16):** with an empty index, the dashboard filled in thumbnails, page counts ("page 23 of 23 · 20 notes") and a greyed missing file as the backfill ran; a card opened its document at the saved page; paging on and closing the tab brought the dashboard back with that document first at its new page ("page 3 of 15").
- **Not done:** title/author from PDF metadata (Core exposes none; file names stand in); no "remove from recents" on a card (the File ▸ Recent menu has none either).

## 12. Decisions

| # | Question | Decision (2026-11-16) |
|---|---|---|
| Q1 | Library = opened documents only, or also watched folders? | Opened only for v1. Folders later if asked for. |
| Q2 | Index reviewers' in-PDF comments, or only the reader's own notes? | Both, labelled ("your note" / "reviewer comment") and filterable. |
| Q3 | Missing files: keep greyed, or prune automatically? | Keep greyed with "Locate…" and "Remove from library". No automatic pruning. A file on an unplugged drive is not gone. |
| Q4 | Encrypted PDFs: never index text, or index after an opt-in once opened with the password? | Never in v1; revisit only if a reader asks. |
| Q5 | Library window, or a scope toggle inside the accordion's Search section? | Separate window (works with no document open), with a link from the accordion. |
| Q6 | Index OCR text that exists only in the live session (not baked into the file)? | Yes, flagged unverified. It is lost otherwise once the session ends. |
