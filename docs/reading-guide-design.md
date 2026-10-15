# Reading guide — pointer-driven word and line focus at low magnification (design)

**Status:** Phase 0 implemented on RailReaderCore branch `feat/reading-guide-phase0` (not yet merged to `main`) (`WordIndex`, `ReadingGuideHitTester`, `ReadingGuidePosition`; commit `3bbfbf8`, slated for the **unreleased** 0.65.0 — the app still pins 0.64.2; issue #136 stays open until the shell lands), 2026-10-13; Phase 1 (shell) implemented on railreader2 branch `feat/reading-guide`; **steer mode** (§10, Core 0.66.0) added 2026-10-13 after the first hands-on test
**Author:** drafted with Claude Code, 2026-10-13
**Related roadmap item:** "Make RailReader2 useful to readers who use only low magnification (1.25–2×)" — rail reading *and* help focusing on the immediately important content

---

## 1. Motivation

RailReader2's reading aids were designed around high magnification. Rail mode engages above `rail_zoom_threshold` (default 3×), frames each line with a camera snap, and the line-focus dim isolates one line of a page that, at 5×, mostly fills the viewport anyway. A reader at 1.25–2× can see the whole page width and a good part of its height. What they lack is not magnification but a **stable place-keeper** and a way to **quiet everything they are not reading right now** — the job a physical typoscope (a card with a slot) or a finger on the page does.

Rail mode does not transfer down to that zoom range. An investigation on 2026-10-13 found:

- Rail below the threshold exists only as the one-shot "start rail here" (`R`, `RailNav.ForceActivateAt`). Core clears the force flag whenever a new page's analysis is seated, so the mode dies at every page boundary.
- Below the threshold Core suppresses *all* camera motion (`RailNav.SnapSuppressed`), on the assumption that the whole page is visible. At 1.5–2× an A4 page is taller than most viewports, so the current line walks off the bottom of the screen.
- Lowering the threshold to 1.0 engages normal rail with its centring snap, which moves the page on every line — the opposite of "the layout stays where it is".

Rather than build a third rail variant, the decision (user, 2026-10-13) is a **pointer-driven reading guide**: the mouse becomes the place-keeper, the app snaps its highlight to the text structure it already knows (blocks, lines, characters), and the aperture idea (§6, Phase 2) becomes an optional dim around whatever the guide is on.

## 2. Goals / non-goals

**Goals**

- A reader at any zoom can hold **Shift** and move the pointer over the page; the **word** under the pointer and its **line** are highlighted, snapped to real text geometry, with no camera motion.
- Releasing Shift **parks** the guide where it is (page-anchored: it scrolls with the text). No click gesture is needed to pin.
- The guide is a first-class pointer **mode** with a toolbar button, a menu entry and a shortcut, so it can also be set to follow the pointer **always** for readers who cannot hold a key.
- Word highlighting is **on by default** in the mode; line highlighting reuses the existing tint/opacity/padding settings; the line-focus dim (and later the aperture) can follow the guide.
- Degrades gracefully: no layout model → lines from the text layer; no text layer → a plain line slot.
- No timers, no per-frame work beyond one layer repaint when the position changes.

**Non-goals (v1)**

- Driving the camera. The guide never scrolls or zooms; the reader does that with the wheel/keys as in browse mode.
- Confining or warping the OS pointer. "Constrained" means the *highlight* is constrained to text, not the mouse.
- Text-to-speech, sentence segmentation, keyboard stepping of the guide position (all noted as later phases).
- Touch / stylus specifics beyond "a pen that reports as a mouse works".

## 3. User experience

### 3.1 Entering and leaving the mode

- **Toolbar**: a third pointer-mode toggle beside **Browse** and **Text-Select** in `ToolBarView` (the three form a radio group, as Browse/Text-Select do today). Icon: a Lucide glyph suggesting a pointer over text (e.g. `text-cursor` / `scan-line`), converted to a `StreamGeometry` per the icon recipe.
- **Menu**: Rail ▸ **Reading Guide** (checkable), mirroring the toolbar.
- **Shortcut**: `K` — free (plain `K` and `N` are the only unbound letters; `A` is **not** free — rail mode binds WASD, `A`/`D` = line left/right in `MainWindow.TryHandleKey`, and `G` is Go-to-page). `Escape` leaves the mode like every other modal state.
- Entering the mode clears any text selection; annotation tools are unavailable while it is on (it is a pointer mode, same exclusivity as Text-Select). Left-drag still pans the page, wheel still scrolls/zooms.

### 3.2 Following (Shift held)

While Shift is held and the pointer is over a document view:

- The pointer is resolved to page space through the existing non-anchoring hover path (`ViewportPanel.ResolveHoverPoint`).
- Core's hit-tester (§5.1) returns the **line** nearest the pointer and, if the pointer is inside that line's horizontal extent, the **word** under it.
- The **mouse cursor is hidden** while following (`StandardCursorType.None`) — the highlight *is* the cursor; an arrow glyph on top of the word being read defeats the purpose. It returns on Shift release, on leaving the view, and on leaving the mode.
- Pressing Shift with the pointer stationary resolves the last known pointer position immediately (no wiggle needed).

### 3.3 Parked (Shift released)

- The guide stays exactly where it was. Its position is **page-anchored** (block/line/word indices on a page), so wheel scrolling moves the text and the highlight together.
- A parked guide survives zoom changes, page re-renders and analysis refreshes for that page. Re-resolution by index is **shell work**: Core's `ReadingGuideHitTester.Resolve` is point-based and forgets its hysteresis on a new `PageAnalysis` instance, so the shell re-reads `(BlockIndex, LineIndex, WordIndex)` against the new analysis (`Blocks[b].Lines[l]`, `WordIndex.For(analysis, text).WordsOf(b, l)`) to refresh the rects; if the indices no longer exist, the guide clears rather than pointing at the wrong line.
- Changing page clears it. Leaving the mode clears it.

### 3.4 What is drawn

| Element | Default | Source of style |
|---|---|---|
| Word under the pointer | on | Line-highlight tint at a higher opacity (≈2× `line_highlight_opacity`, capped), rounded rect, min height in device px so it is visible at 1.25× |
| Its line | on | Existing line-highlight band: `OverlayPalette.ResolveLineHighlight(tint, opacity)`, `LinePadding` |
| Everything else | off | When "Dim the other lines" (line-focus dim) is on, the page layer's dim gradient is driven by the guide line instead of the rail line (Phase 2 widens this into the aperture) |

Behavioural constraints that make it feel like a ruler rather than a jitter:

- **Vertical hysteresis.** The current line only changes when the pointer's Y leaves the current line's band expanded by a dead zone (proposed: 60 % of line height on each side); then the nearest line by centre wins. Moving along a line with an unsteady hand never flickers to a neighbour.
- **Horizontal freedom.** Inside the *block's* horizontal extent (± a small tolerance) the word under X — or the nearest word on the line — is lit. Outside it (pointer in the margin or in the gutter between columns) the line stays lit with no word — this is the "rest the hand in the margin and slide down" ruler behaviour, with no setting.
- **Line ends stick.** Past the ragged right end of a line (still inside the block) the nearest word is the last one, so it stays lit until the pointer actually moves to another line's band.
- **Column awareness.** Candidate lines are those of blocks whose horizontal extent contains the pointer X (with a tolerance); only if none qualifies does the nearest block take over. This stops a two-column page from snapping to the other column's line at the same Y.

### 3.5 Relationship to rail mode

Rail activation stays zoom-driven in Core. The guide mode is meant for below the threshold; if the reader zooms past it, rail engages as today and takes over the line visuals (the guide is hidden, not cleared); dropping back below restores the guide. Keyboard rail navigation (arrows, auto-scroll, `J`) is a rail-mode thing and is untouched. A later phase may give the guide its own keyboard stepping (§6 Phase 3).

### 3.6 Multi-viewport

Following acts on the **view under the pointer**, not `FocusedViewport` — hover never focuses a pane (by design, see Continuous Scrolling ▸ Input), and a reader may well rest the pointer on a secondary pane. Parked state is **per viewport** (`Dictionary<Viewport, GuideState>`, like freeze panes), so split panes, tear-offs and duplicate tabs each keep their own guide.

## 4. Data model & persistence

**Transient, per viewport** (never persisted):

```
ReadingGuidePosition(                 // Core record struct (RailReader.Core.Models) — carries no page
    int BlockIndex,          // index into PageAnalysis.Blocks (page-level, not navigable-subset)
    int LineIndex,           // index into block.Lines (0 for a block with no lines = whole block)
    int? WordIndex,          // index into WordIndex.WordsOf(block, line); null = margin / no word
    RectF LineRect, RectF? WordRect)   // page points, displayed frame (matches the analysis)
```

The shell's per-viewport `GuideState` wraps it with the `Page` it belongs to (and the last pointer point, for re-resolving on Shift-down with a still pointer).

**Preferences**, app-side sidecar `ConfigDir/reading_guide_prefs.json` (`Services/ReadingGuidePreferences.cs`, same `JsonSidecar` pattern as `PortalPreferences` — Core's `AppConfig` is a NuGet type we don't extend):

| Field | Default | Meaning |
|---|---|---|
| `follow` | `while_shift_held` | or `always` |
| `word_highlight` | `true` | draw the word rect |
| `dead_zone` | `0.6` | vertical hysteresis as a fraction of line height (advanced) |

Settings ▸ **Reading** gets the two user-facing controls (follow mode, word highlight) in the simple view; the dead zone is advanced-only. Line tint/opacity/padding and the dim are the existing "Current Line" controls — no duplicates.

## 5. Architecture

### 5.1 Core (RailReaderCore) — word index + hit-tester

Two small, portable, testable pieces (filed as a Core issue; see Phasing):

**`WordIndex`** — built lazily per `(PageAnalysis, PageText)` and cached on the analysis:

- Words are runs of glyphs walked in `CharBox.Index` order (over `PageText.DedupedCharBoxes`, so fake-bold duplicates drop out), broken by a whitespace character in `PageText.Text` between consecutive glyphs (boxless whitespace included), by a glyph whose vertical band no longer overlaps the previous one, or by a glyph starting more than a glyph-height to the left of the previous one (line breaks a producer emitted without whitespace). A word's rect is the union of its boxes; a ligature is one box inside its word.
- Each word goes to the block whose bbox contains its centre (smallest when blocks overlap), then to that block's line whose vertical band contains the centre, else the nearest line by centre; a word in no block is **dropped** (the guide never highlights text the layout did not claim). Per line, words are sorted by `Left`.
- Exposes `IReadOnlyList<WordSpan> WordsOf(blockIndex, lineIndex)` where `WordSpan(int Start, int Length, RectF Rect)`, plus `TextOf(word)`. `WordIndex.For(analysis, text)` is the cached entry point (`ConditionalWeakTable` keyed on the analysis instance; rebuilt if the `PageText` changes), `Build` the uncached one.

**`ReadingGuideHitTester`** — stateful per viewport (it owns the hysteresis):

```
ReadingGuidePosition? Resolve(float pageX, float pageY, PageAnalysis analysis, WordIndex? words)
void Clear()
```

- Candidate blocks: those whose bbox horizontal extent contains `pageX` ± tolerance (`ColumnToleranceFraction`, 5 % of block width); else all blocks. Non-navigable roles (page headers/footers, page numbers) are excluded via the tester's `NavigableRoles` property — which **defaults to `DefaultRoleSets.Navigable`, not the user's configured set**; the shell must assign `AppConfig.NavigableRoles` at construction and again from `OnConfigChanged`, or a role the user removed in Settings ▸ Analysis still captures the guide.
- Line choice: keep the current line while `pageY` lies inside its band expanded by `dead_zone × height`; otherwise the candidate line with the nearest centre.
- Word choice: if `pageX` is within the block's extent (± tolerance), the word whose `[Left, Right]` contains it, else the nearest word on that line; outside the block's extent, `null`. "Line ends stick" falls out of the hysteresis plus the nearest-word rule.
- Degradation: `words == null` (no text layer) → line-only positions. Lines come from whichever analyser produced the `PageAnalysis` — the ONNX models, the model-free `TextLayoutAnalyzer`, or `LineDetector`'s pixel-projection fallback for a scan with OCR off — so the hit-tester has no model dependency at all.

Cost per pointer move: a linear scan over the page's blocks and the candidate block's lines plus a binary search over one line's words — microseconds.

### 5.2 Shell (RailReader2)

- **Mode**: `MainWindowViewModel.IsReadingGuideMode` + `ReadingGuidePreferences`; `ToolBarView` third toggle; `MenuBarView` item; `MainWindow.TryHandleKey` binds `K` and the `Escape` exit; `ViewportPanel` cursor table gets the mode.
- **Input**: `ViewportPanel.OnPointerMoved` → if mode && (Shift || follow-always) → `ResolveHoverPoint(pos)` → `vm.UpdateReadingGuide(surface, page, pageX, pageY)` → controller/Core hit-test → per-viewport `GuideState`. `MainWindow` forwards Shift `KeyDown`/`KeyUp` so a press with a still pointer re-resolves and a release parks. `PointerExited` parks (and restores the cursor) without clearing.
- **Rendering**: a new `ReadingGuideLayer : CompositionLayerControl<ReadingGuideVisualHandler>` in `DocumentView`, between `SearchHighlightLayer` and `RailOverlayLayer`. State = `(camera, page, LineRect, WordRect?, tint, opacities, padding, effect)`, pushed via `SendHandlerMessage` only when the position or camera changes. Two `DrawRect` calls; no `SaveLayer`, no image filters.
- **Dim / aperture**: `DocumentView.BuildPageState` already takes `LineY/LineH` from the rail; with the guide active and rail not drawing, it takes them from the guide position instead. Phase 2 replaces the single band with the aperture description.
- **Accessibility**: `DocumentViewportAutomationPeer` can report the guide line's text as the reading position when the guide (not rail) is the active place-keeper — extraction via `PageText.ExtractTextInRect(LineRect)`, cached on the UI thread like the rail path. Optional in v1.

### 5.3 Shell vs Core boundary

Core: geometry, word segmentation, hit-testing, hysteresis — pure functions over models Core already owns, reusable by the CLI/Export and testable without Avalonia. Shell: the mode, modifier tracking, cursor, layer, preferences, settings UI. Nothing in Core knows about Shift or the mouse.

## 6. Phasing

| Phase | Where | Delivers |
|---|---|---|
| **0** | RailReaderCore | `WordIndex`, `ReadingGuideHitTester`, `ReadingGuidePosition`, unit tests (hysteresis, column awareness, margin → no word, line-end stickiness, no-text-layer path) |
| **1** | RailReader2 | The mode (toolbar/menu/`A`/Escape), Shift follow + park, hidden cursor, `ReadingGuideLayer` drawing word + line, preferences + Settings ▸ Reading controls, user-guide section, screenshot-harness shot |
| **2** | RailReader2 | **Aperture**: generalise the page layer's dim gradient to *height in lines*, *nearby-context strength*, *edge softness* (feather with a device-pixel floor — at 2× the current 8 %-of-line feather is ~2 px); clear zones clipped to their blocks' horizontal extents (column-safe); driven by the guide below the threshold and by rail above it. Block outline hidden while the aperture is active |
| **3** | both | Polish: sentence as the soft unit (punctuation-based, spanning lines), keyboard stepping of the guide in reading order with keep-in-view scrolling, a stylus button as the Shift stand-in, automation-peer reporting |

Phase 1 is useful on its own; Phase 2 is where the "help focusing on the immediately important content" goal lands.

## 7. Risks & open questions

- **Rotated views.** Block lines come from analysis re-run in the rotated frame, but `PageText.CharBoxes` are in the page's own frame. **Decided (2026-10-13): the guide runs line-only while rotated** (the shell passes `words: null`), matching the annotation lockout while rotated. Mapping boxes through `ViewRotation` is a possible later refinement.
- **Maths-heavy text.** An inline equation is a run of glyph boxes with odd spacing; whitespace splitting may yield one giant "word" or many fragments. Acceptable for v1 (the line highlight still carries the position); a max-word-width cap that splits on large gaps is a cheap refinement.
- **Hyphenation / ligatures.** A hyphenated word is two words on two lines — correct for a place-keeper. Ligature glyphs map to one `CharBox` for several characters; the index handles this since it walks boxes by `Index`, not by string position.
- **Continuous scroll.** Hover already resolves to the page under the pointer; the guide needs that page's analysis (background read-ahead usually has it). A page without analysis gets no guide and a one-line hint, not a stale one.
- **Shift collisions.** Shift+drag constrains annotation shapes — not reachable in this mode. Shift+arrows are rail-only. Shift+wheel (horizontal scroll on some platforms) should keep working: following is about pointer *moves*, so wheel events pass through untouched. **Shift chords are the real hazard**: `Shift+H/G/T/E` (semantic jumps), `Shift+O/B/I` (panes), `Shift+D` (debug overlay), `Shift+Z` (redo), `Shift+M`, `Shift+R` all begin with a Shift-down that §5.2 treats as "start following" — the parked guide would jump to wherever the pointer happens to rest, then re-park there on release. Rule: Shift-down only re-resolves when the pointer is over a document view, and a non-Shift key pressed while Shift is held cancels the follow and restores the position parked before the chord.
- **Hidden cursor edge cases.** The cursor must be restored on every exit path (Shift up, pointer exit, mode exit, window deactivation, tab switch). A stuck hidden cursor is a bug class worth a dedicated test checklist.
- **Touchpad without a mouse.** Holding Shift and dragging a finger on a touchpad is fine; a tap would be a click (pan start/end, harmless).
- **Open:** should the word highlight use the line tint or a fixed accent? Should `follow: always` hide the cursor too (probably not — the pointer then also serves normal clicking)?

## 8. Alternatives considered

- **Low-zoom rail mode** (persistent force flag, keep-in-view instead of snap, dwell-based auto-scroll). Rejected 2026-10-13: a third rail variant for a reader who doesn't want the camera moved at all, with the page-boundary and keep-in-view problems above needing Core redesign. The pointer is the natural place-keeper at low zoom.
- **Typoscope only** (page-wide dim with a slot following pointer Y). Kept as the degraded mode (no text layer) rather than a separate feature.
- **Click to pin** instead of Shift-release-parks. Rejected: adds a gesture, conflicts with browse click/drag, and every twitch moves the place-keeper while the hand is on the mouse.
- **Ctrl or Alt as the modifier.** Ctrl is free-pan drag, Ctrl+wheel zoom and the Ctrl+right-click block menu; Alt activates Avalonia menu mnemonics and focuses the menu bar on release (Windows). Shift is unclaimed for plain pointer moves.
- **Confining the OS pointer to the text.** Hostile, platform-specific, and Avalonia offers no portable API for it.

## 10. Steer mode — the invisible joystick (added 2026-10-13)

**Why.** The first hands-on test of Phase 1 (point mode) found it works but is not a natural way to *read*: the pointer maps to a position, so the reader must track the text with the mouse and make the diagonal "return sweep" at every line end. Two requests came out of it — snap to the next line past the last word, and "an invisible virtual joystick around the highlighted word, like some mobile apps: moving the cursor to the left initiates a horizontal scroll and keeps it going". Both are the same change: make the pointer a **throttle**, not a position. Decisions (user, 2026-10-13): steer is the **default**, keep-in-view scrolling is accepted, vertical deflection steps lines, pace starts from the reading-pace presets.

**Behaviour.**

- **Shift-down plants the stick** at the pointer. If nothing is parked on this page the guide is first seated on the word under the pointer; if a guide is parked, reading continues from the park — the pointer's position means nothing in steer mode except as the stick's origin.
- While Shift is held, the pointer's displacement from the origin is a throttle. Inside a **dead zone** (28 px at the default sensitivity; was 16 px — a hand resting on the mouse drifted past it) nothing moves. Beyond it the **larger axis wins**: rightward displacement advances the guide word by word along **reading order** — wrapping to the next line's first word, into the next navigable block, and across the page break (the host turns the page and seats the first word once its analysis is in); leftward reverses; vertical steps lines (landing on the line's first word). The rate ramps quadratically from a minimum at the dead-zone edge to a maximum at 260 px (was 160), like hold-to-scroll; the pace at the dead-zone edge is half the preset's slow end so a nudge starts gently. Both distances scale with **Settings ▸ Reading ▸ Reading Guide ▸ Steer sensitivity** (advanced; `SteerSensitivity`, 1 = default, lower is calmer). Return to the dead zone and it stops exactly where it is.
- **Pace is in characters per second**, so a long word dwells longer than a short one and the rhythm feels even; a word ending a sentence (`. ! ? : ;`) dwells six characters' worth longer; a one-letter word dwells like a two-letter one. The pace comes from the reading-pace settings (Relaxed / Normal / Brisk, or the raw hold-to-scroll speeds): min = 0.4 × scroll-start, max = 0.4 × scroll-max chars/s → Relaxed ≈ 4–11, Normal ≈ 5.6–17, Brisk ≈ 8–24. No new pace setting.
- **Keep-in-view** (the one departure from §2's "never scrolls"): when the guide word leaves the middle band of the viewport (18–82 % vertically, 8–92 % horizontally) the camera eases it back inside by the minimum amount — exponential ease, ~140 ms time constant, clamped to the page — and never recentres, so the page stays still while reading inside the band. Not applied to a frozen view (zoom-locked).
- **Release Shift to park. Click a word to re-seat** (the jump gesture; in guide mode a plain click no longer follows links or frames blocks — leave the mode to do that). The pointer glyph is hidden while steering, as in point mode — so a small **steer indicator** is drawn instead (`SteerIndicator`, `ReadingGuideLayer`, screen space): a faint ring marking the dead zone where Shift went down, a knob where the hidden pointer is (hollow in the dead zone, filled once pushing), and a line between them; redrawn each frame while the stick is planted, gone on release. Added after a test found steering with no visible reference disorientating.
- Document end / start, or a confined (portal) view: the stick lets go rather than spinning.

**Point mode stays** as "Point at the text (hold Shift)" and "…always (no key)", with two refinements from the same test: a **carriage return** (resting on the last word of a line, moving to the next line in reading order lands on its *first* word, not the word under the still-far-right pointer — Core 0.66.0's hit-tester) and a **return-sweep gate** (a leftward move faster than 1.2 px/ms holds the current word instead of lighting every word it crosses backwards — shell).

**Where it lives.** Core 0.66.0: `ReadingGuideFlow` (reading-order stepping: `StepWord` / `StepLine` / `First` / `Last` / `At` / `IsNextLine`, null at the page boundary) and `ReadingGuideSteer` (the stick: `Engage` / `SetPointer` / `Tick(dt, currentWordChars, sentenceEnd)` → at most one signed word or line step per frame; dead zone, ramp, dwell, axis lock), both pure and unit-tested. Shell: `GuideState.Steer` per viewport; `SetGuideShiftHeld` engages the stick where the pointer rests over a view; `TickReadingGuide(dt)` runs from `RunAnimationFrame` only while a stick is engaged (no timers) and applies steps, page turns (`DocumentModel.GoToPage(vp, …)` + a `PendingSeat` resolved in `RefreshGuidesAfterAnalysis`) and keep-in-view easing; `SeatGuideAt` is the click. `ReadingGuideFlow.At` replaced the shell's own index re-anchor.

**Open after this round:** whether the stick should keep its throttle when the pointer leaves the viewport (it does today, until Shift is released); whether line stepping should keep the current word column rather than landing on the first word; a short dwell on entering a new block (paragraph pause).

## 11. Steering the rail — the same stick above the threshold (Core 0.67.0, app 3.73.0.0)

**Why.** The first hands-on test of steer mode (§10) was good enough that the user asked whether the stick should drive rail mode too. It should: rail already has the engine the stick needs — a line plus a continuous camera position along it, hold-to-scroll with a time-ramped speed, edge-hold line advance (forward at the line's end, backward at the framed unit's left edge with a snap-to-line-end), semi-auto flow that parks at non-prose, page turns — and the stick is a better *input* to that engine than a held arrow key. The reader then has one physical habit from 1.25× to 8×: seat, hold Shift, nudge. Decisions (user, 2026-10-13): continuous pan (not word-stepped) first, one mode (`K`) covering both regimes. The rail word marker was first planned as an off-by-default option; the first rail test ("it feels very natural… would be nice to have the word highlight in rail mode too") made it follow the existing word-highlight setting (on by default).

**Behaviour (rail active and drawing — `Rail.VisualsVisible`).**

- `K` is the single "steer with the pointer" mode. Below the rail threshold it moves the word highlight as in §10. Above it (or with "start rail here" active below it) the same Shift-and-nudge drives the rail; the guide layer stays hidden, the rail's own line highlight / dim / overlay are what the reader sees.
- **Shift-down plants the stick** at the pointer. Planting stops auto-scroll (`P`) if it is running — the stick supersedes it, as A/Left does today — and cancels any hold-scroll.
- **Horizontal deflection sets the rail's scroll velocity** along the line: `v = sign · (ScrollSpeedStart + (ScrollSpeedMax − ScrollSpeedStart) · t²)` page-px/s, where `t` is the stick's deflection (0 at the dead-zone edge, 1 at full deflection) — the same numbers and the same quadratic shape as hold-to-scroll, driven by deflection instead of hold time. Integrated per frame with `dt`, clamped and pixel-snapped exactly like hold-to-scroll (`ClampX`/`SnapX`). Return to the dead zone and the camera stops where it is. `RailNav.ScrollSpeed` reflects the steered speed so motion blur and the speed readout keep working.
- **Line ends reuse edge-hold.** Pinned against the line's end (forward) or the framed unit's left edge (backward), the existing `EdgeHoldStateMachine` accumulates the hold and fires the advance: `AdvanceLine` + `StartSnap` (forward) / `StartSnapToEnd` (backward), page turn at the page boundary — the controller's `HandleEdgeAdvance` path unchanged. The post-advance input suppression (`ShouldSuppressAfterEdgeAdvance`) applies to the stick too: the steered velocity is ignored until the snap lands, so a held deflection doesn't pan the just-flipped line.
- **Vertical deflection steps lines** like key repeat (Core 0.67.0, after the first rail test found a downward drift jumped several lines): one step the moment the stick leaves the dead zone vertically, then a 0.5 s hold, then repeat at 0.6–2 lines/s by deflection (the word guide repeats at 0.8–3). Each step is ↑/↓ (`AdvanceLine` + snap) on the viewport under the pointer. A deliberate step never parks the stick (nor do ↑/↓ park semi-auto scroll).
- **Non-prose units park the stick.** A steered *forward flow* whose edge advance enters a stop unit — a new chunk, a block in `AutoScrollStopClasses` (equations, tables, headings; figures are not navigable by default so the rail skips them), or a new page — parks: the stick ignores its deflection until the pointer returns to the dead zone and deflects again. That re-push is the gesture the reader is already making and replaces D/S for a steering reader. Pure prose flows through line ends without a re-push, only the per-line edge-hold beat.
- **Release Shift** → the rail stays where it is (it has no "park" concept beyond that). **Click** seats the rail at the clicked block/line (`HandleClick`'s rail branch, already the behaviour). Pointer glyph hidden while steering, Shift chords cancel, `Deactivated` releases — all as §10.
- **No keep-in-view**: the rail's own framing (snap, `VerticalBias`, soft edge clamp) owns the camera.
- **A virtual reading point, centred.** The stick carries a reading point along the line at the steered speed and the camera *eases to keep it centred* where the line's framing allows, staying pinned where it doesn't — at the line's start (where the snap landed: the head of a line is read with the camera still) and at the line's end (the line's end at the window's right edge, the stop auto-scroll uses: the tail is read with the camera pinned). The edge-hold beat begins only when the point reaches the line's edge, so no word is skipped at either end — the first rail test found a fixed reading point advanced to the next line before the last words were reached; the second asked for the view to be centred on the highlighted word. **Shift-down with the pointer resting on a word of the current line reads from that word** (`TrySeatSteerReadingPointAt`); elsewhere the stick continues from where the rail is. **A click** seats the rail's line as before and the reading point lands on the clicked word (`SeatSteerReadingPoint`, applied on the next tick). A new line under the stick restarts at its near end. Core: `RailNav.SteerReadingPointPageX` / `SteerReadingPointFraction` (0.5) / `SteerFollowTauSecs` (0.12).
- **Rail word marker** (the existing `WordHighlight` setting, default on): while the stick is engaged, the word under the steered reading point is drawn with the guide's word mark — a thin rounded **outline** since the first rail test (a fill over the glyphs cost too much contrast; applies below the threshold too) — a moving anchor for the eye through head, middle and tail. Redrawn every steer tick (not only on camera moves, or it froze whenever the camera was pinned — the second test's "not reliably highlighting words near the end"). Uses `WordIndex` + the current line's `WordSpan`s, no new geometry. Not drawn once Shift is released.

**Not steerable:** a confined (portal) view (the stick lets go at its block edge, like edge-hold's `ConfinedHold`); a frozen (zoom-locked) view's body is already clamped, so the stick drives it within the clamp; jump mode (`J`) is ignored by the stick; free pan (Ctrl+drag) is a Shift chord → cancel.

**Where it lives.**

- **Core 0.67.0 (shipped as designed).** `RailNav.SetSteerThrottle(t)` / `Steering` / `SteerParked` / `ParkSteer()` (`Services/RailNav.Steer.cs`) and a `TickSteer` branch in `RailNav.Tick` beside `TickScrollHold`, mutually exclusive with hold-scroll and auto-scroll (engaging stops a held arrow scroll; `StartScroll` / `StartAutoScroll` / `Deactivate` clear the stick); it advances the virtual reading point by `SteerSpeed` × `dtSecs`, eases the camera toward framing it at `SteerReadingPointFraction` within [line end at the right edge, the snap's line-start framing] (`ClampX`/`SnapX`), sets `ScrollSpeed`, holds while a snap is in flight, and drives the edge-hold machine (`OnEdgeHit` once the point reaches the line's edge, `OnMoved` otherwise) so the pending edge-advance reaches the controller through the existing channel. `HandleEdgeAdvance` parks the stick on a page change or when `ShouldParkOnLineAdvance` says the forward advance entered a stop unit; the park clears when the throttle returns to 0. Per-viewport controller entry points — `SteerRail(Viewport vp, double throttle)` (stops auto-scroll on engage) and `StepRailLine(Viewport vp, bool forward)` — because the arrow handlers act on `FocusedViewport` and the stick acts on the view under the pointer. Tests: `RailNavSteerTests` (injected edge-hold clock) + `RailSteerControllerTests` (incl. the park and the re-push).
- **Shell (small, as designed).** `GuideState.RailSteering`; `EngageSteer` plants without seating when `Rail.VisualsVisible` (and stops auto-scroll); `TickRailSteer` feeds `SteerRail(vp, Axis == Horizontal ? Throttle : 0)` every frame and `StepRailLine` per `LineSteps`; `ReleaseRailSteer` sends throttle 0 on Shift-up / chord cancel / mode off / rail gone. `SeatGuideAt` returns false at rail zoom so the click falls through to the rail's own seat. `ReadingGuideSteer` reused as-is. `ReadingGuideLayer` accepts a word-only state; `BuildGuideState` draws the rail word marker (`WordHighlight` pref) from `TryGetRailWordMarker`.

**Open questions.** Whether the rail's `ScrollSpeedStart` at the dead-zone edge feels too fast for fine positioning at 8× (hold-to-scroll starts there too, so probably not; a lower floor is one constant). Whether the word-stepped pan variant (camera eases word by word) is worth adding behind a pref once the plumbing exists. Whether the edge-hold beat (400 ms hold + 300 ms cooldown) is the right per-line rhythm for a steered flow, or should shorten with deflection.
