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
- **The one miss** is a Porter stemmer limit: "diagnosing" (`diagnos`) does not match "diagnosis" (`diagnosi`). Kept in the set as a known miss. *Fixed in §11g (word families).*
- **Normalisation fix found while labelling:** PDFium reports a line-end hyphen as U+0002 with no line break, so "Trans\u0002formers" had been indexed as two words. Now joined (kept as a hyphen before a capital). Known gap: a glyph with no Unicode mapping (a broken "fi" ligature) arrives as a control character and splits its word. *TeX's ligature codes are mapped back since §11g.*

**Deviations from §5:**
- **Notes are re-indexed when a document's last tab closes and at shutdown**, not on each auto-save: Core has no "annotations saved" event (`AnnotationFileManager` saves on its own timer). Notes are also refreshed on every open. A crash loses only the index's copy of that session's note edits, which the next open restores. *Superseded by §11f: notes are now re-indexed on every save.*
- **Backfill enumerates documents** from `DocumentIdentity`'s map file (read tolerantly: the path keys of its `files` object; a format change finds nothing rather than failing — Phase 1 first read the wrong shape, and only the live-app test caught it, since the unit test had been written to the same assumption) plus the recent-files list. Core has no public "list documents" call; worth adding upstream. *Superseded by §11f: Core's `DocumentIdentity.KnownDocuments()`.*
- **No PDF metadata (Title/Author)** yet: the PDF service doesn't expose it and the shell must not call PDFium directly. File names are indexed instead. Another candidate for Core. *Superseded by §11f.*
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
- **Back after opening a hit** (checked live): one history entry, the reader's own position. From page 100, a hit on page 425 of the same book went to 425 and Back returned to 100, not to the document's first match on page 1, so the search's intermediate jumps add no history.
- **The Search pane's toggles are the reader's:** a hit turns Regex on (and Case off) for its highlight pattern; the pane restores the reader's own settings the next time they type, press Ctrl+F or clear the search. A freshly created pane raises `TextChanged` for the hit's pattern *after* the activation has finished, so a change to exactly the text the activation set is ignored; without that, opening a hit with no document open restored the toggles at once and ran a literal search for the regex (0 matches, found in the live test).
- **Clear vs Rebuild:** Clear empties the index and sets `meta.backfill = off`, so earlier documents stay out until opened again (otherwise the next start's backfill would refill everything, and Clear would mean nothing). Rebuild empties it and re-queues every known document.
- **Deviations:** no raw FTS5 pass-through (§7.1 said "only when the query contains it"); Phase 1's rule that no input reaches `MATCH` unquoted stands, and "couldn't read that query" never arises. The window is **not owned** by the main window: an owned window always stays above its owner, so it hid the document a result opened (found in the live test); it is closed in `ShutdownLibrary` instead. Library progress shows in the window's status line and in Settings, nowhere else (§5.3).
- **Live-app check (2026-11-16):** with no document open, a reviewer comment in a moved file was found and shown greyed; Locate refused a different PDF ("its contents differ") and accepted the moved one, re-pointing the entry; Enter on the note hit opened the file and selected the highlight with its comment popup; "kernel ridge" opened a 615-page book at page 243 with both words highlighted on the page (match 1008 of 2212 active). Found and fixed: Enter was swallowed by the list (now a tunnelling handler), and the owned window covered the document.
- **Not done:** the Search pane's own result list still scrolls to page 1 when a hit's match is activated (it doesn't follow `GoToMatch` from code); searching a 615-page book takes ~30 s in that pane, as it always has.

## 11d. Phase 3 results (2026-11-16)

Built: `Views/RecentDocumentsView` in the welcome panel (between the Open button and the shortcut hints; the panel now scrolls), `RecentDocuments` (cards from the recent-files list, de-duplicated by content id, newest entry winning, joined to `documents` and the note counts), thumbnails in `ConfigDir/library/thumbs/<content-id>.png`.

- **Cards:** up to 12, newest first: thumbnail, title (PDF metadata since §11f, else the file name), a thin progress bar and "page 41 of 230" (or "page 41" before the document is indexed), note count, greyed "File not found" for a missing file, "Password protected" (no thumbnail) for a locked one. Click opens at the saved position (`OpenDocument`, which restores it); a missing file points the reader to the Library window's Locate. Above them a search box that opens the Library window with the query.
- **Hidden until there is history**, so a first launch and the AppImage catalogue screenshot are unchanged. Loads only while the welcome screen shows: on load, when the last tab closes (with this session's positions), and when the index changes (backfill bringing page/note counts), at most every ~2 s.
- **Thumbnails:** first page at ~240 px wide, rendered on the thread pool one at a time through Core's `RenderPage` (PDFium gate), PNG written via temp file + rename, re-rendered when older than the PDF (an annotation write-back). Never for an encrypted PDF (it would put a page of it on disk), and not at all while indexing is off (`RR_DISABLE_LIBRARY_INDEX`). They are part of the index: Remove deletes the document's, Clear and Rebuild delete the folder. Decoded bitmaps are reused across reloads so cards don't flicker.
- **Live-app check (2026-11-16):** with an empty index, the dashboard filled in thumbnails, page counts ("page 23 of 23 · 20 notes") and a greyed missing file as the backfill ran; a card opened its document at the saved page; paging on and closing the tab brought the dashboard back with that document first at its new page ("page 3 of 15").
- **Not done:** no "remove from recents" on a card (the File ▸ Recent menu has none either).

## 11e. Performance follow-up (2026-11-16, code review)

- **Schema 2: FTS rows are keyed by rowid ranges.** Each row's rowid is `(doc_key << 20) | n` (page number for pages, a counter for outline entries and notes), so a document's rows are one rowid range. Re-index, notes refresh, OCR fill-in and Remove delete or list by `rowid between …`, a b-tree range lookup. Schema 1 filtered on the `UNINDEXED doc_key` column, a full table scan, so each re-index cost grew with the library. An older index is dropped and rebuilt in place on open (not moved aside as unreadable); the next start's backfill refills it.
- Measured with synthetic 20-page documents (400 words a page):

| | schema 1, 1,000 docs | schema 2, 1,000 docs | schema 2, 3,000 docs |
|---|---|---|---|
| Re-index one document | 41 ms | 11 ms | 12 ms |
| Notes refresh | 0.7 ms | 0.1 ms | 0.1 ms |
| OCR fill-in | 27 ms | 0.4 ms | 1.5 ms |
| Remove | 32 ms | 4.8 ms | 4.6 ms |
| Build (all documents) | 23 s | 7.9 s | 24 s |

- **The Library window caches the index totals** (status, colour categories, note counts: 84 ms at 60,000 pages) and re-reads them only after the writer reports a change, not on every keystroke. A stale flag survives a refresh that is overtaken by typing.
- Search quality is unchanged (`library bench`: recall@10 97.9 %, MRR 0.938, p95 2.3 ms).

## 11f. Core 0.87.0 follow-up (2026-11-16)

RailReaderCore 0.87.0 added the four APIs the phases above asked for (#202–#205); the shell's workarounds are replaced.

- **Backfill enumeration:** `DocumentIdentity.KnownDocuments()` instead of parsing `document_ids.json` (`KnownDocuments.cs` and its test deleted). No `Refresh()`: the app owns the map, so its in-memory copy is the newest.
- **Notes on every save:** the app handles `AnnotationFileManager.Saved` (debounced auto-save, last tab closing, shutdown) and queues `LibraryWriter.NotesSaved` with a snapshot taken in the handler. The handler always runs on the UI thread here (auto-save is posted through the Avalonia marshaller; `Release`/`FlushAll` are called from the UI thread). `DocumentClosed` now only adds OCR-only text and records the page. At shutdown `ShutdownLibrary` calls `FlushAll` first, so the last edits are saved and queued before the writer stops; Core's own flush in `DocumentController.Dispose` comes after. A save of an encrypted document's annotations is ignored by the writer (indexed as locked).
- **PDF metadata:** `LibraryIndexer.Extract` reads `IPdfMetadataService.GetMetadata` (cast from the text service; reuses the document already opened for text). `DocumentInfoCleaner` drops titles that aren't titles ("Microsoft Word - draft3.docx", "main.tex", "Untitled", a path, no letters) and placeholder authors ("User", "Administrator"); subject and keywords are only normalised. Encrypted documents get none (Q4).
  - **Schema 3:** `documents.title`/`authors` and a separate `info_fts(title, author, subject, keywords)` (one row per document), separate from `name_fts` so a rename, which rewrites the name row, can't drop it. A version-2 index is **migrated in place** (columns added, table created), not rebuilt, so open history, the removed list and the backfill setting survive; `IndexerVersion` 2 makes the backfill re-extract each document with its metadata. A read-only opener (CLI, Library window) of an older build returns "no index" on schema 3, as before for any schema mismatch.
  - **Search:** a new `HitSource.Details` (weight 2.0, as file names; bm25 column weights title 1, author 1, subject 0.5, keywords 0.5), labelled "title, author or keywords". The Library window's "Titles & file names" filter covers both whole-document sources, and neither gets a snippet row: the header shows the title (else file name) and an authors line. Recents cards show the title, else the file name.
  - `library bench` unchanged: recall@10 97.9 %, MRR 0.938.
- **Search start page:** the Search pane passes the library hit's page to `SearchService.FinalizeSearch(doc, matches, startPage)` instead of re-activating a match afterwards. Core wraps to the document's first match when none is on or after that page; the pane then shows the hit's page itself, as before.

## 11g. Search limits follow-up (2026-11-17, #302)

Three limits recorded in §11b, fixed and measured with `library bench` on the same machine (50 known-item queries now, two added below; "before" is main at 3.90.0.0 on the same set). The ligature repair recovers two queries (MRR +0.04), the word families one (+0.03, part of it small rank gains elsewhere); the extra latency is the OR groups.

| | before | after |
|---|---|---|
| recall@10 | 94.0 % (47/50) | 100 % (50/50) |
| MRR | 0.900 | 0.970 |
| empty-result precision (absent terms) | 100 % (6/6) | 100 % (6/6) |
| query latency p95, 861 pages | 3.9 ms | 5.3 ms |
| database size | 4.37 MB | 4.37 MB |

- **Stemming (word families, query side).** FTS5's Porter stemmer leaves some forms apart ("diagnosing" → `diagnos`, "diagnosis" → `diagnosi`; "estimator" → `estim`, "estimate" → `estimat`). A truncated prefix query can't bridge them, because FTS5 stems the prefix too ("analy"* becomes `anali`* and finds nothing). Instead `LibraryQuery.Family` removes one ending from a plain word (longest first: `-ations … -sis -es -ed -or -er -is -e -s -y`; `-sis`/`-ses` keep their s) and ORs the root with each ending: `("diagnosing" OR "diagnos" OR "diagnosis" OR …)`. Forms that aren't words match nothing; the cost is a little query time. Not expanded: phrases, `prefix*` terms, `-excluded` terms, words with none of the endings (the stemmer already covers their inflections), words with a non-letter, and roots shorter than 5 letters. Terms are now joined with an explicit `AND`, since FTS5 doesn't join a parenthesised group implicitly. No schema or index change. The known "diagnosing" miss is now found.
- **Broken ligatures (normalisation).** A pdfTeX font with no Unicode map hands PDFium its raw codes. In the corpus (`svd_ols_ridge_pcr.pdf`) these are TeX's T1 (Cork) codes: 0x1B ff, 0x1C fi, 0x1D fl, 0x1E ffi, 0x1F ffl ("De\u001Cnition", "coe\u001Ecient"). `TextNormalizer` maps them back, plus OT1's 0x0E ffi / 0x0F ffl (OT1's ff, fi and fl are 0x0B–0x0D: vertical tab, form feed and carriage return, which stay word breaks), only when the code touches a letter. Other control characters stay word breaks (that file's T1 0x10 quote and 0x15 en dash among them). `IndexerVersion` 3, so the backfill re-extracts. Two queries were added that only match through a repaired "fi" ("unified spectral perspective", "rank deficiency automatically"); both fail with the old normaliser. The same file emits U+FFFE for a hyphen through pypdfium2's text-range call; not handled, since the app's own path (U+0002, §11b) wasn't seen to produce it.
- **Highlighting.** `LibraryIndex.MatchedForms(contentId, page, query)` reads FTS5's `highlight()` for the hit's page row and returns the words and phrases it marked, as they appear in the indexed text (stemmed forms and family members included). `LibraryQuery.HighlightPatternFor` turns them into `\b(form|form|…)\b` (whitespace in a phrase is `[\s\-]+`, at most 12 forms, the first 12 in page order), so a page hit is highlighted with exactly what the index found. `HighlightPattern`'s suffix strip remains the fallback (outline hits, no index). Still unhighlighted: a word the index joined (a line-end hyphen, a repaired ligature), since the in-document search runs on the PDF's own text.
- The trigram table §4.1 left open wasn't needed for these. It would still be the way to add partial-word matches.

## 12. Decisions

| # | Question | Decision (2026-11-16) |
|---|---|---|
| Q1 | Library = opened documents only, or also watched folders? | Opened only for v1. Folders later if asked for. |
| Q2 | Index reviewers' in-PDF comments, or only the reader's own notes? | Both, labelled ("your note" / "reviewer comment") and filterable. |
| Q3 | Missing files: keep greyed, or prune automatically? | Keep greyed with "Locate…" and "Remove from library". No automatic pruning. A file on an unplugged drive is not gone. |
| Q4 | Encrypted PDFs: never index text, or index after an opt-in once opened with the password? | Never in v1; revisit only if a reader asks. |
| Q5 | Library window, or a scope toggle inside the accordion's Search section? | Separate window (works with no document open), with a link from the accordion. |
| Q6 | Index OCR text that exists only in the live session (not baked into the file)? | Yes, flagged unverified. It is lost otherwise once the session ends. |
