# Reading guide — pointer-driven word and line focus at low magnification (design)

**Status:** Phase 0 implemented in RailReaderCore 0.65.0 (`WordIndex`, `ReadingGuideHitTester`; issue #136), 2026-10-13; Phases 1–3 (shell) pending
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
- **Shortcut**: `A` — free (plain `A`, `K`, `N` are the unbound letters; `G` is Go-to-page), and it sits under the left hand that also holds Shift. `Escape` leaves the mode like every other modal state.
- Entering the mode clears any text selection; annotation tools are unavailable while it is on (it is a pointer mode, same exclusivity as Text-Select). Left-drag still pans the page, wheel still scrolls/zooms.

### 3.2 Following (Shift held)

While Shift is held and the pointer is over a document view:

- The pointer is resolved to page space through the existing non-anchoring hover path (`ViewportPanel.ResolveHoverPoint`).
- Core's hit-tester (§5.1) returns the **line** nearest the pointer and, if the pointer is inside that line's horizontal extent, the **word** under it.
- The **mouse cursor is hidden** while following (`StandardCursorType.None`) — the highlight *is* the cursor; an arrow glyph on top of the word being read defeats the purpose. It returns on Shift release, on leaving the view, and on leaving the mode.
- Pressing Shift with the pointer stationary resolves the last known pointer position immediately (no wiggle needed).

### 3.3 Parked (Shift released)

- The guide stays exactly where it was. Its position is **page-anchored** (block/line/word indices on a page), so wheel scrolling moves the text and the highlight together.
- A parked guide survives zoom changes, page re-renders and analysis refreshes for that page (re-resolved by index; if the page's analysis is replaced and the indices no longer exist, the guide clears rather than pointing at the wrong line).
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
ReadingGuidePosition(
    int Page,
    int BlockIndex,          // index into PageAnalysis.Blocks (page-level, not navigable-subset)
    int LineIndex,           // index into block.Lines
    int? WordIndex,          // index into the line's word list; null = margin / no word
    RectF LineRect, RectF? WordRect)   // page points, rotation-aware display frame
```

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

- Words are maximal runs of non-whitespace characters in `PageText.Text`, mapped to their `CharBox`es by `CharBox.Index`; a word's rect is the union of its boxes. A run with no boxes (synthetic whitespace, control chars) is skipped.
- Each word is assigned to the block line whose vertical band contains the word's centre and whose block bbox contains its centre X; words matching no line attach to the nearest line of the containing block. Per line, words are sorted by `Left`.
- Exposes `IReadOnlyList<WordSpan> WordsOf(block, line)` where `WordSpan(int Start, int Length, RectF Rect)`.

**`ReadingGuideHitTester`** — stateful per viewport (it owns the hysteresis):

```
ReadingGuidePosition? Resolve(float pageX, float pageY, PageAnalysis analysis, WordIndex? words)
void Clear()
```

- Candidate blocks: those whose bbox horizontal extent contains `pageX` ± tolerance; else all blocks. Non-navigable roles (page headers/footers, page numbers) are excluded by the same `NavigableRoles` set rail uses, so the guide never lands on furniture.
- Line choice: keep the current line while `pageY` lies inside its band expanded by `dead_zone × height`; otherwise the candidate line with the nearest centre.
- Word choice: if `pageX` is within the block's extent (± tolerance), the word whose `[Left, Right]` contains it, else the nearest word on that line; outside the block's extent, `null`. "Line ends stick" falls out of the hysteresis plus the nearest-word rule.
- Degradation: `words == null` (no text layer) → line-only positions. Lines come from whichever analyser produced the `PageAnalysis` — the ONNX models, the model-free `TextLayoutAnalyzer`, or `LineDetector`'s pixel-projection fallback for a scan with OCR off — so the hit-tester has no model dependency at all.

Cost per pointer move: a linear scan over the page's blocks and the candidate block's lines plus a binary search over one line's words — microseconds.

### 5.2 Shell (RailReader2)

- **Mode**: `MainWindowViewModel.IsReadingGuideMode` + `ReadingGuidePreferences`; `ToolBarView` third toggle; `MenuBarView` item; `MainWindow.TryHandleKey` binds `A` and the `Escape` exit; `ViewportPanel` cursor table gets the mode.
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
- **Shift collisions.** Shift+drag constrains annotation shapes — not reachable in this mode. Shift+arrows are rail-only. Shift+wheel (horizontal scroll on some platforms) should keep working: following is about pointer *moves*, so wheel events pass through untouched.
- **Hidden cursor edge cases.** The cursor must be restored on every exit path (Shift up, pointer exit, mode exit, window deactivation, tab switch). A stuck hidden cursor is a bug class worth a dedicated test checklist.
- **Touchpad without a mouse.** Holding Shift and dragging a finger on a touchpad is fine; a tap would be a click (pan start/end, harmless).
- **Open:** should the word highlight use the line tint or a fixed accent? Should `follow: always` hide the cursor too (probably not — the pointer then also serves normal clicking)?

## 8. Alternatives considered

- **Low-zoom rail mode** (persistent force flag, keep-in-view instead of snap, dwell-based auto-scroll). Rejected 2026-10-13: a third rail variant for a reader who doesn't want the camera moved at all, with the page-boundary and keep-in-view problems above needing Core redesign. The pointer is the natural place-keeper at low zoom.
- **Typoscope only** (page-wide dim with a slot following pointer Y). Kept as the degraded mode (no text layer) rather than a separate feature.
- **Click to pin** instead of Shift-release-parks. Rejected: adds a gesture, conflicts with browse click/drag, and every twitch moves the place-keeper while the hand is on the mouse.
- **Ctrl or Alt as the modifier.** Ctrl is free-pan drag, Ctrl+wheel zoom and the Ctrl+right-click block menu; Alt activates Avalonia menu mnemonics and focuses the menu bar on release (Windows). Shift is unclaimed for plain pointer moves.
- **Confining the OS pointer to the text.** Hostile, platform-specific, and Avalonia offers no portable API for it.
