# Driving RailReader2 with an AI agent (computer-use-linux)

A playbook for controlling RailReader2's GUI through an automation agent such as
[computer-use-linux](https://github.com/agent-sh/computer-use-linux) over AT-SPI,
plus when to skip the GUI entirely.

Everything here is grounded in the app's **live AT-SPI tree** (verified on KDE/X11,
2026-06-27) and the in-repo audit tools. Re-verify after UI changes with
`src/Tools/A11yPeerDump.Headless` (headless, anywhere) and `scripts/atspi-dump.py`
(live).

---

## 0. First decide: GUI or CLI?

- **Content tasks** — extract text/structure, transcribe equations/tables/figures,
  export to Markdown, render pages, dump annotations. **Do NOT drive the GUI.** Use
  the headless **`RailReader2.Cli`** (`render` / `structure` / `annotations` / `vlm`
  / `export`). It needs no display, no a11y, and is deterministic. See
  [user-guide.md](user-guide.md#cli-tool).
- **Interactive control / demos / "read this on screen"** — drive the GUI as below.

---

## 1. The golden rule: act via **buttons + keyboard**, not menus

On Linux/AT-SPI, **Avalonia menu items expose no callable Action** — an agent cannot
open the menu bar or invoke a menu command through AT-SPI. (They also don't appear in
the tree until the menu is opened.) So:

- **Prefer the on-screen buttons** (toolbar, status bar, side-panel) — every button
  exposes a `click` (or `toggle`) Action and a descriptive accessible name.
- **Prefer keyboard accelerators** — once the window is focused, the full shortcut map
  (below) drives everything, including all menu-only commands.
- Treat the menu bar as human-only. If a command exists *only* in a menu, fire its
  keyboard accelerator instead.

---

## 2. Launch and focus

```bash
# Enable the accessibility bus once per session
gsettings set org.gnome.desktop.interface toolkit-accessibility true

# Launch on a document (positional arg = PDF path)
railreader2 /path/to/paper.pdf
#   or from source:  dotnet run -c Release --project src/RailReader2 -- /path/to/paper.pdf
```

**App identity on the a11y bus:** the application registers as **`Avalonia Application`**
(Avalonia doesn't derive it from the assembly); the top-level **frame is named
`railreader2`**. Match on the frame, or on the Avalonia app name. There are no
`--page/--zoom/--rail` startup flags yet — reach the desired state with the actions below.

---

## 3. Element reference (stable handles)

Buttons carry a descriptive **accessible name**; the most important also carry a stable
**AutomationId** (shown as `#Id`), which is unaffected by state/label changes — prefer
the Id when present.

**Chrome / navigation**
| Control | Name | AutomationId | Action |
|---|---|---|---|
| Side-panel toggle | `Toggle side panel` | `#SidebarToggle` | toggle |
| Previous page | `Previous page (PgUp)` | `#PreviousPage` | click |
| Next page | `Next page (PgDn)` | `#NextPage` | click |
| Page indicator (read-only) | `Page N of M` | `#PageIndicator` | — |
| Zoom indicator (read-only) | `Zoom NNN percent` | `#ZoomIndicator` | — |
| Side panel container | `Side panel` | `#SidePanel` | — |
| Tab close | `Close <filename>` | — | click |
| All-tabs overflow | `All tabs` | — | click |

**Document viewport** — the GPU canvas's accessibility channel
| Control | AutomationId | Notes |
|---|---|---|
| Viewport | `#DocumentViewport` | **Name** = `Document viewport` while browsing, and **the current line text while rail-reading** (updates as you advance). **HelpText/Value** = a full description: `Page N of M, zoom X%, <Browse mode \| Rail reading <role>, line i of n: "…">`, plus a page outline (`Page contains: 1 heading, 3 paragraphs, 1 figure`). This is the primary "what is on screen / what is being read" read-channel. |

**Mode toolbar** (left of the viewport)
| Name | AutomationId | Action |
|---|---|---|
| `Browse / Pan` | `#BrowseButton` | toggle |
| `Text Select` | `#SelectButton` | toggle |
| `Start rail reading here` | `#RailHereButton` | toggle (then click in the page) |
| `Freeze panes` | `#FreezeButton` | click (opens a flyout) |
| `Annotation mode` | `#AnnotateButton` | toggle |

**Rail toolbar** (right edge, visible in rail mode) — by name
`Auto-scroll`, `Jump mode`, `Line focus blur`, `Line highlight` (each a click button),
and sliders `Scroll speed` (becomes `Jump distance` in jump mode) and `Motion blur intensity`.

**Annotation tools** (when annotation mode is on) — by name
`Highlight`, `Underline`, `Strikethrough`, `Squiggly underline`, `Pen`, `Rectangle`,
`Text note`, `Text box`, `Eraser`, plus `Colour` and `Stroke thickness`. Also `Copy selected text`.

---

## 4. Reading app state

Poll these instead of OCR-ing pixels:

- **Viewport `#DocumentViewport`** — Name (current line in rail mode) and HelpText (full
  page/zoom/mode/line/outline description). The name changes on each line advance, which
  is what a screen reader speaks.
- **Status bar** exposes live text labels: `Page N of M` (`#PageIndicator`),
  `Zoom NNN percent` (`#ZoomIndicator`), and (in rail mode) `Block i/n | Line j/m`,
  `Rail Mode`, `Auto-Scroll`.
- A **status button** `Stop auto-scroll (P)` appears while auto-scroll runs.

---

## 5. Keyboard shortcut map

Focus the window first (click the viewport or `#DocumentViewport`).

**General:** `Ctrl+O` open · `Ctrl+W` close tab · `Ctrl+Tab` next tab ·
`Ctrl+\` split right · `Ctrl+Shift+\` close pane · `Ctrl+,` settings ·
`Ctrl+M` minimap · `Ctrl+Shift+M` margin-crop · `Ctrl+Shift+O/B/I` outline/bookmarks/index ·
`Ctrl+G` go to page · `Ctrl+F` search · `F3`/`Shift+F3` next/prev match ·
`F1` shortcuts · `F11` fullscreen · `Ctrl+Q` quit.

**Navigation/zoom:** `PgDn`/`PgUp` page · `+`/`-` zoom · `0` fit page · `Shift+D` debug overlay.

**Rail mode** (engages above ~3× zoom, or press `R` to start at any zoom then click a spot):
`Down`/`S` next line · `Up`/`W` previous line · `Right`/`D` hold-scroll forward ·
`Left`/`A` hold-scroll back · `Home`/`End` line start/end · `P` auto-scroll (then `D`/`S` to
continue when parked) · `J` jump mode · `H` line highlight · `F` line focus dim · `B` bookmark ·
`Z` freeze panes · `C` cycle colour effect · `Ctrl`+drag free pan ·
`[`/`]` speed · `Shift+[`/`Shift+]` blur ·
`Ctrl+Shift+H`/`G`/`T`/`E` jump to next heading/figure/table/equation.

**Annotation / clipboard:** `Ctrl+E` annotation mode · `1`–`5` Highlight/Pen/Rectangle/Text-note/Eraser ·
`Ctrl+Z`/`Ctrl+Y` undo/redo · `Ctrl+L` copy block as LaTeX (VLM) · `Ctrl+C` copy selected text ·
`Escape` cancel/stop/close.

---

## 6. Task recipes

- **Open and go to a page:** launch with the PDF path → click `#NextPage`/`#PreviousPage` or
  `Ctrl+G` then type the page → read `#PageIndicator` to confirm.
- **Rail-read a page aloud:** zoom up (`+` several times, or click `Start rail reading here`
  `#RailHereButton` then click a line) → confirm `Rail Mode` in the status bar → press `Down`
  repeatedly, reading `#DocumentViewport`'s Name after each advance.
- **Auto-scroll through prose:** in rail mode, click `Auto-scroll` (or `P`); it parks at
  equations/tables/figures/headings — press `D`/`S` (or click `Stop auto-scroll`) to continue.
- **Search:** `Ctrl+F` → type query → `F3`/`Shift+F3` to step matches → read `#PageIndicator`.
- **Jump to the next figure/table/equation:** `Ctrl+Shift+G`/`T`/`E` (forward).
- **Extract a block's content:** right-click is pointer-only; prefer `Ctrl+L` (copy current
  rail block as LaTeX/Markdown/description) or — for bulk/headless — the CLI `vlm`/`export`.
- **Freeze a table header:** click `Freeze panes` `#FreezeButton` → pick Rows/Columns/Both →
  click in the page to drop the split (`Z` arms "both").

---

## 7. Verifying / re-checking accessibility

- **Headless, anywhere (CI-able):**
  `dotnet run --project src/Tools/A11yPeerDump.Headless -c Release -- <pdf> [--rail]` —
  audits the automation-peer tree (names, ids, patterns, the viewport peer).
- **Live AT-SPI (what an agent actually sees):**
  `/usr/bin/python3 scripts/atspi-dump.py` (system python — pyatspi is invisible to conda).

---

## 8. Known gaps / caveats (as of 2026-06-27)

- **Menus aren't AT-SPI-actionable** — use buttons + accelerators (§1).
- App name on the bus is the generic `Avalonia Application` (frame is `railreader2`).
- **Input/screencast transport:** AT-SPI (the tree above) is display-server-agnostic, but an
  agent's *input injection and screen capture* on Wayland go through the portals
  (RemoteDesktop/ScreenCast); on X11 they're direct. This playbook covers the a11y tree, not
  the transport.
- Live-region announcements: Avalonia's AT-SPI backend has no `LiveSetting`; line announcements
  ride the viewport's Name-change, which Orca-class readers speak.

See `memory/project_agent_readiness` for the running findings and next steps.
