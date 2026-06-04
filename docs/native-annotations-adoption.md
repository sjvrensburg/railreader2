# Task: Adopt RailReaderCore 0.17.0 native PDF annotations in railreader2

## Context

**RailReaderCore 0.17.0** ships **native PDF annotation read/write**. Until now,
railreader2 stored annotations in a JSON sidecar
(`~/.config/railreader2/annotations/<hash>.json`) that was completely divorced from
the PDF's own `/Annots`. The new release lets annotations live **inside the PDF**, so
RailReader can read Acrobat reviewers' comments and write/edit/delete annotations back
into the file as if created natively — the key feature for university users with
Acrobat Pro.

This is **opt-in**: the Core `DocumentController` still takes a consumer-supplied
`IAnnotationStore`. Today railreader2 injects `AnnotationService.Default`
(sidecar-only). Your job is to switch it to `CompositeAnnotationStore.Default` and
build the surrounding UI so the new capabilities are usable.

- **Repo:** `~/railreader2` (github.com/sjvrensburg/railreader2), Avalonia desktop app
  (`src/RailReader2/`), headless CLI (`src/RailReader2.Cli/`), Markdown export
  (`src/RailReader.Export/`).
- railreader2 consumes RailReaderCore **via NuGet** (pinned versions), currently
  **0.16.0**. (0.17.0 may take a few minutes to appear in the NuGet index after publish;
  if `dotnet restore` can't find it yet, wait and retry — don't switch to project
  references.)
- **Sample test PDF:** `~/Downloads/Day-ahead-photovoltaic-power-forecasting---Short.pdf`
  — an Acrobat review copy with **40 markup annotations** (22 Highlight, 17 Text/sticky,
  1 Caret) from reviewer `cclohessy`, on pages 2–5, plus 215 GoTo links.

## What changed in Core 0.17.0 (the API you'll consume)

- **`CompositeAnnotationStore`** (an `IAnnotationStore`, in `RailReader.Core.Services`):
  makes the PDF's `/Annots` canonical, sidecar as fallback. `CompositeAnnotationStore.Default`
  wraps `AnnotationService.Default`.
  - `Load` reads native annotations + merges the sidecar.
  - `Save` routes by writability: **writable + unsigned** → reconciled into the PDF
    (keyed by `/NM`); **read-only / signed** → JSON sidecar fallback.
  - Two signals (wire these to UI):
    - `Action<string, SidecarFallbackReason>? OnSidecarFallback` — fired once per PDF
      when a save fell back to the sidecar (`SidecarFallbackReason.ReadOnly` / `.Signed`).
      Tell the user "annotations stored separately because this PDF is read-only / signed."
    - `Action<string>? OnSidecarMigration` — fired once per writable PDF that still has
      sidecar annotations which will be **baked into the PDF on the next save**. Show a
      heads-up *before* the user's private notes enter a potentially-shared document.
- **New annotation subtypes** (in `RailReader.Core.Models`): `UnderlineAnnotation`,
  `StrikeOutAnnotation`, `SquigglyAnnotation` (all under a new `TextMarkupAnnotation` base
  alongside `HighlightAnnotation`), plus `CaretAnnotation` and `FreeTextAnnotation`.
  **Caret is read-only** (PDFium can't create carets).
- **New round-trip metadata on `Annotation`:** `Author`, `Contents`, `Subject`,
  `NativeId`, `CreatedUtc`, `ModifiedUtc`, `State` (`ReviewState` enum:
  None/Accepted/Rejected/Cancelled/Completed), `InReplyTo` (read-only — PDFium can't write
  `/IRT`), `Source` (`AnnotationSource.RailReader` | `.InPdf`), `Flags`, `ColorComponents`.
- **`AnnotationFile.Pages` / `Bookmarks` are now settable** (was get-only — this fixed a
  pre-existing data-loss bug where saved annotations didn't reload; you get the fix for
  free by bumping).
- Core's `AnnotationRenderer` and `AnnotationGeometry` **already render and hit-test the
  new subtypes** — railreader2 gets that for free via the Core packages. You do **not**
  need to re-implement rendering.

## Required tasks (minimum viable)

1. **Bump RailReaderCore to 0.17.0** in every csproj that references it. Known sites
   (verify with grep — `grep -rl 'Version="0.16.0"' --include=*.csproj`):
   `src/RailReader2/RailReader2.csproj`, `src/RailReader2.Cli/RailReader2.Cli.csproj`,
   `src/RailReader.Export/RailReader.Export.csproj`, `tests/RailReader.Export.Tests/...`.
   Restore + confirm the solution still builds (`dotnet build <sln> -c Release`).

2. **Wire `CompositeAnnotationStore.Default`** in place of `AnnotationService.Default` at
   these sites (verify line numbers — they may drift):
   - `src/RailReader2/ViewModels/MainWindowViewModel.cs` (~line 173): the
     `new DocumentController(config.ToCoreSettings(), config, AnnotationService.Default, …)`
     call — the one that lights up the desktop viewer.
   - `src/RailReader2.Cli/Commands/AnnotationsCommand.cs` (~line 31) and
     `RenderCommand.cs` (~line 39): `AnnotationService.Default.Load(pdfPath)`.
   - `src/RailReader.Export/MarkdownExportService.cs` (~line 45): same.
   - Leave the `AnnotationService.ExportJson` / `ImportJson` / `MergeInto` static helpers
     as-is (those are sidecar import/export features, still valid).

3. **Wire the two signals** once at startup (the store is a singleton): subscribe to
   `CompositeAnnotationStore.Default.OnSidecarFallback` and `.OnSidecarMigration` and
   surface them via the app's existing toast/status-message mechanism (the
   `DocumentController.StatusMessage`/`StatusBar` pattern is a good model). The migration
   heads-up in particular should be a clear, one-time notice.

4. **Update the CLI annotation serializer** (`src/RailReader2.Cli/Commands/AnnotationsCommand.cs`):
   the local `SerializeAnnotation` / `AnnotationOutput` DTO currently (a) emits `"unknown"`
   for any subtype it doesn't recognize and (b) does **not** carry the annotation's
   `Contents`. Add cases for `underline`/`strikeout`/`squiggly`/`caret`/`free_text`, and
   surface `Contents`, `Author`, `CreatedUtc`/`ModifiedUtc`, `State`, and `Source` on the
   output DTO. (This is the gap that made the integration test show a `"unknown"` caret with
   no comment text.)

## Stretch tasks (full Acrobat-review parity)

5. **Comment / annotation panel**: surface the new metadata in the desktop UI — author,
   comment text (`Contents`), created/modified dates, and review `State`. Distinguish
   `Source.InPdf` (reviewer's, e.g. a subtle badge) from `Source.RailReader` (the user's
   own). Show reply threads using `InReplyTo` (read-only).
6. **Authoring tools** for the new subtypes: text-selection-based Underline/StrikeOut/
   Squiggly (mirror the existing Highlight tool), and a FreeText "typewriter" tool. Extend
   the `AnnotationTool` usage / toolbar accordingly. (Caret stays read-only — no tool.)
7. **Review-state UI**: let users set Accepted/Rejected/etc. on a comment (Core writes
   `/State`). **Replies are display-only** — Core can read `/IRT` but PDFium has no API to
   write reply linkage, so don't build a "reply" authoring flow expecting it to persist as a
   thread.

## Constraints & gotchas (important)

- **Do not modify RailReaderCore** — it's a separately published NuGet package. If you find
  a Core bug, note it for a Core-side fix; don't vendor or patch it here.
- **railreader2 git hygiene:** never `git add -A` — the working tree has an `experiments/`
  directory with 100MB+ of untracked artifacts. Stage specific files only.
- Build/test with `-c Release`. Note that the Release config may skip the test projects in
  some setups — run tests explicitly (`dotnet test …`).
- Use the repo's **PR-merge flow**; branch for changes, open a PR, and **do not tag or
  release** without explicit instruction (a tag triggers a publish for that repo too).
- **Bookmarks stay in the sidecar** — PR4 (bookmarks into the PDF) was deliberately deferred
  because PDFium has no API to write bookmarks/named-destinations. Don't try to move
  bookmarks into the PDF.
- There is **no "privacy / sidecar-only" mode** by design — the migration heads-up (task 3)
  is the agreed UX, not an opt-out toggle.

## Verification

- **Headless smoke test (fastest):**
  `dotnet run --project src/RailReader2.Cli -- annotations "~/Downloads/Day-ahead-photovoltaic-power-forecasting---Short.pdf" --format json`.
  Before the wiring change this prints "No annotations found"; after, it should dump **40**
  annotations with their `Contents` (e.g. "accuracy", "Model") and `Author` "cclohessy".
- **Write round-trip:** open the sample PDF in the desktop app (or a small harness using
  `CompositeAnnotationStore.Default`), add a comment and delete one of the reviewer's, save,
  reopen — your comment should persist and the deleted one should be gone, with the other 39
  intact. (`pdfinfo` should still report `Form: AcroForm` and `qpdf --check` should be clean.)
- Confirm the new subtypes render in the overlay and are selectable/movable (Core provides
  this; you're verifying the model flows through).
- Full solution build clean; run the test suites.

## Definition of done

railreader2, built against RailReaderCore 0.17.0, opens an Acrobat-reviewed PDF and shows
the reviewers' comments natively; the user can add/edit/delete annotations that persist
**into the PDF** (writable files) or the sidecar (read-only/signed), with a clear heads-up
on first migration; and the CLI `annotations` command faithfully dumps every subtype with
its metadata.

---

## Reviewer notes (verified against the repo, 2026-06-04)

The tasks above were checked against the working tree. The concrete claims hold: the
`Version="0.16.0"` sites, the line numbers, and the CLI serializer gap (`"unknown"`
fallback, no `Contents`) all match. Refinements found during review:

1. **The bump touches five packages per src csproj, not one.** Each src project references
   `RailReader.Core`, `.Core.Pdfium`, `.Core.Analysis`, `.Renderer.Skia`, **and
   `.Core.Vlm.OpenAI`** (the test project omits Vlm). A blanket replace of
   `Version="0.16.0"` → `0.17.0` is correct *and* safe here — confirmed that every
   `0.16.0` reference in the tree is a RailReaderCore package, and that all five are
   published at 0.17.0 on NuGet (including `Vlm.OpenAI`, which versions in lockstep). Verify
   the lockstep assumption on future bumps before a blanket replace.

2. **No automated regression coverage in *this* repo for tasks 2 & 4.** The only test
   project is `tests/RailReader.Export.Tests` — the `tests/RailReader.Core.Tests` that
   `CLAUDE.md` references no longer exists. So "run the test suites" exercises Export only;
   the store swap and the CLI serializer rewrite are verified solely by the headless smoke
   test and the manual desktop round-trip. Treat those manual checks as load-bearing, not
   optional. (A small golden-output assertion on the `annotations` command would be the
   cheapest durable guard, since its JSON is deterministic.)

3. **Task 3 signal wiring — two implementation gotchas.** `OnSidecarFallback` /
   `OnSidecarMigration` are settable `Action<…>?` delegate *properties* on the
   `CompositeAnnotationStore.Default` **singleton**, not multicast C# events. (a) Assign once
   (`= handler`); don't `+=` expecting event semantics, and beware clobbering if the VM is
   ever reconstructed (the singleton outlives the VM). (b) The handler ultimately sets
   `StatusToast` (an `[ObservableProperty]`); marshal to the UI thread before touching it if
   a signal can fire off-thread. Also: **task 3 is desktop-only** — the CLI and Export sites
   only ever `Load`, never `Save`, so they never raise these signals; don't wire them there.

4. **Stretch tasks 5–7 are each a substantial feature, not a tail.** Ship tasks 1–4 as the
   first PR (the actual enablement). Land the comment panel (5), the new authoring tools (6),
   and the review-state UI (7) as *separate* follow-up PRs — bundling them produces an
   un-reviewable mega-branch.

5. **Namespace vs. assembly (clarity).** `CompositeAnnotationStore` lives in the
   `RailReader.Core.Pdfium` *assembly* but the `RailReader.Core.Services` *namespace*. All
   four target files already `using RailReader.Core.Services;`, so the swap needs **no new
   using** — it is a drop-in for `AnnotationService.Default` (both implement
   `IAnnotationStore`).

6. **Tangential:** `CLAUDE.md` still lists a `tests/RailReader.Core.Tests` project and the
   `slnx`'s test set that no longer fully exists. Worth a follow-up fix so future readers
   aren't misled by "run the test suites."

---

## Reference — deeper context in RailReaderCore

When you need the *why* behind a design choice or the precise API contract, these are the
authoritative sources (the RailReaderCore repo is checked out locally at `~/RailReaderCore`,
and is also on GitHub):

- **PR #33 — "Native PDF annotations (0.17.0)"** —
  `gh pr view 33 --repo sjvrensburg/RailReaderCore`
  (or https://github.com/sjvrensburg/RailReaderCore/pull/33). This is the merged feature PR.
  Its **commit history is the best step-by-step explanation** — each commit message
  documents one piece in detail (read-API bindings → model extension → `PdfAnnotationReader`
  → `CompositeAnnotationStore` merge → incremental→full-save writer → `/NM` reconciliation →
  Save routing + signed-PDF guard → cross-engine `/AP` fidelity → migration hardening → the
  code-review fix commits). If a behavior surprises you, find the commit that introduced it.

- **`CHANGELOG.md` → `## 0.17.0` section** (`~/RailReaderCore/CHANGELOG.md`) — the
  consumer-facing summary: Added / Changed (**breaking notes**) / Fixed. Start here for
  "what's new and what might break me."

- **`docs/native-pdf-annotations-plan.md`** (`~/RailReaderCore/docs/`) — **the single most
  useful document.** It's the full design + decision log: the locked decisions
  (sidecar-as-fallback, the academic-markup scope, **why PR 4 bookmarks-in-PDF was
  deferred**, **why there's no privacy/sidecar-only mode**), the per-phase breakdown
  (PR 1 read → PR 2 write → PR 5 migration), edge cases and invariants, the railreader2
  integration-test findings, and a risk register. Read this before the stretch tasks — it
  explains the constraints you must respect (e.g. PDFium can't write
  bookmarks/named-destinations/appearance-streams; carets and `/IRT` replies are read-only;
  writes use a full `FPDF_SaveAsCopy` rewrite, not incremental, because incremental corrupts
  the xref on linearised PDFs).

- **`CLAUDE.md` → "Annotation storage" paragraph** (under *RailReader.Core.Pdfium*) — a
  concise architecture summary + the PDFium write limitations, in the repo's own guidance
  file.

- **Source as API reference** (read these directly for exact signatures/behavior):
  - `src/RailReader.Core/Models/Annotations.cs` — the complete annotation model: base
    `Annotation` fields, the subtype hierarchy, and the `ReviewState` / `AnnotationSource`
    enums.
  - `src/RailReader.Core.Pdfium/CompositeAnnotationStore.cs` — the store you're wiring:
    routing logic, the `OnSidecarFallback` / `OnSidecarMigration` signals, and the
    sidecar-merge semantics.
  - **Worked examples** of driving the whole API live in the test suite — copy patterns from
    `tests/RailReader.Core.Tests/`: `CompositeAnnotationStoreRoutingTests.cs` (writable vs
    read-only routing + signals), `PdfAnnotationMigrationTests.cs` (lazy migration + the
    heads-up), `PdfAnnotationReaderTests.cs` / `PdfAnnotationReconcileTests.cs` (read +
    add/edit/delete round-trips). These show exactly how `Load`/`Save` behave for each case.

- **Memory aid for limitations** (so you don't rediscover them the hard way): PDFium has
  **no write API for bookmarks / named destinations / catalog** and **does not generate
  appearance streams** — written markup relies on viewer-side synthesis (verified to render
  in Poppler & MuPDF; FreeText is the exception and may show an empty box in strict viewers).
  These are documented in the plan doc's risk register and in the `CHANGELOG`/`CLAUDE.md`
  notes above.
