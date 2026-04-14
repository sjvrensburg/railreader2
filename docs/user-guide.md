# railreader2 User Guide

Everything you need to know to get the most out of railreader2.

> **Web version:** This guide is also available as an [HTML page](https://sjvrensburg.github.io/railreader2/guide.html) with inline screenshots and lightbox image viewing.

## Contents

1. [Getting Started](#getting-started)
2. [Basic Navigation](#basic-navigation)
3. [Rail Mode](#rail-mode)
4. [Auto-Scroll](#auto-scroll)
5. [Jump Mode](#jump-mode)
6. [Line Focus & Highlight](#line-focus--highlight)
7. [Colour Effects](#colour-effects)
8. [Search](#search)
9. [Annotations](#annotations)
10. [PDF Links](#pdf-links)
11. [Text Selection](#text-selection)
12. [Bookmarks](#bookmarks)
13. [Figures Panel](#figures-panel)
14. [Copy as LaTeX (VLM)](#copy-as-latex-vlm)
15. [CLI Tool](#cli-tool)
16. [Settings](#settings)
17. [Troubleshooting](#troubleshooting)
18. [Keyboard Shortcuts](#keyboard-shortcuts)
19. [Removed Features](#removed-features)

---

## Getting Started

### Download and install

The AI layout model is bundled in all packages.

- **Linux:** Download `railreader2-linux-x86_64.AppImage` from [GitHub Releases](https://github.com/sjvrensburg/railreader2/releases/latest), make it executable (`chmod +x railreader2-linux-x86_64.AppImage`), and run it.
- **Windows (Microsoft Store):** Install directly from the [Microsoft Store](https://apps.microsoft.com/store/detail/9P9J8KZ6RVZP) for automatic updates, no SmartScreen warnings, and clean install/uninstall. The Store release may lag behind the GitHub release by a few days due to certification review.
- **Windows (standalone installer):** Download `railreader2-setup-x64.exe` from [GitHub Releases](https://github.com/sjvrensburg/railreader2/releases/latest) and run it. Optionally associate `.pdf` files during setup. This always has the latest version immediately.

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

> **Quick go-to:** Double-click the page number in the status bar to type a page number directly and press Enter to navigate.

> **Edge-hold page navigation:** When not in rail mode, holding `Down` or `S` at the bottom of the page for 400ms automatically advances to the next page. Similarly, holding `Up` or `W` at the top of the page goes to the previous page.

### Margin cropping

Press `Ctrl+Shift+M` to toggle **margin cropping**, or enable it in Settings. When on, fit/centre operations (the `F` and `0` keys, plus the page-flip on edge-hold) target the detected content area instead of the full page, so whitespace margins don't waste screen space at high zoom.

The crop is computed from the analysed layout blocks and grows automatically as more pages are analysed — it never clips content. Without the ONNX model, cropping is a no-op.

Margin cropping never pushes you into rail mode: if the tighter fit would cross the rail zoom threshold, the fit is capped just below it. Toggling while zoomed past fit-width leaves your camera alone; the effect takes hold on your next fit or page flip.

### Minimap and outline

Press `Ctrl+M` to toggle the **minimap** — a page thumbnail in the corner. Click or drag inside it to navigate. **Drag the top edge** (grip handle) to move it anywhere on the window. **Drag the inner corner** (resize handle, opposite the screen edge it's docked against) to resize. The minimap maintains the page's aspect ratio. Position and size persist across sessions.

When you make the minimap large, it switches its source bitmap to the primary view's high-DPI page render so the thumbnail stays crisp.

Press `Ctrl+Shift+O` to open the **outline panel** (table of contents). Click entries to jump to sections. Press `Ctrl+Shift+B` to open the **bookmarks panel** — see [Bookmarks](#bookmarks). Press `Ctrl+Shift+I` to open the **figures panel** — a browsable index of all detected figures, tables, and equations in the document. See [Figures Panel](#figures-panel).

### Multi-tab

Open multiple PDFs in tabs with `Ctrl+O`. Each tab has independent zoom, position, and analysis state. Switch tabs with `Ctrl+Tab` or by clicking. Drag tabs to reorder.

**Right-click any tab** to open a context menu with:
- **Duplicate Tab** — opens the same PDF in a new tab
- **Close Tab** — closes the tab

**Tab bar overflow:** When many tabs are open, they shrink with ellipsis text. Use the mouse wheel to scroll the tab bar horizontally, or click the **▼** dropdown to see all tabs and jump to one.

Switching tabs automatically exits any active annotation mode to prevent accidental edits on the wrong document.

---

## Rail Mode

Rail mode is the core feature of railreader2. When you zoom past the threshold (default 3x), the AI layout analysis detects text blocks and reading order, and navigation locks to those blocks. Non-active regions are dimmed so you can focus on the current block and line.

![Rail mode](img/rail_mode.png)
*Rail mode — line-by-line reading at high magnification with the current line highlighted*

### Free pan

Hold `Ctrl` and drag to temporarily pan freely, even zooming out below the rail threshold. This lets you quickly check a figure, equation, or footnote elsewhere on the same page without losing your place. Release `Ctrl` to snap back to your original reading position and zoom level.

### Zoom position preservation

Zooming while reading a line now preserves your horizontal scroll position, so you can adjust magnification without losing your place in the text.

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

### Rail toolbar

When rail mode is active, a floating toolbar appears with **P** (auto-scroll), **J** (jump mode), **F** (line focus dim), and **H** (line highlight) toggle buttons, plus a speed/distance slider.

---

## Auto-Scroll

Press `P` in rail mode to toggle **auto-scroll**. The view continuously scrolls horizontally along the current line, then advances to the next line when it reaches the edge.

- **Speed boost:** Hold `D` or `Right` during auto-scroll to double the speed.
- **Pauses:** Auto-scroll pauses briefly at line boundaries (default 400ms) and block/page transitions (default 600ms) to let your eyes settle. Configurable in Settings > Auto-Scroll.
- **Auto-scroll trigger:** Optionally, auto-scroll can start automatically after holding `D` or `Right` for a configurable delay. Enable this in Settings > Auto-Scroll > **Enable auto-scroll trigger** and set the desired hold duration.
- **Stop:** Press `Escape`, `P`, or any opposing navigation key (`Up`, `Left`).

The status bar shows a green **"Auto-Scroll"** indicator when active. Adjust speed with the rail toolbar slider or the `[` / `]` keys.

---

## Jump Mode

Press `J` in rail mode to toggle **jump mode**. Instead of continuous scrolling, `Right`/`D` and `Left`/`A` perform saccade-style jumps — advancing by a configurable percentage of the visible width (default 25%).

Hold `Shift` with `Right` or `Left` to perform a **short jump** at half the normal distance. This is useful for fine-grained positioning within a line.

This mimics natural reading eye movements and is useful for scanning text quickly. Adjust jump distance with `[` / `]` or in Settings > Rail Reading.

> **Note:** Auto-scroll and jump mode are mutually exclusive. Enabling one disables the other.

---

## Line Focus & Highlight

### Line focus dim

When enabled, line focus dim applies a smooth feathered dimming overlay to the entire page except the active line in rail mode. Non-active lines fade toward the background colour, reducing peripheral distraction while maintaining a clean visual transition.

![Line focus dim](img/line_focus_blur.png)
*Line focus dim — non-active lines are dimmed to reduce distraction*

Toggle with the `F` key, the **F** button on the rail toolbar, or in Settings > Rail Reading. Dim intensity is adjustable from 0 (off) to 1 (maximum). The line padding (how much extra space stays fully visible around the active line) is also configurable.

### Line highlight tint

The active line in rail mode can have a configurable colour tint applied as an overlay. This makes the current line stand out more clearly, especially at high magnification. Toggle independently with the `H` key or the **H** button on the rail toolbar. Line highlight works with or without line focus dim enabled.

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

Press `Ctrl+F` to open the search panel in the sidebar. The search tab sits alongside the Outline and Bookmarks tabs. Type your query — results appear automatically after a brief debounce.

![Search highlights](img/search_highlights.png)
*Search results — matches grouped by page in the sidebar, highlighted on the page in yellow with the active match in orange*

- **Results panel:** Matches are grouped by page with text snippets showing the match term in context (bolded). Click any result to jump directly to that match.
- **Navigate matches:** `Enter` / `Shift+Enter` in the search input, `F3` / `Shift+F3` globally, or the arrow buttons in the panel.
- **Case sensitivity:** Toggle with the `Aa` button.
- **Regex:** Toggle with the `.*` button for regular expression search.
- **Match count:** The panel shows the current match index and total count (e.g. "3 of 42").
- **Clear:** Click the clear button (✕) to remove all highlights and results, or press `Escape`.

---

## Annotations

Right-click anywhere on the page to open the **radial menu** with five annotation tools:

![Annotations](img/annotations.png)
*Annotations — highlights and text notes on a PDF page*

| Tool | Key | Description |
|------|-----|-------------|
| **Highlight** | `1` | Click and drag over text to highlight. Uses character-level detection. Choose from yellow, green, or pink via the colour ring. |
| **Pen** | `2` | Freehand drawing. Choose stroke thickness (thin/normal/thick) via the thickness ring, and colour (red/blue/black) via the colour ring. |
| **Rectangle** | `3` | Draw rectangular outlines or filled regions. Choose stroke thickness and colour (blue/red/black) via the radial menu rings. |
| **Text Note** | `4` | Click to place a note. Shows as a small folded-corner icon; click the icon in browse mode to expand/collapse the popup. Click an existing note in Text Note mode to edit. |
| **Eraser** | `5` | Click on an annotation to remove it. |

### Tool cursors

Each annotation tool shows a distinct mouse cursor so you always know the active mode:
- **Highlight, Pen, Rectangle, Text Note** — crosshair cursor
- **Eraser** — no-entry cursor
- **Text Select** — I-beam cursor
- **Browse (no tool)** — default arrow cursor

### Radial menu rings

The radial menu has up to three rings:

- **Inner ring** — tool selection (always visible)
- **Middle ring** — stroke thickness: thin, normal, thick (shown for Pen and Rectangle). Displayed as size-varied circles.
- **Outer ring** — colour selection (shown for Highlight, Pen, and Rectangle)

Tap a segment to expand its rings. **Selecting a thickness** keeps the menu open so you can also pick a colour. **Selecting a colour** or clicking outside the rings activates the tool and closes the menu. A small indicator dot on the segment shows the currently active colour.

### Annotation z-order

Annotations are drawn in a fixed z-order: highlights appear below freehand strokes and rectangles, which appear below text notes. Within each tier, annotations are drawn in the order they were created.

### Popup notes

Text notes display as a compact folded-corner icon (16px). In browse mode, click the icon to expand a floating popup showing the full note text with word wrapping. Click again to collapse. Double-click or use the Text Note tool to edit.

### Select, move, and resize

In **browse mode** (no annotation tool active), click on any annotation to select it (shown with a dashed blue outline). Drag a selected annotation to move it. For freehand annotations, 8 resize handles appear on the bounding box — drag a handle to scale proportionally. All move and resize actions support undo/redo.

### Delete selected annotation

Press `Delete` or `Backspace` in browse mode to remove the selected annotation. This uses the same undo-supported removal as the eraser.

### Undo and redo

`Ctrl+Z` undoes the last annotation action (including moves, resizes, and deletions). `Ctrl+Y` or `Ctrl+Shift+Z` redoes. Each tab has an independent undo/redo stack.

### Persistence

Annotations are saved automatically to internal storage (`~/.config/railreader2/annotations/` on Linux, `%APPDATA%\railreader2\annotations\` on Windows). They load automatically when you reopen the file. When the same PDF is open in multiple tabs, all tabs share the same annotation data — edits in one tab are immediately visible in the other, with independent undo/redo stacks per tab.

### Export

Use **File > Export with Annotations** to create a new PDF with annotations rendered into the pages. The original PDF is not modified.

Use **File > Export Annotations as JSON** to save all annotations and bookmarks for the current document to a JSON file. This is useful for backup, scripting, or sharing with other RailReader2 users.

Use **File > Import Annotations...** to import annotations from a JSON file. Imported annotations are merged with any existing annotations on the active document — your annotations are preserved, and the imported ones are added alongside them. Duplicate bookmarks (same name and page) are skipped.

---

## PDF Links

Clickable links embedded in PDF documents are fully supported.

### Internal links (cross-references)

Clicking an internal link — such as a citation reference, figure number, table of contents entry, or equation reference — navigates directly to the target location. The view scrolls to the exact position specified by the link destination, not just the page. This works in both browse mode and rail mode. In rail mode, links take priority over block-snapping.

### External links (URLs)

Clicking an external link (a URL) opens a confirmation dialog showing the full URL. Click **Open** to launch it in your default browser, or **Cancel** to dismiss. Only `http://` and `https://` URLs are allowed; other schemes are blocked for security.

### Back and forward

After following a link or jumping to a bookmark, press `Alt+Left` or `` ` `` (backtick) to go back. Press `Alt+Right` to go forward. Each tab maintains its own independent navigation history. The back button in the bookmarks panel also works.

### Hover feedback

When the mouse hovers over a clickable link, the cursor changes to a hand pointer. This works in browse mode (when no annotation tool is active).

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

Bookmarks are stored in the same annotation file as highlights, notes, and other annotations. They persist across sessions automatically.

---

## Figures Panel

Press `Ctrl+Shift+I` to open the **figures panel** — a browsable index of all figures, tables, and equations detected by the layout analysis model. The panel sits alongside the Outline, Bookmarks, and Search tabs in the sidebar.

### How it works

RailReader2 progressively analyses all pages in the background when idle. As pages are scanned, detected figures, tables, and equations appear in the panel. A progress indicator shows how many pages have been scanned (e.g., "12 of 20 pages scanned"). Background scanning pauses automatically during rail mode to avoid interfering with reading.

### Browsing entries

Each entry shows:

- **Figures and tables** — a thumbnail crop of the detected region
- **Equations** — the extracted text content from the PDF text layer (e.g., Unicode math symbols)

Use the **Figures**, **Tables**, and **Equations** toggle buttons at the top to filter by category. Click any entry to navigate directly to that page.

---

## Copy as LaTeX (VLM)

Press `Ctrl+L` to send the current rail block to a Vision Language Model and copy the result to the clipboard. The action adapts to the block type:

- **Equations** → copied as LaTeX
- **Tables** → copied as Markdown
- **Figures** → copied as a brief description

You can also `Ctrl+right-click` any detected block to open a context menu with **Copy as LaTeX**, **Copy as Markdown**, **Copy Description**, and **Copy Image** options.

### Setup

Open **Settings > VLM** and configure:

- **Endpoint** — the URL of an OpenAI-compatible API (e.g., `http://localhost:11434/v1` for Ollama)
- **Model** — the model name (e.g., `qwen2.5-vl:7b`, `lightonai/LightOnOCR-2-1B`, `gpt-4o`)
- **API Key** — leave blank for local endpoints that don't require authentication

Use the **Test Connection** button to verify your setup. The API key is stored locally in your config file.

### Structured JSON output

Under **Settings > VLM** there's a **Use structured JSON schema responses** checkbox (off by default). Enabling it forces the model to return a JSON object matching a strict schema (`{latex}`, `{markdown}`, or `{description}`), which produces cleaner output on capable models — no stray `$$` wrappers, code fences, or prompt echoes. Recommended for GPT-4o, Qwen2.5-VL, Gemini, and other instruction-tuned vision models served via an OpenAI-compatible API that honours `response_format: json_schema`.

Some local or OCR-specialised models (LightOnOCR, certain older Ollama builds) don't reliably support JSON schema and may return truncated or mis-escaped output. If you see errors, disable this checkbox.

### Recommended models

- **Cloud (OpenAI):** `gpt-4.1-mini` — best accuracy for equations and tables. Requires an API key from [platform.openai.com](https://platform.openai.com). Note: sends cropped block images to external servers.
- **Local (Ollama):** `qwen2.5-vl:7b` — good general-purpose vision model, no data leaves your machine
- **Local (vLLM + LightOnOCR):** `lightonai/LightOnOCR-2-1B` — specialised OCR model, fast local inference

See the [VLM setup guide](vllm-guide.md) for detailed instructions on all three options.

---

## CLI Tool

RailReader2 ships a standalone headless CLI for automated PDF extraction. Download `railreader2-cli-linux-x64.tar.gz` (Linux) or `railreader2-cli-win-x64.zip` (Windows) from [GitHub Releases](https://github.com/sjvrensburg/railreader2/releases/latest), then extract the archive. On Linux, make the binary executable with `chmod +x RailReader2.Cli`.

### ONNX model

The CLI uses the PP-DocLayoutV3 ONNX layout model. If the GUI is installed, the CLI finds the model automatically from the shared cache. If the GUI isn't installed, download the model by running `./scripts/download-model.sh` from source. The `structure` command works without the model but skips layout analysis.

### render — export pages as PNG

Renders PDF pages as PNG images, with optional colour effects and annotation overlay.

```
railreader2-cli render <pdf> [options]
```

| Option | Description |
|--------|-------------|
| `--pages <range>` | Page range, e.g. `"1,3,5-10"` (default: all) |
| `--dpi <int>` | Render DPI (default: 300) |
| `--effect <name>` | Colour effect: `none`, `highcontrast`, `highvisibility`, `amber`, `invert` |
| `--intensity <float>` | Effect intensity 0.0–1.0 (default: 1.0) |
| `--annotations` | Burn annotations into rendered pages |
| `--output-dir <path>` | Output directory (default: `./screenshots`) |

```bash
# Render first 5 pages with amber filter
railreader2-cli render paper.pdf --pages 1-5 --effect amber --output-dir ./out

# Render all pages with annotations baked in
railreader2-cli render paper.pdf --annotations --dpi 150
```

### structure — extract document structure

Extracts the PDF outline, ONNX layout blocks, and per-block text as JSON.

```
railreader2-cli structure <pdf> [options]
```

| Option | Description |
|--------|-------------|
| `--output <path>` | Output JSON file path (default: stdout) |
| `--include-text` | Include extracted text per layout block |
| `--analyze` | Run ONNX layout analysis to detect blocks |
| `--pages <range>` | Page range for analysis (default: all) |

```bash
# Full structure with layout analysis and text
railreader2-cli structure paper.pdf --analyze --include-text --output structure.json

# Just the outline (no model needed)
railreader2-cli structure paper.pdf
```

### annotations — export annotations

Exports annotations as rich JSON (with extracted text, layout block correlations, and nearest section headings) or as an annotated PDF.

```
railreader2-cli annotations <pdf> [options]
```

| Option | Description |
|--------|-------------|
| `--output <path>` | Output file path (default: stdout for JSON) |
| `--format <json\|pdf>` | Export format (default: json) |
| `--include-text` | Extract text under each annotation |
| `--include-blocks` | Correlate annotations with layout blocks (implies ONNX analysis) |

```bash
# Rich JSON export with text and layout context
railreader2-cli annotations paper.pdf --include-text --include-blocks --output annotations.json

# Export as annotated PDF
railreader2-cli annotations paper.pdf --format pdf --output paper.annotated.pdf
```

### vlm — transcribe blocks via a vision LLM

Sends detected equation, table, and figure crops to an OpenAI-compatible vision API and writes the transcriptions as JSON. Equations become LaTeX, tables become Markdown, figures become one-sentence descriptions. Works with any endpoint the GUI's **Copy as LaTeX** feature supports (OpenAI, Ollama, vLLM, LMStudio, etc.).

```
railreader2-cli vlm <pdf> [options]
```

**Selection:**

| Option | Description |
|--------|-------------|
| `--classes <list>` | Comma-separated subset: `equation`, `table`, `figure` |
| `--all` | Shortcut for all three classes |
| `--pages <range>` | Page range (e.g. `1,3,5-10`) |
| `--page <n> --block <i>` | Transcribe a single block by page + block index |
| `--min-confidence <f>` | Skip blocks below this detection confidence (0–1) |
| `--from-structure <path>` | Reuse an existing `structure --analyze` JSON instead of re-running ONNX |

**Endpoint config (override `Settings > VLM`):**

| Option | Description |
|--------|-------------|
| `--endpoint <url>` | OpenAI-compatible endpoint |
| `--model <name>` | Model identifier |
| `--api-key <key>` | API key (or set `$OPENAI_API_KEY`; blank for local endpoints) |
| `--prompt-style <style>` | `instruction` (default) or `ocr` — the latter works better with OCR-specialised models |

Per-class overrides allow routing different block types to different backends (e.g. local LightOnOCR for equations, cloud GPT-4o for figure descriptions) in a single run. Each class gets its own fallback-chained `{endpoint, model, api-key}` trio:

```
--equation-endpoint / --equation-model / --equation-api-key
--table-endpoint    / --table-model    / --table-api-key
--figure-endpoint   / --figure-model   / --figure-api-key
```

**Output & post-processing:**

| Option | Description |
|--------|-------------|
| `--output <path>` | JSON output (default: stdout) |
| `--dpi <n>` | Crop render DPI (default: 300) |
| `--concurrency <n>` | Parallel VLM requests (default: 1) |
| `--dump-crops <dir>` | Write the PNG crops to disk (useful for debugging) |
| `--no-structured-output` | Disable JSON schema response format (default: on — use when the model doesn't support strict JSON schema) |
| `--no-html-to-md` | Keep HTML tables as-is (default: convert to Markdown) |

```bash
# Transcribe every equation and table in a paper to LaTeX/Markdown
railreader2-cli vlm paper.pdf --classes equation,table \
    --endpoint https://api.openai.com/v1 --model gpt-4o-mini \
    --output transcriptions.json

# Dry run — dump the detected crops without calling any API
railreader2-cli vlm paper.pdf --all --dump-crops ./crops/

# Mixed routing: local Ollama for equations, OpenAI for figures
railreader2-cli vlm paper.pdf --classes equation,figure \
    --equation-endpoint http://localhost:11434/v1 --equation-model qwen2.5-vl:7b \
    --figure-endpoint https://api.openai.com/v1 --figure-model gpt-4o-mini \
    --output rich.json

# Reuse a pre-computed structure JSON (skip ONNX)
railreader2-cli structure paper.pdf --analyze --output structure.json
railreader2-cli vlm paper.pdf --from-structure structure.json --all --output vlm.json
```

When `--api-key` is not set, the command falls back to the `OPENAI_API_KEY` environment variable before finally consulting the saved AppConfig — letting you run the command without putting your key on the command line or in shell history.

### export — convert PDF to Markdown

Exports a PDF to structured Markdown using layout analysis, VLM transcription, and annotation extraction. Heading hierarchy is resolved by matching detected heading blocks against the PDF outline tree. Degrades gracefully depending on available tools.

```
railreader2-cli export <pdf> [options]
```

**Output:**

| Option | Description |
|--------|-------------|
| `--output <path>` | Markdown output file (default: stdout) |
| `--pages <range>` | Page range (e.g. `1,3,5-10`) |
| `--no-page-breaks` | Omit page break markers (`---`) between pages |

**Content:**

| Option | Description |
|--------|-------------|
| `--no-vlm` | Disable VLM transcription (equations/tables/figures become placeholders) |
| `--no-annotations` | Exclude annotations from output |
| `--figure-dir <dir>` | Save figure PNGs and reference them in the Markdown |

**VLM config (override `Settings > VLM`):**

| Option | Description |
|--------|-------------|
| `--endpoint <url>` | OpenAI-compatible endpoint |
| `--model <name>` | Model identifier |
| `--api-key <key>` | API key (or set `$OPENAI_API_KEY`; blank for local endpoints) |
| `--concurrency <n>` | Parallel VLM requests (default: 2) |
| `--prompt-style <style>` | `instruction` (default) or `ocr` |
| `--no-structured-output` | Disable JSON schema response format |

```bash
# Basic export (text + headings, equations/figures as placeholders)
railreader2-cli export paper.pdf --no-vlm --output paper.md

# Full fidelity with VLM (LaTeX equations, Markdown tables, figure descriptions)
railreader2-cli export paper.pdf \
    --endpoint https://api.openai.com/v1 --model gpt-4o-mini \
    --output paper.md

# Export with figure images saved to disk
railreader2-cli export paper.pdf --figure-dir ./figures --output paper.md

# Export specific pages without annotations
railreader2-cli export paper.pdf --pages 1-10 --no-annotations --output chapter1.md
```

**Graceful degradation:**

| Available tools | Behaviour |
|----------------|-----------|
| ONNX + VLM + Annotations | Full fidelity: headings, LaTeX equations, pipe tables, figure images/descriptions, annotation blockquotes |
| ONNX only (no VLM) | Headings + text + `[equation]`/`[figure]` placeholders + code-block tables |
| Neither | Plain text per page with heading markers from the PDF outline |

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
- **Line Highlight:** Toggle the active-line highlight independently (works with or without line focus dim). Choose a colour tint (Auto, Yellow, Cyan, Green, None) and set opacity.
- **Jump Distance:** Percentage of visible width for jump mode (5–80%).

### Auto-Scroll
- **Line Pause:** Pause duration at line boundaries (ms, 0 to disable).
- **Block Pause:** Pause duration at block/page boundaries (ms, 0 to disable).

### Advanced
- **Navigable Block Types:** Choose which PP-DocLayoutV3 block types are navigable in rail mode.
- **Centered Block Types:** Choose which block types are horizontally centered when they are narrower than the viewport. By default, headings (paragraph_title, doc_title) are excluded so they stay left-aligned with surrounding text, while formulae and body text are centered.
- **Analysis Lookahead:** Number of pages to pre-analyze ahead (0 to disable).

### Config file

Configuration is stored at `~/.config/railreader2/config.json` (Linux) or `%APPDATA%\railreader2\config.json` (Windows). You can edit it directly; restart the app to apply changes.

---

## Troubleshooting

RailReader2 writes a diagnostic log during each session. If you encounter a problem, the log helps developers understand what happened.

### Exporting the log

- **Help → Export Diagnostic Log...** opens a save dialog to export a copy of the current session log.
- **Help → About** shows the log file path at the bottom of the dialog. Click the copy icon next to the path to copy it to the clipboard, then attach the file to a bug report.

The log file is located at:
- **Linux:** `~/.config/railreader2/session.log`
- **Windows:** `%APPDATA%\railreader2\session.log`
- **macOS:** `~/Library/Application Support/railreader2/session.log`

The log is overwritten at the start of each session. Old `.log` files are automatically removed after 7 days by the cleanup service.

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
| `Ctrl+Shift+M` | Toggle margin cropping |
| `Ctrl+Shift+O` | Toggle outline panel |
| `Ctrl+Shift+B` | Toggle bookmarks panel |
| `Ctrl+Shift+I` | Toggle figures panel |
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
| `Alt+Left` / `` ` `` | Navigate back |
| `Alt+Right` | Navigate forward |
| `C` | Cycle colour effect on active tab |
| `F` | Toggle line focus dim |
| `H` | Toggle line highlight |
| `Ctrl+Drag` | Free pan (release Ctrl to snap back) |
| `[` / `]` | Adjust speed or jump distance |
| `Shift+[` / `Shift+]` | Adjust blur intensity |
| Click | Jump to block |

### Search & Annotations

| Key | Action |
|-----|--------|
| `Ctrl+F` | Open search panel |
| `F3` / `Shift+F3` | Next / previous match |
| `1` / `2` / `3` / `4` / `5` | Highlight / Pen / Rectangle / Text Note / Eraser |
| Right-click | Open radial menu (thickness + colour rings for Pen/Rect, colour ring for Highlight) |
| `Ctrl+Z` | Undo |
| `Ctrl+Y` | Redo |
| `Delete` / `Backspace` | Delete selected annotation (browse mode) |
| `Ctrl+L` | Copy current block as LaTeX / Markdown / description (VLM) |
| `Ctrl+C` | Copy selected text |
| `Escape` | Cancel / close / stop / exit fullscreen |

