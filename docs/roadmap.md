# RailReader2 Roadmap: Features Adapted from Cauchy and PDF Scholar

Prepared 26 October 2026. Based on [jerrydjin/cauchy](https://github.com/jerrydjin/cauchy) and [sjvrensburg/railreader2](https://github.com/sjvrensburg/railreader2) at commit `960e8cd` (24 October 2026). Phase 5 (M8–M10) added 17 November 2026 from [emilmsh/pdf-scholar](https://github.com/emilmsh/pdf-scholar). Code links below point at where files were in October; some have since moved (`ReferenceIndex` now lives in `src/RailReader2.References/`).

## Status (17 November 2026)

| Item | Status |
|---|---|
| M1 Statement and equation references | Shipped |
| M2 Reference benchmark command | Shipped |
| M3 Previews for unlinked citations | Shipped (auto-pin cited references) |
| M4 Content-based document identity | Shipped (v3.82.0.0; Core 0.72.0) |
| M5 Ask about a selection, with evidence labels | Not started |
| M6 Portable reading session package | Shipped (v3.84.0.0) |
| M7 Library-wide search and recents dashboard | Shipped (v3.87.0.0) |
| M8 Notes over MCP | Done on branch `feat/m8-notes-mcp` (not yet released): `railreader2-mcp` in every package, four read-only tools, consent switch, hidden documents and folders, Settings ▸ Library ▸ AI Tools, Copy Notes for AI, reply threads, docs |
| M9 Reference cards with abstracts | Not started |
| M10 Zotero and DOI citations | Not started |
| Q1 OCR text marked unverified | Shipped |
| Q2 CLI agent providers | Not started |
| Q3 Highlight colour categories | Shipped (not yet in Markdown export) |

## Guiding principles

These come from Cauchy's design and suit RailReader2's reader base.

1. **Abstain rather than guess.** A wrong preview, citation or answer costs a magnified reader more than a missing one, because checking it means a long pan away from the current line. Every resolver below returns nothing when the match is ambiguous.
2. **Measure before shipping.** Each heuristic gets a labelled test set, including pages that should produce nothing, with precision reported ahead of recall (the approach in [Cauchy's benchmark README](https://github.com/jerrydjin/cauchy/blob/main/Benchmarks/README.md)).
3. **Local-first AI.** Anything that uses a model goes through the existing OpenAI-compatible endpoint, so Ollama or vLLM work, and stays behind the experimental flag until it has been evaluated.
4. **Everything reachable from the CLI.** New capabilities get a `railreader2-cli` command so agents and tests can drive them.

## Priority summary

| Rank | Milestone | Value | Effort | Depends on |
|---|---|---|---|---|
| 1 | M1 Statement and equation references | High | S–M | — |
| 2 | M2 Reference benchmark command | High (enables M1, M3) | S | — (build alongside M1) |
| 3 | M3 Previews for unlinked citations | High | M | M2 |
| 4 | M4 Content-based document identity | Medium (foundation) | M | Possibly a RailReaderCore change |
| 5 | M5 Ask about a selection, with evidence labels | High | L | M4 preferred, M2 for evaluation |
| 6 | M6 Portable reading session package | Medium | M | M4 |
| 7 | M7 Library-wide search and recents dashboard | Medium | M | M4 |
| — | Quick wins Q1–Q3 | Low–Medium | S each | See below |

Effort is relative: S is a self-contained change to one service plus tests, M touches several services or the UI, and L adds a new subsystem.

## Phase 1: Better references

### M1 Statement and equation references

**Why first:** this is the cheapest high-value change, and it extends a subsystem that already works. `ReferenceIndex` only parses "Figure N" and "Table N" for auto-pinning ([ReferenceIndex.cs](https://github.com/sjvrensburg/railreader2/blob/main/src/RailReader2/Services/ReferenceIndex.cs)). Cauchy covers Theorem, Lemma, Proposition, Corollary, Definition, Example, Exercise, Remark, Proof and equation numbers ([ReferenceKind.swift](https://github.com/jerrydjin/cauchy/blob/main/Cauchy/Models/ReferenceKind.swift)).

**Scope**

- Extend `ReferenceIndex.ParseLine` to recognise those statement kinds and numbering forms such as `3.2`, `A.1` and "Theorem (3.2)". Also recognise equation mentions: "(3.2)", "Eq. (4)", "Equation 4" and "eqs. (3)–(5)".
- **Statement targets:** treat a text block whose first line starts with "Theorem 3.2." (or similar) as the statement. Its extent runs to the next heading, the next statement header, or "Proof".
- **Equation targets:** read the number from the right-hand (or left-hand) edge of display-maths blocks that the layout model has already found. Multi-line environments map one number to one line band.
- Auto-pin the resolved target into a portal using the existing `TryAutoPin` path. Add a separate toggle for statements and for equations, since some readers will only want figures.
- **Abstention:** if a label resolves to more than one target, or a "(3)" in running text could be an enumeration, show nothing.

**Done when**

- `ReferenceIndexTests` covers each new kind, including hard negatives such as "(3)" in lists, "Section 3.2" and years in parentheses.
- Precision is at least 0.95 on the M2 labelled set, with recall reported.

### M2 Reference benchmark command

**Why now:** M1 and M3 are heuristics, and you'll want numbers rather than impressions. This is also the natural place to compare Heron with PP-DocLayoutV3 on downstream tasks.

**Scope**

- Add `railreader2-cli references <pdf> [--ground-truth labels.json] [--layout heron|ppdoclayout]`. It emits the detected mentions, the resolved targets and any abstentions as JSON, following the existing `structure` and `vlm` command patterns.
- **Ground-truth format:** per page, the expected mentions and their target page plus block index, and pages that should produce nothing.
- **Report:** precision, recall and F1 per reference kind; abstention rate; and a list of failures. Compare against two simple baselines: plain regex with the nearest match, and first match in the document.
- **Corpus:** start with 20–30 arXiv papers across statistics, econometrics and ML, plus a few scanned pages so OCR is exercised.

**Done when:** the benchmark runs in CI on a small fixed subset and fails if precision drops below a threshold.

### M3 Previews for unlinked citations

**Why:** many PDFs, especially older working papers and journal reprints, have no internal links. Link peek ([LinkPreferences.cs](https://github.com/sjvrensburg/railreader2/blob/main/src/RailReader2/Services/LinkPreferences.cs)) can't help there. Cauchy reads the bibliography itself and abstains on ambiguous matches ([BibliographyIndex.swift](https://github.com/jerrydjin/cauchy/blob/main/Cauchy/Services/BibliographyIndex.swift)).

**Scope**

- **Bibliography parsing:** find the References or Bibliography section by heading. Split it into entries using numeric labels (`[12]`, `12.`), alphanumeric keys (`[Smi19]`) or hanging-indent geometry for author-year styles.
- **Citation detection:**
  - numeric: `[3]`, `[3, 7–9]`
  - alphanumeric keys
  - author-year: "Smith (2019)", "(Smith and Jones, 2019a)" and "et al."
- **Display:** show the entry in the same peek popup as link peek, or optionally pin it to a portal. If a hyperlink exists, use the link target instead.
- **Abstention:** if an author-year citation matches two entries, or the entry can't be split cleanly, show nothing.
- **CLI:** add a `citations` mode to the M2 command, and extend the ground-truth format to cover citations.

**Done when:** precision on the labelled set is at least 0.95 for numeric styles and at least 0.9 for author-year styles.

## Phase 2: Foundations

### M4 Content-based document identity

**Why:** this unlocks M5, M6 and M7. Today, portals are keyed by the PDF's full path ([PortalSetManager.cs](https://github.com/sjvrensburg/railreader2/blob/main/src/RailReader2/Services/PortalSetManager.cs)). Annotation sidecars are keyed by a SHA-256 of the path, not of the file's contents (see the storage section of [CLAUDE.md](https://github.com/sjvrensburg/railreader2/blob/main/CLAUDE.md)). If you move or rename a PDF, or open it from a different machine's mount, its portals and sidecar annotations are silently lost. Cauchy ties its stored evidence to a hash of the PDF's contents.

**Scope**

- **Document ID:** compute a content hash when a document opens, cached by path, size and modification time so large PDFs aren't re-hashed on every open.
- **Lookup:** key shell-side sidecars (portals, view rotation, and the AI threads coming in M5) by content hash. Keep a path-to-hash map so you can migrate old records and fall back to path lookup.
- **Annotation sidecars:** these live in Core's `CompositeAnnotationStore`. Either propose content-hash keying upstream in RailReaderCore, or add a shell-side remap layer that runs on open.
- **Edge case:** writing annotations back into the PDF changes its contents. Record the old-to-new hash on each write-back so the identity survives.

**Done when:** a PDF that is moved, renamed or copied to another machine reopens with its portals, rotation and sidecar annotations intact.

## Phase 3: Grounded AI

### M5 Ask about a selection, with evidence labels

**Why:** this is Cauchy's core feature and the largest new capability. RailReader2 already has the model plumbing (the VLM settings and the `vlm` CLI command). Cauchy's useful idea isn't the chat itself. It's the evidence labels and the record of what the model was shown ([SelectionThread.swift](https://github.com/jerrydjin/cauchy/blob/main/Cauchy/Models/SelectionThread.swift), [AskContextRetriever.swift](https://github.com/jerrydjin/cauchy/blob/main/Cauchy/Services/AskContextRetriever.swift)).

**Scope**

1. **Entry point:** right-click a highlight or text selection and choose "Ask about this…". This is also available on a portal.
2. **Context assembly:**
   - the selection itself
   - the surrounding block
   - any references resolved through M1 and M3
   - the top passages from retrieval

   Retrieval is keyword-based (BM25 over the per-page text you already extract) by default. Semantic search through an optional local embedding endpoint can be added later.
3. **Evidence record:** store the page and block IDs sent to the model, the model name, the time and the document's content hash (from M4).
4. **Answer labels**, assigned by a separate check rather than by the answering model:
   - **PDF only:** every claim cites a supplied passage.
   - **Mixed:** some claims rely on general knowledge.
   - **Not established:** the supplied context doesn't answer the question.
   - **Unverified:** the check failed or wasn't run.
5. **Storage:** save each question and answer as a reply thread under the highlight, using the existing reply-thread support that writes threaded replies into the PDF. Answers then travel with the PDF and appear in the Comments pane and RailMark. Put the evidence record in a sidecar keyed by the M4 content hash.
6. **Reading at high zoom:** the answer pane follows the UI font scale and can be read with the rail. Cited passages open in a portal rather than moving the main view.
7. **CLI:** `railreader2-cli ask <pdf> --page N --text "…" --question "…"` returns JSON containing the answer, label and evidence.
8. **Markdown export:** questions and answers, with their labels, appear next to their highlights.

**Done when:**

- It ships behind the experimental flag.
- A small labelled question set (questions answerable from the PDF, plus ones that aren't) shows the "not established" label firing on most unanswerable questions with a local 7–14B model.

**Risks:**

- **Overconfident labels:** small local models can mislabel their own answers. Keep the check model separate and configurable, and treat "unverified" as the default.
- **Token cost:** cap the context and show the token count.

## Phase 4: Portability and library

### M6 Portable reading session package

**Why:** this lets you move a reading session between your Linux and Windows machines, or share it with a collaborator. Cauchy's equivalent bundles the PDF, reading state, highlights, conversations and index, all tied to a content hash ([Cauchy README](https://github.com/jerrydjin/cauchy)).

**Scope**

- **Format:** a zip file with a `.railreading` extension containing:
  - the PDF
  - `manifest.json` (format version, content hash, app version)
  - reading state for each view (page, zoom, camera, rail block and line)
  - portals, named bookmarks, view rotation, colour effect
  - sidecar annotations
  - M5 threads and evidence records
- **Import:** if the hash matches a PDF already in the library, merge into it. Otherwise extract to a chosen folder.
- **CLI:** `railreader2-cli package <pdf> -o out.railreading` and `unpack`.
- Refuse to package an encrypted PDF without asking first, matching the existing refusal on flattened export.

**Done when:** a package created on Linux opens on Windows with the same view state and every annotation and portal.

### M7 Library-wide search and recents dashboard

**Why:** today there is only the `recent_files` list. Cauchy can search highlights and conversations across all documents, and hiding a paper from recents keeps its notes.

**Scope**

- **Index:** a local SQLite FTS5 index over annotation text, comments, replies, M5 threads and document titles, keyed by the M4 content hash. Update it on save.
- **UI:**
  - a search panel whose results open the document at the right place
  - a recents dashboard with thumbnails, last position and annotation counts
  - removing a document from recents keeps its data
- **CLI:** `railreader2-cli search "query"` for agents.

## Phase 5: Your notes, outward (added 17 November 2026, ideas from PDF Scholar)

Adapted from [emilmsh/pdf-scholar](https://github.com/emilmsh/pdf-scholar) (MIT, Electron) by behaviour, from its README, spec and MCP doc. Same principles as above: abstain rather than guess, measure first, local by default, CLI first.

| Rank | Milestone | Value | Effort | Depends on |
|---|---|---|---|---|
| 8 | M8 Notes over MCP, for Claude Code and other agents | High | S–M | M7 (shipped) |
| 9 | M9 Reference cards with abstracts | High | M | M3 (shipped) |
| 10 | M10 Zotero and DOI citations | Medium–High | M | M4 (shipped) |

### M8 Notes over MCP, for Claude Code and other agents

**Why:** M7 built a full-text index of every document, its annotations, comments, replies and bookmarks. Today only the Library window and `library search` can read it. An MCP server lets the reader ask Claude Code, Codex or Claude Desktop "what did I note about identification in the minimum-wage papers?" while writing, with no PDF opened. PDF Scholar ships exactly this, read-only. For RailReader2 it is mostly wiring: `LibraryIndex.OpenReadOnly`, `LibraryQuery` and `LibraryResults` already exist, and the CLI already opens the index read-only.

**Scope**

- `railreader2-cli mcp`: a stdio MCP server inside the existing AOT CLI. Use a small hand-written JSON-RPC loop if the C# MCP SDK isn't AOT-clean. It opens `ConfigDir/library/library.db` read-only per call and never writes it.
- **Tools** (read-only):
  - `search_notes(query, kind?)`: hits with document title, page, snippet and note author
  - `list_documents()`: title, authors, page count, note count, last opened
  - `get_document_notes(doc)`: every annotation in page order, with the marked text, comment, replies, colour-category name (Q3) and yours-or-reviewer (`NoteOwnership`)
  - `search_pages(query, doc?)`: page-text hits, so an agent can quote the paper and not only the notes
  - later, `get_thread(id)` for M5 Q&A threads with their evidence labels
- **Never exposed:** PDF bytes, images, file paths or folder names (documents are named by title or file name and an opaque id), and anything from an encrypted document. `Locked` documents already index only their file name, and that rule holds here too.
- **Consent:** off by default. Settings ▸ Library (advanced) gets "AI tools" switches for Claude Code (`claude mcp add --scope user`), Claude Desktop (its config JSON) and Codex (`~/.codex/config.toml`). Each switch writes or removes only its own `railreader2` entry, and refuses a config file it can't parse. A "Copy setup" button gives the entry to paste into any other MCP client. Switch-off also puts a marker in the index, so the server refuses even if a stale entry survives.
- **Per-document opt-out:** a "Hide from AI tools" switch on a document (Library window row menu, plus a folder rule) is stored in the index and filtered in every tool.
- **Fallback:** "Copy notes for AI" in the Comments pane copies the open document's notes as Markdown.
- **Docs:** `docs/privacy.html` (what a tool can read; what it reads goes to that tool's provider), the user guide, and `docs/guide.html`.

**Done when:** Claude Code, connected through the switch, answers a question from notes across three test papers. A hidden document and an encrypted one never appear in any tool's output (covered by a CLI test that drives the server over stdio). The AppImage and the Inno install both find the CLI path the switch writes.

### M9 Reference cards with abstracts

**Why:** M3 resolves a citation to its reference-list entry, but the reader sees a cropped picture of it (a portal or link peek). For deciding whether a cited paper is worth chasing, the entry as text plus the paper's abstract is what matters. At high zoom, reading an abstract in a card beats panning to the bibliography and then to a browser. PDF Scholar does this as Zotero's reader does: selectable entry text, live DOI and URL links, every member of "[3, 5–9]" listed, and one click to fetch the abstract.

**Scope**

- **Card:** a citation's peek (and its auto-pin, as an option) shows one row per resolved entry. Each row has the entry text (de-hyphenated, through the library's `TextNormalizer`), live DOI/arXiv/URL links, "Go to entry" (with Back) and "Abstract". It follows the UI font scale and the colour effect. Any entry M3 can't split cleanly falls back to today's rendered crop.
- **Link destinations as evidence:** when a citation is a real link, its named destination (hyperref's `cite.*`, publishers' `bib12`/`CR12`/`ref012`, and `figure.*`/`equation.*`/`table.*` for the other kinds) decides the target outright, with no heuristic needed. Feed this into `ReferenceIndex` for all reference kinds. It also gives free labelled cases for the M2 benchmark (linked PDFs become ground truth for scoring the unlinked heuristics).
- **Abstract fetch (network, per click only):** a printed DOI or arXiv id comes first. Otherwise use Crossref's bibliographic matcher, and believe the match only when title and year agree; flag a matched (not printed) identity in the card so a wrong match is visible. Fetch abstracts from Crossref, OpenAlex, Europe PMC, then the arXiv API, all keyless. Send only the DOI, or that one entry's text. Cache results per DOI in `ConfigDir`. When a work has no abstract, the card says so plainly. Nothing is fetched until the click.
- **CLI:** `references --abstracts` for testing the matcher; a `--offline` default in CI.
- **Docs:** add the network call to `docs/privacy.html` next to the update check.

**Done when:** on the `tests/references/` corpus, card text is correct for ≥ 95 % of resolved entries. On a labelled set of ~100 entries, a fetched abstract belongs to the right paper ≥ 98 % of the time (a wrong abstract is worse than none, so precision comes first and recall is reported). Using destination names doesn't lower precision on any kind.

### M10 Zotero and DOI citations

**Why:** the reader base is academics writing papers. While reading, they want the paper's own reference: "copy citation", "copy BibTeX", "show in Zotero". PDF Scholar works as Zotero's external reader and does all three. RailReader2's annotations are already standard PDF annotations, so Zotero's "Import Annotations…" can take them, but that workflow is undocumented and the app knows nothing of Zotero.

**Scope**

- **External reader:** document setting RailReader2 under Zotero ▸ General ▸ "Open PDFs using" (Linux and Windows). Check that annotations written by Core show in Zotero's reader. Say plainly that Zotero's import *moves* them out of the file.
- **Zotero awareness:** recognise a stored attachment by its `storage/<KEY>/` path, and a linked one through Zotero's local API (`127.0.0.1:23119`, localhost only, which the user enables in Zotero). A Zotero section in the File menu offers "Show in Zotero" (`zotero://select/...`), "Copy citation", "Copy reference" and "Copy BibTeX". The style is one setting across a handful of CSL styles Zotero ships (APA 7, Chicago, Harvard, IEEE, Nature, Vancouver). BibTeX comes from Zotero's own exporter, leaving out `file` and `abstract`.
- **DOI fallback:** for a document outside Zotero, find a DOI in the PDF metadata (`IPdfMetadataService`) or on the first pages. "Fetch reference from doi.org" gets CSL-JSON by content negotiation and formats it in the same style. That click is the only network call, and only the DOI is sent. A Zotero record, when one exists, wins over the publisher's.
- **Library tie-in:** store the resolved DOI and citation key in the M7 index, so library search finds a paper by its key, and M8's `list_documents` returns the key too (an agent writing LaTeX can then `\cite` what the reader noted).
- **CLI:** `railreader2-cli cite <pdf> [--style apa|...] [--bibtex]`.

**Done when:** on a Zotero library with stored and linked attachments, every opened paper gets its correct item. DOI detection finds the DOI on ≥ 90 % of a sample of published papers with no false DOI. The BibTeX output parses with `biber --tool`.

## Quick wins (any time)

| ID | Item | Notes |
|---|---|---|
| Q1 | Mark OCR text as unverified | When text came from OCR, show it with a distinct style in previews, and label it in M5 evidence, as Cauchy does with its OCR candidates. |
| Q2 | CLI agent providers | Add a provider that runs a local command such as `claude -p` or `codex exec` using the user's own sign-in, alongside OpenAI-compatible endpoints. Useful for M5 and for copy as LaTeX. |
| Q3 | Highlight colour categories | Optional names for highlight colours (for example "definition", "question", "result") so you can filter on them in M7 and Markdown export. |

## Out of scope

- **Platform-specific features:** Apple Intelligence, Apple Vision OCR and the macOS 27 interface. RailReader2 already has cross-platform equivalents (the VLM endpoint and its own OCR models).
- **One document per window:** a regression from tabs, split views and tear-off windows.
- **Hosted retrieval services:** these conflict with the local-first principle.

## Suggested sequence

1. **Sprint A:** M2 and M1 together. Ship statement and equation auto-pinning once the benchmark shows it's precise enough.
2. **Sprint B:** M3, plus Q1.
3. **Sprint C:** M4. Start the RailReaderCore discussion early, because the annotation store may need an upstream change.
4. **Sprint D–E:** M5 behind the experimental flag, plus Q2.
5. **Sprint F:** M6, then M7, plus Q3.
6. **Sprint G:** M8 (smallest of the three, and it builds straight on M7). Do it before M5 if M5 is still waiting on its question set, and add `get_thread` once M5 lands.
7. **Sprint H:** M9. Do the link-destination evidence first, because it improves M1/M3 precision and the benchmark on its own.
8. **Sprint I:** M10. Its DOI detection is shared with M9's abstract lookup, so build that piece once.
