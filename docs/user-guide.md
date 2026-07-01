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
7. [Freeze Panes](#freeze-panes)
8. [Colour Effects](#colour-effects)
9. [Search](#search)
10. [Annotations](#annotations)
11. [PDF Links](#pdf-links)
12. [Text Selection](#text-selection)
13. [Bookmarks](#bookmarks)
14. [Index Pane](#index-pane)
15. [Portals](#portals)
16. [Copy as LaTeX (VLM)](#copy-as-latex-vlm)
17. [CLI Tool](#cli-tool)
18. [Settings](#settings)
19. [Troubleshooting](#troubleshooting)
20. [Keyboard Shortcuts](#keyboard-shortcuts)
21. [Menu Bar](#menu-bar)

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

Press `Ctrl+M` to toggle the **minimap** — a page thumbnail in the corner. Click or drag inside it to navigate. Hover over the minimap to reveal the **grip handle** along the top edge and the **resize handle** at the corner pointing into the screen (top-left when docked bottom-right). Drag the grip to move, drag the resize handle to grow or shrink. The minimap maintains the page's aspect ratio, and both position and size persist across sessions.

When you enlarge the minimap past its thumbnail resolution, it transparently switches to rendering from the primary view's high-DPI page bitmap so the enlarged thumbnail stays crisp.

The **side panel** is a single-open accordion with five sections — **Outline**, **Bookmarks**, **Index**, **Search**, and **Comments**. Opening one section collapses the others, so the open section always fills the panel. Toggle the whole panel with the **sidebar button** (the panel icon at the left of the tab strip), or jump straight to a section: `Ctrl+Shift+O` opens **Outline** (table of contents — click entries to jump to sections), `Ctrl+Shift+B` opens **Bookmarks** (see [Bookmarks](#bookmarks)), `Ctrl+Shift+I` opens the **Index** pane (a browsable index of all detected figures, tables, and equations — see [Index Pane](#index-pane)), and `Ctrl+F` opens **Search**.

When you click an entry in any section — an outline heading, a search result, a bookmark, a figure — keyboard focus moves to the page, so scrolling immediately drives the document rather than the list. (In the Outline, arrow keys still browse the tree; only a mouse click hands focus to the page.)

### Multi-tab

Open multiple PDFs in tabs with `Ctrl+O`. Each tab has independent zoom, position, and analysis state. Switch tabs with `Ctrl+Tab` or by clicking. Drag tabs to reorder.

**Right-click any tab** to open a context menu with:
- **Duplicate Tab** — opens the same PDF in a new tab
- **Close Tab** — closes the tab

Opening a file that is already open — or duplicating a tab — does not load the PDF a second time. The two tabs share one underlying document: the PDF handle, the layout/text caches, and the annotations are shared (so there is no duplicate analysis work, and an annotation made in one tab appears in the other), while each tab keeps its own page, zoom, and rail position.

**Tab bar overflow:** When many tabs are open, they shrink with ellipsis text. Use the mouse wheel to scroll the tab bar horizontally, or click the overflow button (a downward chevron) at the end of the tab bar to see all tabs and jump to one.

Switching tabs automatically exits any active annotation mode to prevent accidental edits on the wrong document.

### Split panes and tear-off windows

To see one document at several positions at once — for example, keeping a figure in view while you read the text that discusses it — you can split the viewport:

- **Split Right** (View ▸ Split Editor ▸ Split Right, or `Ctrl+\`) adds another pane beside the current one. Panes sit side by side and are resized by dragging the splitter between them. Add as many as you like; close one with **Close Pane** (`Ctrl+Shift+\`), or **Close All Extra Panes** to return to a single view.
- **Move Pane to New Window** (in the same menu) tears the focused pane off into its own floating, always-on-top window for a second monitor.

Every pane and window is an independent viewport showing the same document with its own page, zoom, and rail position. **Click a pane to focus it** — keyboard, scroll, and menu commands then act on the focused pane. Closing all extra panes returns to a single view.

---

## Rail Mode

Rail mode is the core feature of railreader2. When you zoom past the threshold (default 3x), the AI layout analysis detects text blocks and reading order, and navigation locks to those blocks. Non-active regions are dimmed so you can focus on the current block and line.

![Rail mode](img/rail_mode.png)
*Rail mode — line-by-line reading at high magnification with the current line highlighted*

### Free pan

Hold `Ctrl` and drag to temporarily pan freely, even zooming out below the rail threshold. While you pan, the page is drawn clean — the rail dim and line overlay are suppressed so nothing obscures the figure you're inspecting. This lets you quickly check a figure, equation, or footnote elsewhere on the same page without losing your place. Release `Ctrl` to snap back to your original reading position and zoom level.

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

Press `P` in rail mode to toggle **semi-automatic auto-scroll**. The view flows through prose line by line on its own — but instead of running unattended through the whole document, it **parks** (stops and waits for you) whenever it reaches something worth pausing on, then continues at your signal. This keeps the easy, mechanical part automatic while leaving the "am I done with this?" decision to you.

- **Where it parks:** auto-scroll stops on arrival at a non-prose block — a display equation, table, figure, or heading — and at the end of a column and a page. Continuous prose flows straight through, even across paragraph breaks.
- **Continuing:** while parked, press `D` or `S` (or `Right`/`Down`) to resume flow. The status bar and a small on-page banner show **"Parked — press D to continue"** so a stop never looks like a freeze.
- **Inspect while parked:** pan and zoom (and `Ctrl`+drag free-pan) stay fully live while parked, so you can study a parked equation or figure for as long as you like before continuing.
- **Reading beat:** within prose, a brief pause is held at the end of every line before moving to the next, giving your eyes time to settle. If the move between lines feels too quick, raise the **Snap duration** (Settings > Rail Reading).
- **Speed:** adjust with the rail toolbar slider or the `[` / `]` keys; holding `D`/`Right` during flow temporarily boosts speed.
- **What parks:** the set of block types that park is configurable — Settings > Auto-Scroll > **Park On** (headings, equations, tables, figures by default; uncheck any to flow through it instead).
- **Auto-scroll trigger:** optionally, auto-scroll can start automatically after holding `D` or `Right` for a configurable delay. Enable this in Settings > Auto-Scroll > **Enable auto-scroll trigger** and set the desired hold duration.
- **Stop:** press `Escape`, `P`, or an opposing navigation key (`Up`, `Left`) to exit auto-scroll entirely.

The status bar shows an **"Auto-Scroll"** indicator while flowing and **"Parked — press D to continue"** while stopped.

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

## Freeze Panes

Pin part of a page in place — like Excel's *Freeze Panes* — so a header row, a label column, or both stay visible while the rest of the page scrolls under them. It is **page-wide and works on any page**, not just detected tables, and it does not depend on table detection at all.

Open the **Freeze** button (the snowflake) on the toolbar — it is always available whenever a page is loaded — and pick a mode:

- **Rows** — then click in the page to freeze everything **above** a horizontal line.
- **Columns** — then click to freeze everything **left of** a vertical line.
- **Both** — then click to freeze above **and** left of a crossing point. Shortcut: press `Z` to arm "both" directly.

The pointer shows a guide line while a placement is armed; the click drops the split exactly where you point — there is no snapping to detected boundaries (which are sometimes wrong). The two axes compose: freeze rows first, then add a column freeze to build up to "both". The frozen strips stay pinned in place, re-rendered crisply as you zoom, while the body scrolls freely.

Each split pane and tear-off window freezes **independently**, and a freeze belongs to the page it was set on — it clears automatically when that view moves to another page. To release it, click **Unfreeze** in the Freeze flyout, use the **❄ Frozen — Unfreeze** chip that appears in the frozen pane's corner, or press `Z` again. Press `Escape` to cancel a placement you've armed but not yet dropped.

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

Press `Ctrl+F` to open the **Search** section of the side panel (one of the accordion sections, alongside Outline, Bookmarks, Index, and Comments). Type your query — results appear automatically after a brief debounce. Clicking a result jumps to the match and hands keyboard focus back to the page.

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

### Comments pane

The side panel's **Comments** section gathers every text note and reviewer comment in the document into one scrollable list, so you can read or jump between them without scrolling the pages.

- **Sources:** your own text-note annotations and **in-PDF reviewer comments** — comments authored in other PDF tools (Acrobat, Preview, etc.) and embedded in the file — are shown together.
- **Filter:** use the **All / Reviewer / Yours** filter at the top to narrow the list by source.
- **Jump:** click any entry to navigate to its page; focus returns to the page so you can keep scrolling.
- **Review state:** for in-PDF reviewer comments, you can change the review state inline from the list.

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

Press `Ctrl+Shift+B` to open the **Bookmarks** section of the side panel. Each bookmark shows its name and page number.

- **Navigate:** Click a bookmark to jump to that page (zoom resets to fit the page).
- **Rename:** Click the **Rename** button on a bookmark to change its name.
- **Delete:** Click the **Delete** button to remove a bookmark.
- **Back:** After navigating to a bookmark, a **"Back to previous location"** button appears at the top of the list. Click it or press `` ` `` (backtick) to return to where you were.

### Duplicate handling

If you bookmark a page that already has a bookmark, the existing bookmark's name is updated instead of creating a duplicate.

### Persistence

Bookmarks are stored in the same annotation file as highlights, notes, and other annotations. They persist across sessions automatically.

---

## Index Pane

Press `Ctrl+Shift+I` to open the **Index** pane — a browsable index of all figures, tables, and equations detected by the layout analysis model. It is one of the side-panel accordion sections (alongside Outline, Bookmarks, Search, Comments, and Portals). Click an entry to jump to it, or **right-click** it to open it in the detachable [portal](#portals) pop-out window.

### How it works

RailReader2 progressively analyses all pages in the background when idle. As pages are scanned, detected figures, tables, and equations appear in the pane. A progress indicator shows how many pages have been scanned (e.g., "12 of 20 pages scanned"). Background scanning pauses automatically during rail mode to avoid interfering with reading.

### Scan All

Background analysis only reaches pages near where you have been reading. To index the **entire** document at once, click **Scan All** at the top of the pane — it sweeps every page for figures, tables, and equations and reports progress as it goes. The result is kept with the document, so switching tabs and back does not lose it. Use this when you want a complete figure index immediately rather than waiting for the background pass.

### Browsing entries

Each entry shows:

- **Figures and tables** — a thumbnail crop of the detected region
- **Equations** — the extracted text content from the PDF text layer (e.g., Unicode math symbols)

Use the **Figures**, **Tables**, and **Equations** toggle buttons at the top to filter by category. Click any entry to navigate to that page (focus returns to the page so you can scroll straight away). **Right-click** an entry instead to open it in the [portal pop-out window](#pop-out-window) — a quick way to park a figure on a second monitor while you keep reading; **Lock** it there to keep it pinned.

---

## Portals

A **portal** links a reference in the text — a "see Figure 3", a "cf. Table 2", an "(Eq. 7)" — to the figure, table, or equation it points to. Once linked, the target stays in view while you rail-read past the reference, so you never have to scroll away from the paragraph to see what it cites. The linked target appears in the side panel's **Portals** section and, optionally, in a detachable always-on-top pop-out window you can drag to a second monitor.

### Creating a portal

Right-click a detected block on the page and choose **Create Portal — Keep This Block In View While Reading**. Or use the two-step **Set as Portal Target** → **Link Target to Current Paragraph** when it is easier to mark the figure first and find the reference afterwards.

Sources are **line-precise**: the link fires at the exact line you were on, so several references in one paragraph (line 2 → Figure 3, line 8 → Figure 4) each surface their own target in turn. The shown target stays pinned until you reach a *different* portal's source, so it never flickers as you scroll.

### On-page markers

Subtle, always-on markers show where portals are anchored on the current page: a small **gutter dot** beside each source line and a **corner badge** on each target block. The currently-shown portal is drawn accented. Click a marker to act on it — a source shows its target, a target jumps back to its source; a marker standing for several portals opens a chooser.

### Pop-out window

Click **Pop out ↗** in the Portals pane (or click the docked preview) to detach the target into a floating, borderless, always-on-top window — useful on a multi-monitor setup. The window hosts a **live viewport** of the target rather than a still image, so you can rail-read, freeze panes, and annotate inside it just like the main view; it follows the reading position and re-aims as you cross new references. Drag its top bar to move it, the corner grip to resize; scroll to zoom and drag to pan inside it, double-click to fit. The **Pin** toggle controls always-on-top, **Lock** freezes the current target so reading on (or an auto-pin) won't replace it until you unlock, and **Dock** returns it to the panel. Its size and position are remembered between pop-outs.

### Temporary peek

To glance at a block without saving a link, right-click it and choose **Open in Portal (Temporary)** — or right-click any entry in the [Index pane](#index-pane). It opens in the pop-out window only, leaves any saved portal's tracking untouched, and dismisses itself once you read on (unless you **Lock** it).

### Managing portals

The Portals pane lists every portal in the document. Rename one inline, click **Go to source** to jump to and frame its reference, or **Delete** to remove it. Portals are saved per-document in a sidecar keyed by the PDF's path, so they persist across sessions.

> **Acknowledgement:** the portal concept — and the name — is borrowed from [Sioyek](https://sioyek.info/), a PDF reader for research papers whose Portals feature inspired this one.

---

## Copy as LaTeX (VLM)

Press `Ctrl+L` to send the current rail block to a Vision Language Model and copy the result to the clipboard. The action adapts to the block type:

- **Equations** → copied as LaTeX
- **Tables** → copied as Markdown
- **Figures** → copied as a brief description

You can also `Ctrl+right-click` any detected block to open a context menu with **Copy as LaTeX**, **Copy as Markdown**, **Copy Description**, and **Copy Image** options. The same four actions are available from the **Edit menu** (Copy Block as LaTeX / Markdown / Description / Image), where they act on the current rail block.

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

- **Cloud (OpenAI):** `gpt-5.4-nano-2026-03-17` — best accuracy for equations and tables. Requires an API key from [platform.openai.com](https://platform.openai.com). Note: sends cropped block images to external servers.
- **Local (Ollama):** `qwen2.5-vl:7b` — good general-purpose vision model, no data leaves your machine
- **Local (vLLM + LightOnOCR):** `lightonai/LightOnOCR-2-1B` — specialised OCR model, fast local inference

See the [VLM setup guide](vllm-guide.md) for detailed instructions on all three options.

---

## CLI Tool

RailReader2 ships a standalone headless CLI for automated PDF extraction. Download `railreader2-cli-linux-x64.tar.gz` (Linux) or `railreader2-cli-win-x64.zip` (Windows) from [GitHub Releases](https://github.com/sjvrensburg/railreader2/releases/latest), then extract the archive. On Linux, make the binary executable with `chmod +x RailReader2.Cli`.

### ONNX model

The CLI uses the Docling Heron-INT8 ONNX layout model by default, with PP-DocLayoutV3 available as an alternative. If the GUI is installed, the CLI finds the models automatically from the shared cache. If the GUI isn't installed, download the models by running `./scripts/download-model.sh` from source. The `structure` command works without the model but skips layout analysis.

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
    --endpoint https://api.openai.com/v1 --model gpt-5.4-nano-2026-03-17 \
    --output transcriptions.json

# Dry run — dump the detected crops without calling any API
railreader2-cli vlm paper.pdf --all --dump-crops ./crops/

# Mixed routing: local Ollama for equations, OpenAI for figures
railreader2-cli vlm paper.pdf --classes equation,figure \
    --equation-endpoint http://localhost:11434/v1 --equation-model qwen2.5-vl:7b \
    --figure-endpoint https://api.openai.com/v1 --figure-model gpt-5.4-nano-2026-03-17 \
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
    --endpoint https://api.openai.com/v1 --model gpt-5.4-nano-2026-03-17 \
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

### Rendering
- **Render Quality:** Pick a render-DPI preset — **Ultra** (800 DPI), **Quality** (600), **High** (525, the default), **Balanced** (450), **Medium** (400), **Performance** (350), or **Custom**. Higher presets re-rasterise pages at a greater DPI cap for sharper text and deeper zoom, at the cost of more memory and more frequent re-renders; lower presets favour fluidity. The change applies to the open page immediately — no restart.
- **Custom (Max render DPI / Tier step):** When **Custom** is selected, set your own maximum DPI (150–1200) and tier step (the DPI granularity at which the page re-rasterises; smaller steps render more crisply at intermediate zoom but re-raster more often). Values are clamped to the supported range.

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
- **Line Pause:** The per-line reading beat — the pause held at the end of every line before moving to the next (ms, 0 to disable).
- **Park On:** Which block types auto-scroll parks on when it reaches them (headings, equations, tables, figures by default). Unchecked types flow through like prose; column and page breaks always park.
- **Enable auto-scroll trigger / Trigger delay:** Optionally auto-start auto-scroll after holding `D`/`Right` for the set delay.

### Advanced
- **Layout Model:** Choose between Docling Heron-INT8 (default, bundled, ~66 MB) and PP-DocLayoutV3 (alternative, ~50 MB). See the [Heron layout model guide](heron-layout-model.md) for installation instructions and trade-offs.
- **Custom Layout Model:** Optionally replace the built-in model with your own ONNX (PP-style I/O contract) + class-mapping JSON.
- **Navigable Block Types:** Choose which block types are navigable in rail mode. Roles are model-independent.
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
| `Ctrl+\` | Split editor (add a pane to the right) |
| `Ctrl+Shift+\` | Close the focused pane |
| `Ctrl+Q` | Quit |
| `Ctrl+,` | Settings |
| `Ctrl+M` | Toggle minimap |
| `Ctrl+Shift+M` | Toggle margin cropping |
| `Ctrl+Shift+O` | Open Outline section |
| `Ctrl+Shift+B` | Open Bookmarks section |
| `Ctrl+Shift+I` | Open Index section (figures / tables / equations) |
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
| `R` | Start rail here — then click where to begin (rail-reads at the current zoom) |
| `Z` | Freeze panes (both axes) / unfreeze |
| `Down` / `S` | Next line |
| `Up` / `W` | Previous line |
| `Right` / `D` | Scroll forward (hold) |
| `Left` / `A` | Scroll backward (hold) |
| `Shift+Right` / `Shift+Left` | Short jump — half distance (jump mode) |
| `Home` / `End` | Line start / end |
| `P` | Toggle auto-scroll (semi-automatic; `D`/`S` to continue when parked) |
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

---

## Menu Bar

Every command is reachable from the menu bar by name — handy for discovery, keyboard navigation, and assistive technologies. There are six menus:

- **File** — open, duplicate / close tab, export & import annotations, settings, quit.
- **Edit** — find, annotation mode, undo / redo, and **Copy Block as LaTeX / Markdown / Description / Image** (the same VLM block actions as the `Ctrl+right-click` context menu, acting on the current rail block).
- **View** — zoom, side panels, minimap, fullscreen, **Split Editor** (split right, move pane to a new window, close panes), debug overlay, colour effects.
- **Rail** — the rail-reading toggles: **Auto-Scroll**, **Jump Mode**, **Line Focus Dim**, **Line Highlight**, and **Add Bookmark** (mirroring the `P` / `J` / `F` / `H` / `B` shortcuts).
- **Navigation** — go to / previous / next / first / last page, and semantic **Jump to Next / Previous** heading, figure, table, or equation.
- **Help** — keyboard shortcuts, about, diagnostic log, clean-up.

**Availability.** Menu items grey out when their action isn't currently possible: *Export with Annotations* is disabled for an encrypted (password-protected) PDF — a flattened copy would be unencrypted, so it's refused; the block-copy items need a configured VLM endpoint; and document-dependent commands are disabled when no document is open.

**Access keys (mnemonics).** Each menu and item carries an `Alt`+letter access key — the underlined letter in its label. Press and hold `Alt` to reveal them, then the letter to activate: `Alt+F` File, `Alt+E` Edit, `Alt+V` View, `Alt+R` Rail, `Alt+N` Navigation, `Alt+H` Help. Access keys are kept distinct within each menu — for example *Copy Block as Markdown* uses `Alt+K` (not `Alt+M`, which already belongs to *Annotation Mode*).

