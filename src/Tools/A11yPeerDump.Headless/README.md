# A11yPeerDump.Headless

A headless **accessibility audit** for RailReader2. It boots the real
`App`/`MainWindow`/`MainWindowViewModel` under `Avalonia.Headless` and walks the
**Avalonia automation-peer tree** — the exact source the platform accessibility
backends project to assistive tech and UI-automation agents (AT-SPI on Linux, UIA
on Windows).

It needs **no display server, no a11y D-Bus bus, and no Accerciser**: it reads the
peers directly in-process. That makes it the cheap, deterministic, CI-able way to
check the app's accessibility surface without a GNOME+Wayland desktop.

## Usage

```bash
# Audit with a bundled sample PDF (picks the first experiments/PDFs/*.pdf), to stdout
dotnet run --project src/Tools/A11yPeerDump.Headless -c Release

# A specific document, attempt rail mode, write to a file
dotnet run --project src/Tools/A11yPeerDump.Headless -c Release -- mypaper.pdf --rail --out audit.txt
```

- positional `*.pdf` — document to open (default: first `experiments/PDFs/*.pdf`).
- `--rail` — engage rail mode (zoom above the rail threshold, which submits the
  page for analysis and seats the rail) so the viewport peer's rail-line channel is
  exercised. Needs the **ONNX layout model present** (`./scripts/download-model.sh`);
  reported as skipped if the page can't be analysed. The report prints both the peer's
  cached Name/HelpText and the **live** Core reading position (role + line text) — the
  latter is the authoritative proof the channel produces the line text, since the
  cached snapshot can lag the live value under the harness's rapid drive.
- `--out <file>` — write the report to a file instead of stdout.

## What the report contains

1. **Automation-peer tree** — what an AT client sees before opening any menu
   (control type, accessible name, AutomationId, exposed patterns, enabled state).
2. **Menu command audit** — every command, *including items inside closed submenus*
   (walked via the logical tree), with its name, keyboard accelerator, and whether
   it exposes the UIA Invoke pattern.
3. **Named / identified controls** — chrome carrying an explicit
   `AutomationProperties.Name` / `AutomationId`.
4. **Document viewport peer** — the `DocumentViewportAutomationPeer` spotlight:
   Name (stable "Document viewport" while browsing, the current line while
   rail-reading), ControlType, AutomationId, and the HelpText/Value description
   (page, zoom, mode, rail line, page outline).
5. **Summary** — gaps worth fixing (e.g. an actionable control with no name) and
   the menu-Invoke coverage finding.

## What it can and cannot verify

**Can** (no desktop needed): every control's accessible name / control-type /
AutomationId; which controls expose Invoke/Toggle/Value/etc. patterns; the live
viewport description channel. This catches the common gaps — an unnamed button, a
command missing from the tree, the viewport not reporting its state.

**Cannot**: AT-SPI-*backend*-specific projection. The peer tree is the *source*;
the AT-SPI backend's mapping of it has its own behaviour (e.g. it ignores
`AutomationProperties.LiveSetting`, and it may expose menu items through the AT-SPI
**Action** interface even though the UIA-style peer has no `IInvokeProvider`). To
confirm those, run a live dump against the running app — and X11 is fine for this
(AT-SPI is a D-Bus protocol, independent of Wayland/X11; the Wayland target only
matters for the agent's input/screencast transport, not the a11y tree).

### Live AT-SPI cross-check (on X11, no Accerciser GUI required)

Use the companion script `scripts/atspi-dump.py`:

```bash
# 1. Enable the a11y bus, then launch the app (any X11 session works)
gsettings set org.gnome.desktop.interface toolkit-accessibility true
railreader2 some.pdf &        # or: dotnet run -c Release --project src/RailReader2 -- some.pdf &

# 2. Dump the live tree — note the SYSTEM python (pyatspi is an apt package, invisible to conda):
/usr/bin/python3 scripts/atspi-dump.py
```

It prints the full tree with an `actions=[...]` column, then a focused summary of
whether each menu item exposes a callable **Action** — the thing the peer audit
can't see. Gotchas it handles / you should know:

- **Run it with `/usr/bin/python3`**, not a conda/venv `python3`. `pyatspi` lives in
  the system `dist-packages`; a conda interpreter raises `ModuleNotFoundError` even
  though `apt` reports it installed.
- RailReader2 registers on the a11y bus as **`'Avalonia Application'`** (Avalonia
  doesn't derive it from the assembly), with the frame named `railreader2`. The
  script matches both.

**What the live run established (2026-06-27, KDE/X11):** menu items expose **no**
AT-SPI Action (so menus are not agent-actionable on Linux — use buttons + keyboard
accelerators); all buttons expose `click`/`toggle`; and in rail mode the viewport
node's accessible **name is the live line text** being read. See
`memory/project_agent_readiness` for the full findings.

## Notes

- Boots with real Skia drawing (`UseHeadlessDrawing = false`) so the genuine
  MainWindow / composition layers / `DocumentViewportAutomationPeer` are exercised
  — same proven boot as `RenderHarness.Headless`. Pixels aren't captured.
- Menu *submenu* item containers realise lazily, so the menu audit enumerates the
  `MenuItem`s via the logical tree (always present, declared in XAML) and creates a
  peer per item — closed submenus included.
