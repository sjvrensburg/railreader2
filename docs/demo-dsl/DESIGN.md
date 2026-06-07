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

## 8. Recording (Phase C — implemented)

`GnomeScreenRecorder` drives `org.gnome.Shell.Screencast` (`Screencast`/`StopScreencast`),
which captures the monitor and encodes H.264 MP4 itself (GNOME ≥50) — no PipeWire, no
ffmpeg-piping, and no portal picker prompt (better for automation). Because the sequencer
cuts step motion on real `Settled` events, the continuous capture equals the on-screen
experience. ffmpeg is invoked only to transcode when the requested `output:` extension
differs from what GNOME wrote. Records the full screen; window-scoped capture
(`ScreencastArea`) is a later option. Non-GNOME hosts would swap in an xdg-portal recorder
behind the same `IScreenRecorder` seam.

## 9. Phasing

- **Phase A — control server (keystone). ✅ DONE.** `--control-bus` + `IRailReaderControl` +
  `ViewModelControl` + `DBusControlServer` with verbs `OpenDocument`/`GoToPage`/`FitPage`/
  `FitWidth`/`FrameRole`/`FrameBlock`, the `Settled`/`PageChanged`/`DocumentOpened` signals,
  and the 7 read properties. Validated by hand over `busctl`/`gdbus` (see §11).
- **Phase B — runner + DSL. ✅ DONE.** `railreader2-cli demo <script>` in a new `RailReader.Demo`
  library: a pure hand-rolled YAML-subset `DslParser` (no external YAML dep → AOT-safe),
  `DemoSequencer` (wait-on-`Settled` for `frame_*`, `PageChanged` for `goto_page`, wall-clock for
  `hold`; per-step `wait:` override; per-step timeout fallback), and `IControlClient` with a
  `DBusControlClient` (Tmds.DBus.Protocol) + a `FakeControlClient` for tests. Verbs: `open`,
  `goto_page`, `fit_page`, `fit_width`, `frame_role`, `frame_block`, `hold`. `--dry-run` validates a
  script with no app. Tests: 17 (parser table tests + sequencer vs. fake). **Note discovered in B:**
  eased animations (and thus `Settled`) only advance while the app window is actively rendering —
  the compositor frame loop drives `RequestAnimationFrame`. That's always true while recording, but
  a backgrounded window can stall mid-animation, so the runner times out per step and continues.
- **Phase C — recorder. ✅ DONE.** `IScreenRecorder` seam + `GnomeScreenRecorder` (uses
  `org.gnome.Shell.Screencast` directly — promptless, encodes H.264 MP4 natively on GNOME ≥50, no
  PipeWire) + `NullScreenRecorder`. The sequencer brackets the whole run (`StartAsync` → lead-in →
  steps → lead-out → `StopAsync`, stop in a `finally` so a thrown/cancelled run still finalises the
  file). `recorder: portal|gnome|screen` + `output:` in the DSL select it. ffmpeg is only used to
  transcode when GNOME's container differs from the requested extension (optional). Validated live:
  the MOMENT demo produced a valid H.264 MP4 of the fullscreen app. (Deviation from the original
  plan: used the GNOME Shell screencast API, not the xdg ScreenCast portal — simpler, promptless,
  no ffmpeg-piping; the portal would only be needed for non-GNOME or window-scoped capture.)
  - **Refinements after first review:** `fullscreen: true` (DSL) drives a new `SetFullScreen`
    control verb so the app fills the screen and the capture is just the app — the robust
    "window-only" answer on Wayland (a window can't get its global coords for region capture). A
    leading `open` step is **pre-rolled before recording** so the slow PDF load isn't dead time at
    the head of the video. Pacing is done with `hold` dwell; the **zoom stays the faithful native
    180 ms** (decision reaffirmed). GNOME appends its own container ext to the template, so we pass
    a base path and trust the path it returns.
- **Phase D — pointer + polish. ✅ DONE (cursor draw + broadened verbs).** New control verbs +
  DSL: `set_zoom: <pct>`, `colour_effect: <name>`, `navigate: { role, dir }` (rail role jump),
  `rail_advance_lines: { count, per_line, dir }` (line-by-line rail reading — the hero feature),
  `line_highlight: on|off`, `line_focus_blur: on|off` (plus `SetFullScreen` from Phase C). Role
  strings resolve via Core's shared `BlockRoleAliases`. Cursor: `cursor: show|follow` draws the
  pointer into the capture, `hidden|park|unset` hides it (GNOME `draw-cursor`). **Not done:** actual
  pointer *motion* for `park`/`follow` — synthetic pointer control is unreliable on this Wayland
  setup (see `computer-use-linux-setup`); the keywords are accepted but only toggle drawing.
  Tests: 26. Validated live: a rail-reading demo (frame text → highlight → 6× line advance → amber)
  produced a clean ~17s MP4.

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
  `PinCurrentBlockForActivation`, `ComputeBlockFitZoom`) — RailReaderCore **0.20.0**.
- ✅ Centred-frame + shared role vocabulary — RailReaderCore **0.21.0** (released, on NuGet),
  consumed by railreader2.
- ✅ **Phase A — control server** (branch `feat/control-bus-phase-a`). Files under
  `src/RailReader2/Control/`: `IRailReaderControl` (contract), `ViewModelControl`
  (UI-thread-marshalling impl), `DBusControlServer` (Tmds.DBus.Protocol adapter, the modern
  `DBusConnection`/`DBusAddress`/`IPathMethodHandler` API). VM seams in
  `MainWindowViewModel.Control.cs` (`SmoothlyFrameBlock`/`SmoothlyFrameRole`/`AnimateCameraTo`
  wrappers via `Dispatch(animate:true)`) + `AnimationSettled` event (fired on the
  `StillAnimating` true→false edge in `OnAnimationFrame`) + `PageChangedNotification`. Startup:
  `--control-bus[=name]` parsed in `App.axaml.cs`, server started after `window.Opened`, disposed
  on close. `Tmds.DBus.Protocol` referenced explicitly in the csproj.
  - **Validated 2026-06-07** over `busctl`/`gdbus`: introspection lists all members;
    `OpenDocument` → `true` + `DocumentOpened` signal; `GoToPage 4` → `PageChanged(4)`;
    `FrameRole heading 0` → `true`, `IsAnimating` true→false, `Zoom`→3 (rail framing),
    `CurrentRole`="Heading", and the **`Settled`** signal fired on settle. The real-window +
    reliable-command + event-sync triad is proven.
  - **Figure/Table/Chart framing — RESOLVED in Core 0.21.0** (released; railreader2 bumped
    0.20.0→0.21.0). Previously `FrameRole`/`FrameBlock` returned `false`
    for roles outside `DefaultRoleSets.Navigable` (the rail index can't seat them). Now
    `SmoothlyFrameBlock` falls back to a **geometric centred frame** for non-navigable blocks: ease
    zoom-to-fit + centre in the viewport with rail OFF (new `ZoomAnimationController.StartCameraOnly`
    pure-camera-move mode that skips per-frame `UpdateRailZoom` and the completion snap;
    `RailNav.Deactivate`; `DocumentState.ComputeCenteredFrame`). Unlike rail framing it does NOT
    floor at the 3× threshold, so a large figure shows whole below it. New explicit
    `SmoothlyCenterBlock`/`SmoothlyCenterRole` force geometric centring for any block. The control
    bus needs **no change** — `FrameRole`/`FrameBlock` already call the Core primitives.
    Validated end-to-end over `busctl` against a local Core build on MOMENT.pdf: `FrameRole figure`
    framed figures on pp.3/7 (p7 a large figure at 2.25×, whole) and `FrameRole table` framed a
    table on p5, each emitting `Settled`. Once Core ships, this lands in railreader2 via the bump.
- ⬜ Phases B–D: not started.

## 12. Open items / risks

- Auto-fit floors zoom at the rail threshold (so framing applies) — a very large block
  can't "fit" below threshold; acceptable (rail reading semantics). Revisit if needed.
- ~~Non-navigable blocks (figures/tables outside NavigableRoles) can't use rail framing.~~
  RESOLVED: `SmoothlyFrameBlock`/`FrameRole` now fall back to a geometric centred frame for
  non-navigable blocks; explicit `SmoothlyCenterBlock`/`Role` added (Core branch
  `feat/centred-frame-nonnavigable`, pending release). See §11.
- Recorder portal prompt is interactive — fine for manual runs; headless/cron capture
  would need a different path.
- D-Bus is Linux-only; Windows demos would need the portable-interface swap (designed for).
