# Portals — linked context viewports (design)

**Status:** Implemented (Phases 0–2), 2026-06-09 — shell-only, no RailReaderCore change
**Author:** drafted with Claude Code, 2026-06-08
**Related roadmap item:** "Linked view / context preview — secondary viewport showing the surrounding paragraph at lower zoom" (Portals is the general, user-authored form of the same idea)

---

## 1. Motivation

When you read a paper at high magnification, the text constantly refers to things you can't see: *"as shown in Figure 3"*, *"see Table 2"*, *"by Equation (4)"*. Today you have to jump away, look, and jump back — which is exactly the navigation tax RailReader2 exists to remove.

[Sioyek](https://sioyek.info/) solves this with **Portals**: you link a region on one page to a region on another, and the target stays visible in a secondary viewport that updates automatically as you read past the linked source. While you read the paragraph that references Figure 3, Figure 3 sits beside it.

This document proposes how to build the same capability on RailReader2's architecture, what is reusable today, what is shell-only vs. what would eventually need RailReaderCore, and a phased plan that de-risks the hard parts first.

The earlier exploration of generic window **docking** (Dock.Avalonia) was abandoned (see the accordion side-panel decision). **Portals does not need a docking framework** — it is a secondary-render + state-sync problem, and both halves already have working precedents in the codebase (the minimap, and the VLM block-crop renderer).

## 2. Goals / non-goals

**Goals**
- Let a user link a **source** region (the referencing text) to a **target** region (the figure/table/equation) within the same document.
- Show the target in a secondary view that **auto-updates** when the reading position enters a source region.
- Support a **detachable** secondary view so the target can live on a second monitor (Sioyek's "multi-monitor context linking").
- Persist portals per-document; survive close/reopen.
- Reuse the same secondary-viewport infrastructure for the automatic **context preview** roadmap item.

**Non-goals (initially)**
- Cross-document portals (target in a *different* PDF).
- A fully pannable/zoomable secondary viewport (v1 shows a static crop that swaps as you read; this is faithful to the core value).
- Storing portals *inside* the PDF as native annotations.
- Coordinate-region (free-rectangle) sources/targets — v1 anchors to **detected layout blocks**, which is more robust (see §5.1).

## 3. User experience

### 3.1 Authoring a portal
The authoring flow is the real design question (the rendering is comparatively easy). Proposed primary path, reusing existing surfaces:

1. While reading, the user reaches a reference (*"…in Figure 3…"*).
2. They open the **Index** accordion section (`Ctrl+Shift+I`) — which already lists every detected figure, table, and equation with a thumbnail — and choose **"Link to current reading position"** on Figure 3.
   - The **source** is the block at the current reading position (`GetReadingPosition()` → page + block).
   - The **target** is the Index entry's block.
3. The portal is created and immediately becomes active (you are reading the source), so Figure 3 appears in the portal view.

Alternative / complementary paths:
- **Right-click a detected block** → *"Set as portal target"*, then *"Link from current position"* (or vice-versa). The block right-click menu already exists (Copy as LaTeX/Markdown/Image).
- A **Portals** management section (see §3.3) for editing/removing links.

### 3.2 Viewing
- A **Portal panel** shows the active target. When the reading position crosses from one source region into another, the panel swaps to that portal's target. When no source is active, the panel shows the most-recent target (pinned) or a hint.
- The target render is a crop of the target block at a comfortable DPI, scaled to fit the panel.

### 3.3 Placement: dockable **and** detachable, without a docking framework
- **In-window (v1):** a **Portals** section in the existing single-open accordion side panel — discoverable, consistent, zero new window plumbing. (It can also be a minimap-style floating overlay; the accordion is lower-risk.)
- **Detached (v2):** a **"pop out"** button promotes the portal view to a borderless, non-modal `Window.Show()` that the user drags to a second monitor, with an optional **always-on-top** toggle. This is a plain second window — none of the `ControlRecycling`/reflow problems that sank the Dock.Avalonia attempt.

## 4. Data model & persistence

A portal is anchored to **layout blocks**, identified by `(page, blockIndex)` within that page's `PageAnalysis`:

```jsonc
// ConfigDir/portals/<sha256-of-pdf-path>.json
{
  "version": 1,
  "portals": [
    {
      "id": "…",                 // stable id
      "label": "Figure 3",        // from the target block / caption, editable
      "source": { "page": 4, "block": 7, "line": 5 },  // line-precise: fires at the reference, not the whole paragraph
      "target": { "page": 9, "block": 2 },             // targets are whole-block (line omitted / -1)
      "createdUtc": "2026-06-08T…"
    }
  ]
}
```

**Persistence:** a **shell-managed sidecar**, mirroring `CustomLayoutModel` (`AppConfig.ConfigDir/custom_layout_model.json`) and the annotations sidecar convention (`ConfigDir/annotations/<sha256>.json`). Keyed by the PDF's SHA-256 (same keying as annotations). **This needs no RailReaderCore change.**

> **Open question — block-index stability. (Resolved in implementation.)** Persistence assumes block indices are stable across sessions, i.e. re-running layout analysis on the same page yields the same block ordering. As shipped, each anchor stores `(page, block)` **plus** `role` + a **normalized bbox**; `ResolveAnchorBlock` validates the stored index by role + bbox (ε ≈ 2%) and falls back to the nearest-centre same-role block if a page is re-analysed into a different order — so the feature is robust to ordering changes regardless.
>
> Separately, the *reading-position → block-index* seam (does `ReadingPosition.BlockIndex` index the same list as `PageAnalysis.Blocks`?) is **closed by construction** in Core: `RailNav.CurrentNavigableArrayIndex == Blocks.IndexOf(CurrentNavigableBlock) == ReadingPosition.BlockIndex`, all the page-level index into `analysis.Blocks`. The sync loop reads `CurrentNavigableArrayIndex` directly (cheaper than `GetReadingPosition()`, which extracts text on every call).

## 5. Architecture

### 5.1 Why block-anchored (not coordinate-anchored)
Sioyek anchors source/target to raw page coordinates and intersection-tests the scroll viewport. RailReader2 already has something better: **the reading position is reported as a block**, and every page's blocks are available with bounds and role. Anchoring to blocks:
- is robust to zoom and to RailReader2's block-locked rail navigation;
- gives free, meaningful labels ("Figure 3" from the target's role/caption);
- reuses `FindBlockAt()` hit-testing for the right-click authoring path.

**Sources are line-precise (targets stay whole-block).** Because authoring always happens in rail mode, the source captures the *line* you're on (`pos.LineIndex`), not just the block, plus the line centre's normalized Y for re-analysis stability. A source matches once the reading line reaches its anchored line (`currentLine ≥ anchoredLine`), and "most-recently-passed line wins", so multiple references in one paragraph (line 2 → Fig 3, line 8 → Fig 4) each take over in turn — impossible with a whole-block source. A target is always whole-block (you show the entire figure/table/equation). Pre-line sidecars (no `line` field, default -1) load and behave as whole-block sources.

**On-page markers.** Always-on, subtle indicators show where portals are anchored on the current page: a small **gutter circle** beside each source line and a **corner badge** on each target block (positioned from the stored normalized anchors, so they show even before a page is analysed). The currently-pinned portal's two markers are drawn accented; the rest are muted. A marker stands for one or more portals (a source line can link several targets; a target can have several sources) — clicking a single-portal source marker shows its target, a single-portal target badge jumps to its source, and a multi-portal marker opens a chooser. Drawn in a dedicated composition overlay (`PortalMarkerLayer`, fixed pixel size, mapped from page space each frame); hit-tested in `ViewportPanel`.

**Pin-until-different.** Once a portal's target is shown it stays up — moving off the source line, leaving the block, scrolling around — and only switches when the reading position reaches a *different* portal's source. So the line anchor sets the **switch point** between portals rather than gating visibility moment-to-moment; the target never spontaneously clears (it clears only on tab switch with no active source, deletion of the shown portal, or no document). `MainWindowViewModel.Portals` keys this off a single `_displayedPortalId`.

### 5.2 Reusable primitives (already in the repo)

| Need | Existing primitive | Location |
|------|--------------------|----------|
| Render a page region → bitmap | `BlockCropRenderer.RenderBlockAsPng(pdf, page, bbox, pageW, pageH)` | used in `MainWindowViewModel.Vlm.cs:58,92` (powers Copy-as-LaTeX) |
| Secondary, GPU-cached render of the page | the **minimap** (independent low-DPI render, mipmapped texture upload, throttled repaint, viewport indicator) | `Views/MinimapControl.axaml.cs` |
| Know the current page + block/line/role | `GetReadingPosition()` → `ReadingPosition` | `MainWindowViewModel.cs:301` |
| Be notified when page / reading position changes (UI thread, push-based) | `DocumentController.PageChanged`, `DocumentController.ReadingPositionChanged` | wired at `MainWindowViewModel.cs:273-274` |
| Enumerate detected blocks (figures/tables/equations) with bounds + role | `tab.AnalysisCache[page].Blocks` → `block.BBox` (`X/Y/W/H`), `block.Role` | hit-test precedent in `Vlm.cs:34-41` |
| Per-document sidecar persistence pattern | `AppConfig.ConfigDir` + JSON, SHA-256 keyed | `Services/CustomLayoutModel.cs:46`; annotations sidecar |

### 5.3 The sync loop
```
on ReadingPositionChanged(pos)  // pushed by Core on the UI thread
    activeSource = portals.firstOrDefault(p => matches(p.source, pos))
    if activeSource != currentActive:
        currentActive = activeSource
        if activeSource != null:
            schedulePortalRender(activeSource.target)   // debounced
```
- `matches(source, pos)` — for v1, `source.page == pos.page && source.block == pos.block`. Can widen to "source page-range" or "viewport intersects source bbox" later.
- **Debounced** like the minimap (only re-render on a *change of active source*, not per frame), so the cost is near-zero while reading within one source.

### 5.4 The render path
```
schedulePortalRender(target):
    // MUST run on the UI thread — PDFium is not thread-safe (see CLAUDE.md
    // "Never call PdfService from background threads").
    var bbox = padded(AnalysisCache[target.page].Blocks[target.block].BBox)
    var png  = BlockCropRenderer.RenderBlockAsPng(tab.Pdf, target.page, bbox, tab.PageWidth, tab.PageHeight)
    portalView.Image = decode(png)   // a plain Image control for v1 (no composition layer needed)
```
- A **static crop in an `Image`** is enough for v1 and matches Sioyek's behaviour (the figure is *visible*, it doesn't need to scroll while you read its referencing paragraph).
- A pannable/zoomable secondary viewport (its own `Camera` + composition-layer stack) is **v3**: the layers are per-visual-tree, so a detached window would need its own stack — real work, deferred.

### 5.5 Shell vs Core boundary
- **v1–v2 are shell-only.** Sidecar persistence (shell), `BlockCropRenderer` (already referenced), reading-position callbacks (already exposed), `AnalysisCache` (already exposed), a new panel + optional pop-out window (Avalonia, shell).
- **Would require Core later:** native in-PDF portal storage; a pannable secondary viewport with its own camera/region-render API; coordinate-region (non-block) sources; cross-document targets.

## 6. Phasing

**Phase 0 — prove the sync (≈1 day, all shell).**
A single hard-coded portal, no authoring UI, no persistence: a "Portals" accordion section that, when the reading position enters block N on page P, renders block M on page Q via `BlockCropRenderer` into an `Image`. Proves the `ReadingPositionChanged` → match → render path end-to-end and surfaces any debounce/PDFium-thread issues before investing further.

**Phase 1 — persistence + authoring.**
Sidecar load/save (§4); create from the Index section and the block right-click menu; a Portals management list (label, go-to-source, delete); pin/unpin when no source is active.

**Phase 2 — detach to a window (multi-monitor).**
"Pop out" the portal view into a borderless non-modal `Window`, with always-on-top; restore into the panel on close.

**Phase 3 — depth (needs Core).**
Pannable secondary viewport; native PDF storage; coordinate-region and cross-document portals.

**Shared dividend — context preview.** The same panel + sync loop implements the roadmap's automatic *context preview*: an implicit portal whose target is "the paragraph block around the current rail line, at lower zoom." Build the panel once; manual Portals and auto context-preview are two producers of the same target stream.

## 7. Risks & open questions
- **PDFium UI-thread constraint** — portal renders run on the UI thread; debounce to the change-of-active-source edge (renders are infrequent, so this is fine).
- **Block-index stability across sessions** — see §4; confirm with Core or fall back to (role+ordinal)/bbox anchoring.
- **Authoring UX** — the two-step "set source / set target" flow must feel effortless; the Index-section path is the most promising and reuses existing UI.
- **Multi-tab** — portals are per-document; swap the active set on tab change (portals follow `ActiveTab`).
- **Empty/zero-block pages** — guard `AnalysisCache` lookups (a page may be unanalyzed; portal stays inactive until analysis lands, then refreshes — reuse the existing analysis-complete invalidation).
- **Panel vs. window state** — persist the user's choice (docked vs popped-out, size, monitor) per the existing per-tab UI-state pattern (`SidePanelWidth`, minimap geometry).

## 8. Alternatives considered
- **Detach the *main* viewport** (user's initial framing). Rejected: disruptive to the primary reading experience and entangles the main composition-layer stack. A dedicated *secondary* view is cleaner and cheaper.
- **Dock.Avalonia docking.** Already evaluated and abandoned for the side panel; not revisited — Portals needs at most a single pop-out window, not a docking manager.
- **Full secondary viewport in v1.** Deferred to Phase 3; a static crop delivers the core value at a fraction of the cost.
- **Coordinate-region anchoring (Sioyek-style).** Rejected for v1 in favour of block anchoring (§5.1); can be added later for non-detected regions.

## 9. Automatic pinning (added 2026-06)

Reading a rail line that mentions **"Figure N" / "Table N"** (incl. "Fig./Tab./Figs" abbreviations and
dotted numbers like "2.1", letter suffixes like "4b") automatically pins the referenced float in the
portal preview — no manual link needed. Toggle: checkbox at the top of the Portals pane, persisted
app-wide in `ConfigDir/portal_prefs.json` (`Services/PortalPreferences.cs`), default on.

Mechanics (`TryAutoPin` in `MainWindowViewModel.Portals.cs`, `Services/ReferenceIndex.cs`):

- Runs inside `EvaluatePortals` when no *saved* portal claims the current line (saved portals always
  win). Current-line text comes from the cached `PageText` + `Rail.CurrentLineInfo` rect — no
  `GetReadingPosition` full-block extraction.
- Resolution: `Caption`-role blocks across analysed pages are parsed for a leading "Figure N:"-style
  label and associated with the nearest same-kind float block (horizontal-overlap preferred, then
  smallest vertical gap; Figure accepts `Figure`+`Chart` roles, Table accepts `Table`). Parsed labels
  are cached per page, keyed to the `PageAnalysis` instance (re-analysis rebuilds transparently).
  Lookup scans outward from the current page, preceding page first at each distance.
- The pin renders the **union of float + caption bboxes** (so the label confirms the match) and
  shares `_displayedPortalId` ("auto:Kind:number") with saved portals — same pin-until-different
  semantics; a different reference or a saved portal source takes over, nothing flaps while reading on.
- Unresolved references (caption page not analysed yet) set `_autoRefPending`; the analysis polls
  force a re-evaluation as background read-ahead delivers results. Reading the caption block itself
  never triggers a pin.
- **View lock**: the pop-out window's "Lock" toggle freezes the shown target — reading on switches
  nothing (no auto-pin, no saved-portal activation, no peek auto-dismiss) until unlocked. The lock is
  released by docking/closing the window or switching tabs (the docked pane has no unlock control, so
  it must never stay silently frozen); unlocking re-evaluates immediately so tracking catches up.
