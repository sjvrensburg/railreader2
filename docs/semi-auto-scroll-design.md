# Semi-automatic rail scroll (design)

**Status:** Implemented, 2026-06-16. RailReaderCore 0.32.0 (state machine + config) + desktop wiring (config UI, `AutoScrollParked` plumbing, parked key routing, "parked — press D" affordance) landed.
**Author:** drafted with Claude Code, 2026-06-16
**Supersedes:** the fully-automatic auto-scroll ("dwell") mode — this *replaces* it on the same `P` toggle.
**Core tracking issue:** [sjvrensburg/RailReaderCore#60](https://github.com/sjvrensburg/RailReaderCore/issues/60).

---

## 1. Motivation

Rail mode's fully-automatic auto-scroll is, at its core, a machine that **guesses reading speed** and inserts pauses to match. Over time it accumulated three overlapping guess-mechanisms in `AutoScrollStateMachine.cs` (RailReaderCore):

1. **Full-block dwell** — a narrow block that fits the viewport is held for `AutoScrollLinePauseMs × BlockLineCount`.
2. **Per-line reading beat** — fit-in-window lines get `Max(LinePauseMs, LineReadBudgetMs)`, where the budget scales with content length between `MinLineReadMs` (350 ms) and `MaxLineReadMs` (1200 ms).
3. **Block-end settlement** — `Max(readingBeat, AutoScrollBlockPauseMs)`, with role-based overrides (`AutoScrollEquationPauseMs`, `AutoScrollHeaderPauseMs`).

That is five-plus tunable millisecond knobs all approximating one unknowable quantity: *how long does this reader need on this content right now?* For dense statistical / mathematical PDFs the variance is enormous — a one-line lemma and a three-line integral need wildly different dwell, and no fixed `AutoScrollEquationPauseMs` is right for both. The problem is **structurally unsolvable by a timer**, which is why the dwell mode never feels trustworthy on real papers.

**Semi-automatic scroll** splits the problem along the line where the variance actually lives:

- The **mechanical, low-variance** part — panning a wide line, pacing a run of uniform prose — stays automatic, driven by a single speed knob.
- The **cognitive, high-variance** part — "am I done with this equation / table / figure / section?" — is handed back to the reader as an explicit keypress.

You automate what a timer *can* do and gate what only a human can decide.

## 2. Goals / non-goals

**Goals**
- Auto-flow through continuous prose (across paragraph breaks) at one user-set speed.
- **Park** (stop and wait for a keypress) on arrival at a non-prose unit — display equation, algorithm, table, figure/chart, heading/title — and at column (chunk) and page boundaries.
- Resume with the existing forward/down advance keys (**D** and **S**, and their arrow equivalents).
- Keep pan/zoom/inspect (Ctrl+drag free-pan) fully live while parked, so a parked equation can be studied for as long as the reader likes.
- **Delete** the dwell guess-subsystem outright — net subtraction from Core.

**Non-goals**
- Stopping at every paragraph (rejected — prose flows through; see §9).
- A content-aware / adaptive timer of any kind (the whole point is to stop guessing).
- A hands-free mode. Semi-auto is, by definition, keypress-gated. (Hold-to-flow could be added later — §9 — but is out of scope for v1.)
- Any change to manual rail navigation (`J`, arrows, semantic jumps) — those are untouched.

## 3. Behaviour specification

### 3.1 The rule, in one sentence
Auto-flow (with a simplified per-line beat) through everything until you **enter a stop unit** — a non-prose block, a new column/chunk, or a new page — then **park and wait for an advance key**. Prose never parks; it flows across paragraph breaks.

### 3.2 Stop units (where it parks)
A line advance triggers a **park** when the newly-entered unit satisfies any of:

1. **Page change** — natural reading break.
2. **New chunk** — a column/section boundary (`CurrentChunk` changed; chunks are the existing maximal-run-of-blocks-in-a-column unit from `RailNav.Chunks.cs`).
3. **Stop-role block** — the new current block's `BlockRole` is in the configured stop set. Default stop set:

   | Park (stop role) | Flow through (prose) |
   |---|---|
   | `Heading`, `Title` | `Text` |
   | `DisplayMath`, `Algorithm` | `Caption`, `Aside` |
   | `Table`, `Figure`, `Chart` | `Reference`, `Footnote` |

   Page furniture (`Header`/`Footer`/`PageNumber`/`Decoration`) is non-navigable and never reached. `Caption` flows through deliberately — you have already parked on the figure it describes.

The stop-role set is **configuration-derived**, not hard-coded, so toggling (e.g.) headings out of the stop set later is a one-value change (see §6).

### 3.3 Park-on-entry, one stop per unit
Parking happens **on entry** into a stop unit, exactly once:

```
¶ Para 1 ──auto──┐
¶ Para 2 ──auto──┤  (no stop — prose flows across the paragraph break)
∑ Equation ──────▶  [PARK — press D]
                    (press D → flow resumes; multi-line equation scrolls through, then…)
¶ Para 3 ──auto──▶  (no stop — leaving the equation into prose flows)
§ Heading ───────▶  [PARK — press D]
```

- Entering the equation parks once; the reader studies it freely (pan/zoom stays live).
- Pressing **D/S** resumes flow. Within a multi-line stop block, the remaining lines flow on the speed knob (the reader has signalled "done"); they are **not** individually gated.
- Leaving a stop block into prose does **not** park — prose flows.

This yields exactly one keypress per equation/table/figure/heading/column/page, which is what keeps keypress load tolerable. (The earlier "stop at every paragraph" option was rejected precisely because it multiplied keypresses without buying control where the variance lives.)

### 3.4 Intra-flow pacing (the retained, simplified per-line beat)
Inside a flow segment, prose still advances line-by-line on a timer so reading stays hands-light. The beat is reduced from the 350→1200 ms `LineReadBudgetMs` content-scaling (and the `Max(...)` juggling) to a single flat knob held at **every** line end before advancing — **`AutoScrollLinePauseMs`**, wide lines included.

> **Revised (RailReaderCore 0.32.1).** The original 0.32.0 design gave a *wide* line (one needing horizontal scroll) a beat of `0` ("the scroll travel *is* the pacing") and applied the beat only to fit-in-window lines. In practice that read as abrupt: at high magnification most prose lines are wider than the viewport, so they got no beat and the `AutoScrollLinePauseMs` knob had no effect, and a wide line snapped straight back to the next line's start (a full-width carriage-return) with no rest. The beat now fires on every line so each carriage-return is preceded by a brief rest. Set `AutoScrollLinePauseMs = 0` to advance immediately. The vertical/carriage-return *motion* itself is governed by the existing `SnapDurationMs` knob (Settings → Rail Reading) — raise it to soften the move.

One knob (`AutoScrollLinePauseMs`) governs all intra-flow cadence. No content-awareness anywhere.

### 3.5 Speed, boost, and the hold-to-start trigger
- The single speed knob is the existing `ScrollSpeedStart/Max` ramp; `[ ` / ` ]` still adjust it live via `UpdateSpeed`.
- **Boost** (hold D/Right to temporarily double speed) is retained *during flow*. While **parked**, the same keys mean **resume** — the meaning is disambiguated by state (see §5.2).
- The hold-forward-to-start trigger (`AutoScrollTriggerEnabled` / `AutoScrollTriggerDelayMs`) is retained: holding forward at a line end for the delay still *starts* semi-auto.

## 4. State model

The dwell-era state machine had five states with timer-driven dwell/pause transitions. Semi-auto collapses them:

```
Inactive ──Start()──────────────────────────────→ Scrolling
Scrolling ──reached line end, flow continues────→ (brief Paused for the per-line beat) → Scrolling
Scrolling ──reached line end, enters stop unit──→ WaitingForSnap → WaitingForAdvance  [parks]
WaitingForAdvance ──D/S pressed──────────────────→ Scrolling
Any ──Stop()─────────────────────────────────────→ Inactive
```

- `Dwelling` is **removed**.
- `Paused` survives only as the **short, timer-driven per-line beat** (`AutoScrollLinePauseMs`), never as a block/role dwell.
- `WaitingForAdvance` is **new**: an indefinite park with no timer, exited only by an explicit resume call. It is entered *after* the entry snap completes (reusing the existing deferred-pause-after-snap machinery) so the parked frame is the settled, centred target.

## 5. Component responsibilities

### 5.1 RailReaderCore (the bulk — mostly deletion)
The park *decision* needs role/chunk/page knowledge, which lives in the orchestrator (`DocumentController.Animation.cs::TickAutoScroll`), not the state machine. So:

- **`AutoScrollStateMachine`**: add `WaitingForAdvance` + `RequestDeferredPark()` / `ResumeFromPark()`; gut `TickScrolling` down to the §3.4 two-case beat; delete `Dwelling`, `_dwelt`, the full-block dwell branch and the block-end settlement branch.
- **`RailNav.AutoScroll.cs`**: drop the dwell/budget fields from `AutoScrollContext` (`LineReadBudgetMs`, `BlockEndPauseMs`, `RawBlockWidthPx`, `BlockLineCount`); expose `ParkAutoScroll()` / `ResumeAutoScrollFromPark()` and an `AutoScrollParked` flag.
- **`DocumentController.Animation.cs`**: after `AdvanceLine`, compute `shouldPark = pageChanged || enteredNewChunk || IsStopRole(newRole)` and call `ParkAutoScroll()` instead of `PauseAutoScroll(GetBlockEntryPause(...))`. Delete `GetBlockEntryPause`.
- **Config**: see §6.

Full Core change is specified in the tracking issue (§8).

### 5.2 RailReader2 (desktop — thin)
- **`MainWindow.axaml.cs`** key routing: when `AutoScrollParked`, route D/S/Right/Down to `ResumeFromPark` instead of boost/manual-nav; otherwise unchanged. `P` still toggles (now semi-auto).
- **`MainWindowViewModel`**: surface an `AutoScrollParked` observable; reflect it in `StatusBarView`/overlay.
- **Affordance**: a clear "**parked — press D to continue**" hint (status bar + a small viewport overlay) so a stop never reads as a freeze. This is the single most important UX detail — without it the mode looks broken.
- Free-pan (Ctrl+drag) while parked already composes via the existing temporary-rail-exit + snap-back; verify it returns to the parked state, not to flow.

## 6. Configuration changes

**Removed** (CoreSettings + AppConfig): `AutoScrollBlockPauseMs`, `AutoScrollEquationPauseMs`, `AutoScrollHeaderPauseMs`, and the internal `LineReadBudgetMs` / `MinLineReadMs` / `MaxLineReadMs` tuning.

**Retained:** `AutoScrollLinePauseMs` (now the sole intra-flow cadence knob), `AutoScrollTriggerEnabled`, `AutoScrollTriggerDelayMs`, `ScrollSpeedStart/Max`, `ScrollRampTime`, `SnapDurationMs`, `DefaultAutoScrollSpeed`.

**Added:** `AutoScrollStopClasses` — the set of `BlockRole`s that park (default: `Heading`, `Title`, `DisplayMath`, `Algorithm`, `Table`, `Figure`, `Chart`). Lets a user drop headings from the stop set without a code change.

Removing public `CoreSettings` fields is a breaking Core API change — the Core bump and the desktop update land together (test against an unreleased Core via the local-pack flow before NuGet indexing).

## 7. Resolved & open decisions

**Resolved**
- Prose granularity: **flow through prose**, park only at content-type changes + column/page ends (not per-paragraph, not per-chunk-only).
- Headings: **park** on them (config-derived so it's reversible).
- Replace fully-auto entirely; reuse `P` and the D/S advance keys.
- Keep the per-line beat but simplify to a single flat knob held on **every** line end (revised in 0.32.1 from the original "beat 0 on wide lines" — see §3.4).

**Open / deferred**
- **Hold-to-flow** (hold D → glide through stop boundaries, release → stop at next): a natural later addition if keypress load proves high; not in v1.
- **Optional auto-continue timeout** while parked: deliberately *not* added — it would reintroduce dwell-guessing and muddy the mental model.
- Whether `Caption`/`Reference`/`Footnote` should ever park: defaulting to flow-through; revisit from real use.

## 8. Rollout

1. Land the Core change behind the existing `P` toggle (issue §8 / tracking issue).
2. Bump RailReaderCore; update desktop config + key routing + parked affordance in lockstep.
3. The author dog-foods it on real papers for ~a week.
4. The risk of removing fully-auto is low while RailReader2 is effectively single-user (pre-macOS / pre-ProductHunt). Revisit defaults before the public launch.

## 9. Why not the alternatives

- **Keep dwell, tune harder** — the variance is unbounded; no fixed timer wins. Rejected as structurally unsolvable.
- **Stop at every paragraph** — multiplies keypresses without adding control where it matters (prose is uniform). Rejected.
- **Stop only at chunk boundaries** — would scroll straight past an inline display equation inside a column without parking. Rejected; content-type changes must park.
- **Adaptive timer (track scroll history, learn pace)** — same guessing problem wearing a hat; high complexity, low trust. Rejected.

## 10. Testing

Core unit tests (mirroring the existing `AutoScrollStateMachine` tests, which inject `GetScrollElapsedSeconds`):
- Prose run flows across paragraph (block) boundaries within a chunk **without** parking.
- Entering a `DisplayMath`/`Table`/`Figure`/`Heading` block parks (state → `WaitingForAdvance`); `ResumeFromPark` returns to `Scrolling`.
- New chunk and page change each park.
- Multi-line stop block: parks once on entry; after resume, remaining lines flow without re-parking.
- Per-line beat: wide line → 0 beat; fit-in-window line → `AutoScrollLinePauseMs`.
- `AutoScrollStopClasses` honoured (drop `Heading` → no park on headings).
