# AI-driven GUI-use recording + Demo DSL — Design & Implementation Plan

> Status doc for resuming across sessions. Last updated 2026-06-07.
> Companion agent memory: `demo-video-dsl` (auto-loaded via MEMORY.md).

## 1. Goal

Script **faithful demonstration videos** of the real RailReader2 GUI — especially
smooth zoom onto a specific detected text block — by driving the *running app* and
recording the *real on-screen window*. The output must be exactly what a user sees.

## 2. The non-negotiable invariant

Every action animates through the same path real user input uses
(`MainWindowViewModel` → `DocumentController.Tick` → `RequestAnimationFrame`),
rendered by the real on-screen window. We only change **how a command is issued and
when we cut** — never how motion is computed or drawn.

Why: headless rendering is *too smooth* and otherwise diverges from the real app, so
it is misleading. Capturing the live window guarantees fidelity by construction.

## 3. Decisions (finalized)

| Topic | Decision |
|---|---|
| Capture | Real on-screen window (NOT headless) |
| Command transport | **D-Bus** (session bus), Linux-first |
| Control surface | Behind a **C# interface** so it is testable without a live bus and portable to other IPC/OS later; the D-Bus server is a thin adapter |
| Runner | A **`railreader2-cli demo <script>`** subcommand |
| Recorder | **GNOME screencast portal** (most faithful; one prompt) |
| Cut sync | **Event-synced** on real `Settled` transitions ("close enough" accepted) |
| Pointer | DSL option `cursor: hidden \| park \| follow` (cosmetic; never affects camera) |
| Zoom duration | App-native **180 ms** cubic ease-out only (no custom durations) |
| Block framing | Match **rail's exact framing** (centre narrow chunks, left-align wide with 5% inset) |
| Core changes | Allowed; **shipped in RailReaderCore 0.20.0** |

## 4. Layered architecture

```
        ┌─────────────────────────── RailReader2 (Avalonia GUI app) ───────────────────────────┐
        │  MainWindowViewModel  ──wraps──>  DocumentController (Core)                            │
        │        ▲ implements                                                                    │
        │   IRailReaderControl   ← verbs + queries + events; OS/IPC-agnostic, bus-free testable  │
        │        ▲ adapts                                                                         │
        │   DBusControlServer (Tmds.DBus.Protocol, Linux)  ← thin adapter, zero app logic        │
        └────────────────────────────────────────────────┬───────────────────────────────────────┘
                                                          │ D-Bus (org.railreader.Control1)
        ┌─────────────────────────── railreader2-cli demo ┴───────────────────────────┐
        │  DSL parser (pure)  →  Sequencer  →  IControlClient (D-Bus client)           │
        │                                   →  Recorder (GNOME portal)  →  ffmpeg      │
        └─────────────────────────────────────────────────────────────────────────────┘
```

### App side (RailReader2)
- **`IRailReaderControl`** — the portable contract. Methods (verbs), queries (state),
  and events (signals). Lives in the app (or a tiny `RailReader2.Control.Abstractions`
  assembly if the cli needs the C# types; the cli only needs the D-Bus wire contract,
  so the interface can stay app-internal).
- **`ViewModelControl : IRailReaderControl`** — the only real implementation. Marshals
  every verb to the UI thread (`Dispatcher.UIThread.Post`) onto `MainWindowViewModel`,
  reads state from `DocumentController`, and raises events from the animation loop.
- **`DBusControlServer`** — registers `org.railreader.Control` on the session bus and
  forwards each D-Bus method/property/signal to/from `IRailReaderControl`. Contains no
  app logic, so it is trivially swappable (named pipe / gRPC for Windows) and the app
  logic is tested via `IRailReaderControl` directly, no bus required.

### Runner side (railreader2-cli demo)
- **DSL parser** — pure function: YAML/text → ordered command list. Unit-testable.
- **Sequencer** — issues verbs via `IControlClient`, waits on `Settled` between
  keyframes, applies `hold` dwell, drives the recorder, moves the cursor for `follow`.
- **`IControlClient`** — transport seam (D-Bus client). A fake implementation lets the
  sequencer be tested without a running app or bus.

## 5. D-Bus contract (target)

```
Bus name:  org.railreader.Control
Object:    /org/railreader/Control
Interface: org.railreader.Control1
Activation: app launched with --control-bus[=name]
```

**Methods** (return once the command is accepted / animation started; completion via signal):
- `OpenDocument(s path) -> b`
- `GoToPage(i page)`, `FitPage()`, `FitWidth()`
- `FrameBlock(i pageBlockIndex, d zoom, s easing) -> b`   (zoom <= 0 ⇒ auto-fit)
- `FrameRole(s role, i occurrence, d zoom) -> b`
- `SetZoomPercent(d pct)`, `NavigateRole(s role, b forward) -> b`, `RailAdvanceLines(i count)`
- `SetColourEffect(s name)`, `ToggleLineHighlight(b)`, `ToggleLineFocusBlur(b)`
- `SetAnnotationMode(b)`, `ShowPane(s pane)`
- `GetPageDescription(i page) -> a(issdddd i)`   (index, role, preview, x, y, w, h, order)

**Properties** (read): `DocumentPath s`, `PageCount i`, `CurrentPage i`, `Zoom d`,
`IsAnimating b`, `CurrentBlockIndex i`, `CurrentRole s`.

**Signals** (sync backbone): `Settled()`, `AnimationStarted()`, `PageChanged(i)`,
`ReadingPositionChanged(i blockIndex, i lineIndex)`, `DocumentOpened(s path)`.

## 6. Key implementation seams (railreader2)

- **Entry / flag:** parse `--control-bus` in `src/RailReader2/Program.cs:Main`; start the
  server after `window.Opened` (next to the file-open at `App.axaml.cs:~50`).
- **VM wrappers (needed):** `SmoothlyFrameBlock` / `SmoothlyFrameRole` / `AnimateCameraTo`
  are on `DocumentController` (Core 0.20.0) but need thin `MainWindowViewModel` wrappers
  that go through the VM's `Dispatch(..., animate: true)` so `RequestAnimationFrame`
  drives the eased motion (mirror `HandleZoomKey`). Most other verbs already exist on the VM.
- **`Settled` signal:** `MainWindowViewModel.OnAnimationFrame` already receives
  `TickResult`; fire `Settled` on the `StillAnimating` true→false transition. `IsAnimating`
  exists on `DocumentController` (0.20.0).
- **State for queries:** `DocumentController.GetReadingPosition` / `GetPageDescription` /
  `DocumentInfo` (Core).
- **Transport dep:** `Tmds.DBus.Protocol` is already present transitively (Avalonia uses
  it for AT-SPI on Linux) — no heavy new dependency.

## 7. DSL sketch (YAML)

```yaml
demo: moment-rail
source: papers/MOMENT.pdf
fps: 60
cursor: park              # global: hidden | park | follow
recorder: portal
output: out/moment-rail.mp4
steps:
  - open
  - goto_page: 1
  - fit_page
  - hold: 800ms
  - frame_block: { role: figure, index: 0, zoom: 2.5, easing: ease-out, cursor: follow }
    wait: settled         # default; or wait: <ms>
  - hold: 1500ms
  - rail_advance_lines: { count: 5, per_line: 600ms }
  - colour_effect: amber
  - hold: 1000ms
```

Wait semantics: every motion verb defaults to `wait: settled`; `hold` is wall-clock
dwell for pacing; blind sleeps are never used to time *motion*, only *dwell*.

## 8. Recording (Phase C)

GNOME screencast portal at the monitor refresh; frames → `ffmpeg`. Because the sequencer
cuts on real `Settled` events, captured motion equals the on-screen experience. The
portal prompts once at session start.

## 9. Phasing

- **Phase A — control server (keystone).** `--control-bus` + `IRailReaderControl` +
  `ViewModelControl` + `DBusControlServer` with a minimal verb set (`OpenDocument`,
  `GoToPage`, `FrameRole`) + the `Settled` signal + a few read properties.
- **Phase B — runner + DSL.** `railreader2-cli demo`: parser, sequencer, `IControlClient`.
- **Phase C — recorder.** GNOME portal capture + ffmpeg, event-synced cuts.
- **Phase D — pointer + polish.** `cursor: follow` via synthetic pointer; broaden verbs.

### First step (recommended): Phase A walking skeleton
`--control-bus` + 3 verbs + `Settled` + 2 properties. Validate by hand with `busctl call`
(or `computer-use-linux`) plus a manual recorder: confirm `FrameRole figure` produces the
smooth zoom on the *real* window and emits `Settled`. Proves the real-window + reliable-
command + sync triad before the DSL/recorder exist. Already independently useful
(script-drive the app over `busctl`).

## 10. Testability (per decision #2)

- App: test `ViewModelControl` against `IRailReaderControl` under `Avalonia.Headless`
  (real VM, no bus). `DBusControlServer` is a thin adapter — minimal/contract tests only.
- Runner: DSL parser is pure (table tests); sequencer tested against a fake
  `IControlClient` (assert verb order + wait-on-`Settled` behavior) — no app/bus needed.
- Portability: a future Windows port swaps `DBusControlServer` for a named-pipe/gRPC
  adapter over the same `IRailReaderControl`; runner swaps `IControlClient` impl.

## 11. Status / done

- ✅ Core motion primitives (`SmoothlyFrameBlock`/`SmoothlyFrameRole`/`AnimateCameraTo`/
  `IsAnimating`, `StartTo`, `ComputeSnapTarget`, `TrySetCurrentByPageIndex`,
  `PinCurrentBlockForActivation`, `ComputeBlockFitZoom`) — RailReaderCore **0.20.0**,
  consumed by railreader2 (main).
- ⬜ Phases A–D: not started.

## 12. Open items / risks

- Auto-fit floors zoom at the rail threshold (so framing applies) — a very large block
  can't "fit" below threshold; acceptable (rail reading semantics). Revisit if needed.
- Non-navigable blocks (figures/tables outside NavigableRoles) can't use rail framing —
  `FrameBlock`/`FrameRole` return false. A separate centred-frame path is a later option.
- Recorder portal prompt is interactive — fine for manual runs; headless/cron capture
  would need a different path.
- D-Bus is Linux-only; Windows demos would need the portable-interface swap (designed for).
