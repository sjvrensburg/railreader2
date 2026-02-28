# railreader2 User Guide

Everything you need to know to get the most out of railreader2.

> **Web version:** This guide is also available as an [HTML page](https://sjvrensburg.github.io/railreader2/guide.html) with inline screenshots and lightbox image viewing.

## Contents

1. [Getting Started](#getting-started)
2. [Basic Navigation](#basic-navigation)
3. [Rail Mode](#rail-mode)
4. [Auto-Scroll](#auto-scroll)
5. [Jump Mode](#jump-mode)
6. [Line Focus Dim](#line-focus-dim)
7. [Bionic Reading](#bionic-reading)
8. [Colour Effects](#colour-effects)
9. [Search](#search)
10. [Annotations](#annotations)
11. [Text Selection](#text-selection)
12. [Bookmarks](#bookmarks)
13. [Settings](#settings)
14. [Keyboard Shortcuts](#keyboard-shortcuts)
15. [AI Agent CLI (Experimental)](#ai-agent-cli-experimental)

---

## Getting Started

### Download and install

Download the latest release from [GitHub Releases](https://github.com/sjvrensburg/railreader2/releases/latest). The AI layout model is bundled in both packages.

- **Linux:** Download the AppImage, make it executable (`chmod +x railreader2-linux-x86_64.AppImage`), and run it.
- **Windows:** Run `railreader2-setup-x64.exe` and follow the installer. Optionally associate `.pdf` files during setup.

### Opening a PDF

Use **File > Open** or press `Ctrl+O` to open a PDF. You can also pass a file path as a command-line argument. When no file is open, a welcome screen shows with instructions.

### First steps

Once a PDF is open, scroll through pages with `PgDn`/`PgUp`, zoom with `+`/`-` or mouse wheel, and pan by dragging. When you zoom past 3x, **rail mode** activates automatically — this is where the AI-guided reading begins.

---

## Basic Navigation

### Zoom and pan

**Mouse wheel** zooms towards the cursor. `+` and `-` keys zoom in and out. All zoom actions animate smoothly over ~180ms with cubic ease-out. Rapid scroll wheel inputs accumulate into the in-progress animation for fluid zooming. Press `0` to fit the page to the window. Use **View > Fit Width** to fill the viewport horizontally.

**Click and drag** to pan. Arrow keys also pan when not in rail mode.

### Page navigation

| Key | Action |
|-----|--------|
| `PgDn` / `PgUp` | Next / previous page |
| `Home` / `End` | First / last page |
| `Ctrl+G` | Go to a specific page number |
| `Space` | Next line (in rail mode) or next page |

### Minimap and outline

Press `Ctrl+M` to toggle the **minimap** — a small page thumbnail in the corner. Click or drag on it to navigate.

Press `Ctrl+Shift+O` to open the **outline panel** (table of contents). Click entries to jump to sections. Press `Ctrl+Shift+B` to open the **bookmarks panel** — see [Bookmarks](#bookmarks).

### Multi-tab

Open multiple PDFs in tabs with `Ctrl+O`. Each tab has independent zoom, position, and analysis state. Switch tabs with `Ctrl+Tab` or by clicking. Drag tabs to reorder.

---

## Rail Mode

Rail mode is the core feature of railreader2. When you zoom past the threshold (default 3x), the AI layout analysis detects text blocks and reading order, and navigation locks to those blocks. Non-active regions are dimmed so you can focus on the current block and line.

![Rail mode](img/rail_mode.png)
*Rail mode — line-by-line reading at high magnification with the current line highlighted*

### Line-by-line navigation

| Key | Action |
|-----|--------|
| `Down` / `S` | Next line |
| `Up` / `W` | Previous line |
| `Right` / `D` | Hold to scroll forward along the line |
| `Left` / `A` | Hold to scroll backward |
| `Home` / `End` | Snap to start/end of current line |

When you reach the last line of a block, pressing `Down` advances to the next navigable block. At the last block on a page, it advances to the next page.

### Click to jump

Click on any detected block in rail mode to jump directly to it. The view snaps to the clicked block's first line.

### Horizontal scrolling

Holding `Right`/`D` scrolls horizontally along the current line with speed ramping — it starts slow and accelerates. `Ctrl + mouse wheel` also scrolls horizontally. The speed ramp time and max speed are configurable in Settings.

> **Tip:** Press `Shift+D` to toggle the debug overlay, which shows all detected layout blocks with their class labels, confidence scores, and reading order.

---

## Auto-Scroll

Press `P` in rail mode to toggle **auto-scroll**. The view continuously scrolls horizontally along the current line, then advances to the next line when it reaches the edge.

- **Speed boost:** Hold `D` or `Right` during auto-scroll to double the speed.
- **Pauses:** Auto-scroll pauses briefly at line boundaries (default 400ms) and block/page transitions (default 600ms) to let your eyes settle. Configurable in Settings > Auto-Scroll.
- **Stop:** Press `Escape`, `P`, or any opposing navigation key (`Up`, `Left`).

The status bar shows a green **"Auto-Scroll"** indicator when active. Adjust speed with the rail toolbar slider or the `[` / `]` keys.

---

## Jump Mode

Press `J` in rail mode to toggle **jump mode**. Instead of continuous scrolling, `Right`/`D` and `Left`/`A` perform saccade-style jumps — advancing by a configurable percentage of the visible width (default 25%).

Hold `Shift` with `Right` or `Left` to perform a **short jump** at half the normal distance. This is useful for fine-grained positioning within a line.

This mimics natural reading eye movements and is useful for scanning text quickly. Adjust jump distance with `[` / `]` or in Settings > Rail Reading.

> **Note:** Auto-scroll and jump mode are mutually exclusive. Enabling one disables the other.

---

## Line Focus Dim

When enabled, line focus dim applies a smooth feathered dimming overlay to the entire page except the active line in rail mode. Non-active lines fade toward the background colour, reducing peripheral distraction while maintaining a clean visual transition.

![Line focus dim](img/line_focus_blur.png)
*Line focus dim — non-active lines are dimmed to reduce distraction*

Toggle with the `F` key, the **F** button on the rail toolbar, or in Settings > Rail Reading. Dim intensity is adjustable from 0 (off) to 1 (maximum). The padding around the active line (how much extra space stays fully visible) is also configurable.

---

## Bionic Reading

Bionic reading is a reading aid that de-emphasises the trailing portion of each word, guiding your eye to fixation points at the start of words. The effect is applied as a GPU shader that fades non-fixation characters toward the background.

Toggle with the `R` key, the **R** button on the rail toolbar, or in Settings > Rail Reading. Two parameters are configurable:

- **Fixation percent** — the fraction of each word kept at full contrast (default 40%). Higher values keep more of each word sharp.
- **Fade intensity** — how much the non-fixation characters are faded (default 0.6). Higher values produce a stronger effect.

Bionic reading composes correctly with line focus dim and colour effects.

### Line highlight tint

The active line in rail mode can have a configurable colour tint applied as an overlay. This makes the current line stand out more clearly, especially at high magnification.

Choose from five presets in Settings > Rail Reading:

| Tint | Description |
|------|-------------|
| **Auto** | Adapts to the active colour effect (amber tint for Amber, green for HighContrast/HighVisibility, etc.) |
| **Yellow** | Warm yellow highlighter |
| **Cyan** | Cool cyan tint |
| **Green** | Soft green tint |
| **None** | No tint — line is highlighted by dimming only |

Opacity is adjustable from 0.0 (invisible) to 1.0 (fully opaque). The default is Auto at 25% opacity.

---

## Colour Effects

Four GPU-accelerated colour filters are available, applied only to PDF content (not the UI). Access via **View > Colour Effect**, Settings > Appearance, or press `C` to cycle through effects on the active tab.

Each tab keeps its own colour effect independently — you can have one PDF in Amber and another in High Contrast. The per-tab effect is saved with the reading position and restored when you reopen the file.

| Effect | Description |
|--------|-------------|
| **Amber Filter** | Warm tint that reduces blue light and perceived haze. Good for extended reading. |
| **High Contrast** | White-on-black rendering with an S-curve for maximum contrast. Reduces glare. |
| **High Visibility** | Yellow-on-black for maximum legibility at the cost of colour information. |
| **Invert** | Simple colour inversion for dark backgrounds. |

<p align="center">
  <img src="img/colour_effects.png" alt="Amber colour filter" width="45%">
  &nbsp;
  <img src="img/colour_effect_high_contrast.png" alt="High contrast with rail mode" width="45%">
</p>

Each effect has adjustable intensity (0.0 to 1.0). Rail mode overlay colours automatically adapt to the active colour effect for readable contrast.

> **Tip:** Press `C` to quickly cycle through colour effects. The status bar briefly shows the active effect name.

---

## Search

Press `Ctrl+F` to open the search bar. Type your query and press `Enter` to search across all pages.

![Search highlights](img/search_highlights.png)
*Search results — matches highlighted in yellow, active match in orange*

- **Navigate matches:** `F3` / `Shift+F3` or the arrow buttons in the search bar.
- **Case sensitivity:** Toggle with the `Aa` button in the search bar.
- **Regex:** Toggle with the `.*` button for regular expression search.
- **Match count:** The search bar shows the current match index and total count.

Press `Escape` to close the search bar and clear highlights.

---

## Annotations

Right-click anywhere on the page to open the **radial menu** with five annotation tools:

![Annotations](img/annotations.png)
*Annotations — highlights and text notes on a PDF page*

| Tool | Key | Description |
|------|-----|-------------|
| **Highlight** | `1` | Click and drag over text to highlight. Uses character-level detection. Choose from yellow, green, or pink via the radial menu colour picker. |
| **Pen** | `2` | Freehand drawing. Choose from red, blue, or black via the radial menu colour picker. |
| **Rectangle** | `3` | Draw rectangular outlines or filled regions. |
| **Text Note** | `4` | Click to place a note. Shows as a small folded-corner icon; click the icon in browse mode to expand/collapse the popup. Click an existing note in Text Note mode to edit. |
| **Eraser** | `5` | Click on an annotation to remove it. |

### Colour picker

The **Highlight** and **Pen** segments on the radial menu have colour options. Tap the segment to reveal an outer ring of colour dots. Tap a colour to select it and activate the tool. A small indicator dot on the segment shows the currently active colour.

### Popup notes

Text notes display as a compact folded-corner icon (16px). In browse mode, click the icon to expand a floating popup showing the full note text with word wrapping. Click again to collapse. Double-click or use the Text Note tool to edit.

### Select, move, and resize

In **browse mode** (no annotation tool active), click on any annotation to select it (shown with a dashed blue outline). Drag a selected annotation to move it. For freehand annotations, 8 resize handles appear on the bounding box — drag a handle to scale proportionally. All move and resize actions support undo/redo.

### Delete selected annotation

Press `Delete` or `Backspace` in browse mode to remove the selected annotation. This uses the same undo-supported removal as the eraser.

### Undo and redo

`Ctrl+Z` undoes the last annotation action (including moves, resizes, and deletions). `Ctrl+Y` or `Ctrl+Shift+Z` redoes. Each tab has an independent undo/redo stack.

### Persistence

Annotations are saved automatically as JSON sidecar files alongside the PDF (e.g. `paper.pdf.annotations.json`). They load automatically when you reopen the file.

### Export

Use **File > Export with Annotations** to create a new PDF with annotations rendered into the pages. The original PDF is not modified.

---

## Text Selection

The floating toolbar in the top-left corner provides three modes:

- **Browse** — Default pan mode.
- **Text Select** — Click and drag to select text. Selection uses character-level bounding boxes for precise results.
- **Copy** — Appears when text is selected. Click to copy, or use `Ctrl+C`.

Press `Escape` to cancel selection and return to browse mode.

---

## Bookmarks

Press `B` to bookmark the current page, or click **+ Add Bookmark** in the bookmarks panel. A dialog lets you name the bookmark (pre-filled with "Page N").

### Managing bookmarks

Press `Ctrl+Shift+B` to open the bookmarks panel (a tab alongside the outline panel). Each bookmark shows its name and page number.

- **Navigate:** Click a bookmark to jump to that page (zoom resets to fit the page).
- **Rename:** Click the **Rename** button on a bookmark to change its name.
- **Delete:** Click the **Delete** button to remove a bookmark.
- **Back:** After navigating to a bookmark, a **"Back to previous location"** button appears at the top of the list. Click it or press `` ` `` (backtick) to return to where you were.

### Duplicate handling

If you bookmark a page that already has a bookmark, the existing bookmark's name is updated instead of creating a duplicate.

### Persistence

Bookmarks are stored in the same annotation sidecar file as highlights, notes, and other annotations (`<pdf>.railreader2.json`). They persist across sessions automatically.

---

## Settings

Press `Ctrl+,` or use the menu to open Settings. Changes take effect immediately and are saved automatically.

### Appearance
- **UI Font Scale:** Adjust the size of all UI text (default 1.25x).
- **Dark Mode:** Switch the UI to a dark theme. Takes effect immediately.
- **Motion Blur:** Toggle and adjust intensity of directional blur during scroll/zoom.
- **Colour Effect:** Select and configure the active colour filter (applies globally via Settings; use `C` key for per-tab cycling).

### Rail Reading
- **Zoom Threshold:** Zoom level at which rail mode activates (default 3.0x).
- **Snap Duration:** Duration of line-snap animations in milliseconds.
- **Scroll Speed:** Start and max speed for horizontal hold-to-scroll.
- **Ramp Time:** Seconds to reach max scroll speed from start.
- **Pixel Snapping:** Quantise camera to pixel grid to reduce text shimmer.
- **Line Focus Dim:** Toggle and set intensity and padding.
- **Bionic Reading:** Toggle and configure fixation percentage and fade intensity.
- **Line Highlight Tint:** Choose a colour tint for the active line (Auto, Yellow, Cyan, Green, None) and set opacity.
- **Jump Distance:** Percentage of visible width for jump mode (5–80%).

### Auto-Scroll
- **Line Pause:** Pause duration at line boundaries (ms, 0 to disable).
- **Block Pause:** Pause duration at block/page boundaries (ms, 0 to disable).

### Advanced
- **Navigable Block Types:** Choose which PP-DocLayoutV3 block types are navigable in rail mode.
- **Analysis Lookahead:** Number of pages to pre-analyze ahead (0 to disable).

### Config file

Configuration is stored at `~/.config/railreader2/config.json` (Linux) or `%APPDATA%\railreader2\config.json` (Windows). You can edit it directly; restart the app to apply changes.

---

## Keyboard Shortcuts

### General

| Key | Action |
|-----|--------|
| `Ctrl+O` | Open file |
| `Ctrl+W` | Close tab |
| `Ctrl+Tab` | Next tab |
| `Ctrl+Q` | Quit |
| `Ctrl+,` | Settings |
| `Ctrl+M` | Toggle minimap |
| `Ctrl+Shift+O` | Toggle outline panel |
| `Ctrl+Shift+B` | Toggle bookmarks panel |
| `Ctrl+G` | Go to page |
| `F1` | Keyboard shortcuts dialog |
| `F11` | Toggle fullscreen |

### Navigation

| Key | Action |
|-----|--------|
| `PgDn` / `PgUp` | Next / previous page |
| `Home` / `End` | First / last page |
| `Space` | Next line (rail) or next page |
| `+` / `-` | Zoom in / out |
| `0` | Fit page to window |
| `Shift+D` | Toggle debug overlay |

### Rail Mode

| Key | Action |
|-----|--------|
| `Down` / `S` | Next line |
| `Up` / `W` | Previous line |
| `Right` / `D` | Scroll forward (hold) |
| `Left` / `A` | Scroll backward (hold) |
| `Shift+Right` / `Shift+Left` | Short jump — half distance (jump mode) |
| `Home` / `End` | Line start / end |
| `P` | Toggle auto-scroll |
| `J` | Toggle jump mode |
| `B` | Add bookmark for current page |
| `` ` `` (backtick) | Navigate back to previous location |
| `C` | Cycle colour effect on active tab |
| `F` | Toggle line focus dim |
| `R` | Toggle bionic reading |
| `[` / `]` | Adjust speed or jump distance |
| `Shift+[` / `Shift+]` | Adjust blur intensity |
| Click | Jump to block |

### Search & Annotations

| Key | Action |
|-----|--------|
| `Ctrl+F` | Open search bar |
| `F3` / `Shift+F3` | Next / previous match |
| `1` / `2` / `3` / `4` / `5` | Highlight / Pen / Rectangle / Text Note / Eraser |
| Right-click | Open radial menu (with colour picker for Highlight/Pen) |
| `Ctrl+Z` | Undo |
| `Ctrl+Y` | Redo |
| `Delete` / `Backspace` | Delete selected annotation (browse mode) |
| `Ctrl+C` | Copy selected text |
| `Escape` | Cancel / close / stop / exit fullscreen |

---

## AI Agent CLI (Experimental)

> **Note:** The AI Agent CLI is not included in the binary releases (AppImage or Windows installer). It is only available when building from source with the .NET SDK installed.

RailReader includes a headless agent CLI that lets an LLM open PDFs, navigate, extract text, search, annotate, and export — all via structured tool calls. This feature is experimental and may change in future releases.

### Setup

```bash
# Set your API key
export OPENAI_API_KEY="your-key"

# Optional: specify model and base URL
export RAILREADER_MODEL="gpt-4o"
export RAILREADER_BASE_URL="https://api.example.com/v1"
```

### Usage

```bash
# Run with a task description
dotnet run --project src/RailReader.Agent -- "Open paper.pdf and summarise page 1"

# Interactive mode (prompts for task)
dotnet run --project src/RailReader.Agent

# Capture screenshots deterministically
dotnet run --project src/RailReader.Agent -- --capture-screenshots docs/img/
```

### Available tools

| Tool | Description |
|------|-------------|
| `OpenDocument` | Open a PDF file |
| `CloseDocument` | Close the active document |
| `ListDocuments` | List all open documents |
| `GoToPage`, `NextPage`, `PrevPage` | Page navigation |
| `SetZoom` | Set zoom level |
| `SetRailPosition` | Position rail at specific block/line |
| `GetPageText` | Extract text from a page |
| `GetLayoutInfo` | Get layout analysis results |
| `Search` | Full-text search with regex support |
| `AddHighlight`, `AddTextAnnotation` | Add annotations |
| `ExportPdf` | Export with annotations |
| `ExportPageImage` | Screenshot export (PNG) |
| `WaitForAnalysis` | Wait for layout analysis |
| `SetColourEffect` | Set colour filter |
