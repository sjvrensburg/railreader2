<p align="center">
  <img src="assets/railreader2.png" alt="railreader2" width="128">
</p>

<h1 align="center">railreader2</h1>

<p align="center">
  Desktop PDF viewer optimised for high magnification viewing with AI-guided "rail reading".<br>
  Built with .NET/Avalonia, PDFtoImage (PDFium) for PDF rasterisation, SkiaSharp for GPU-accelerated rendering, and PP-DocLayoutV3 (ONNX) for layout detection.
</p>

<p align="center">
  <a href="https://github.com/sjvrensburg/railreader2/releases/latest">Download</a> &middot;
  <a href="https://sjvrensburg.github.io/railreader2/">Website</a>
</p>

---

<p align="center">
  <img src="docs/img/full_page_view_with_analysis.png" alt="Layout analysis overlay" width="45%">
  &nbsp;
  <img src="docs/img/rail_mode.png" alt="Rail mode" width="45%">
</p>

## How it works

PDF pages are rasterised by PDFium (via PDFtoImage) at a DPI proportional to the current zoom level (150–600 DPI). The resulting bitmap is uploaded to the GPU as an `SKImage` and drawn on an Avalonia Skia canvas via `ICustomDrawOperation`. Camera pan and zoom are applied as a compositor-level `MatrixTransform` — the bitmap only re-renders when the DPI tier changes, not on every pan/zoom frame.

### Rail reading

At high zoom levels, navigation switches to "rail mode" — the viewer locks onto detected text blocks and advances line-by-line, like a typewriter carriage return. This is powered by PP-DocLayoutV3 (ONNX), which detects document regions (text, titles, footnotes, etc.) and predicts reading order natively via its Global Pointer Mechanism — correctly handling multi-column layouts, headers, footnotes, etc. Non-active regions are dimmed so you can focus on the current block and line.

### Features

- **Multi-tab support** — open multiple PDFs with independent per-tab state
- **Menu bar** — File, View, Navigation, Help menus with keyboard shortcuts
- **Interactive minimap** — click or drag to navigate the page
- **Outline panel** — table of contents with collapsible hierarchy
- **Settings panel** — live-editable rail reading parameters with persistence
- **Keyboard shortcuts dialog** — press F1 or Help → Keyboard Shortcuts for a complete reference
- **Click-to-select block** — click on any detected block in rail mode to jump to it
- **About dialog** — version info and credits (Help → About)
- **Disk cleanup** — removes cache, old logs, temp files (Help → Clean Up Temp Files)
- **Analysis lookahead** — pre-analyzes upcoming pages in the background for instant navigation
- **Colour effects** — GPU-accelerated accessibility filters (High Contrast, High Visibility, Amber, Invert) with adjustable intensity
- **Debug overlay** — visualise detected layout blocks with class labels and confidence

## Usage

```bash
# Open a specific PDF
dotnet run --project src/RailReader2 -- <path-to-pdf>

# Or launch without arguments and use File → Open (Ctrl+O)
dotnet run --project src/RailReader2
```

### Controls

| Key | Action |
|-----|--------|
| Ctrl+O | Open file |
| Ctrl+W | Close tab |
| Ctrl+Tab | Next tab |
| Ctrl+Q / Esc | Quit |
| PgDown / PgUp | Next / previous page |
| Home / End | First / last page |
| +/- | Zoom in / out |
| 0 | Reset zoom and position |
| Arrow Down / Up (S / W) | Next / previous line (rail mode) or pan |
| Arrow Right / Left (D / A) | Hold to scroll along line (rail mode) or pan |
| Ctrl + Mouse wheel | Horizontal scroll along line (rail mode) |
| Mouse drag | Pan |
| Mouse wheel | Zoom towards cursor |
| Click on block | Jump to block (rail mode) |
| D (shift) | Toggle debug overlay (shows detected blocks) |
| F1 | Keyboard shortcuts dialog |

### Configuration

Rail reading parameters are editable via the Settings panel (gear icon in menu bar) and persisted to the platform config directory (`~/.config/railreader2/config.json` on Linux, `%APPDATA%\railreader2\config.json` on Windows):

```json
{
  "rail_zoom_threshold": 3.0,
  "snap_duration_ms": 300.0,
  "scroll_speed_start": 10.0,
  "scroll_speed_max": 50.0,
  "scroll_ramp_time": 1.5,
  "analysis_lookahead_pages": 2,
  "colour_effect": "None",
  "colour_effect_intensity": 1.0,
  "navigable_classes": [
    "abstract", "algorithm", "aside_text", "document_title",
    "footnote", "paragraph_title", "references", "text"
  ]
}
```

| Parameter | Description |
|-----------|-------------|
| `rail_zoom_threshold` | Zoom level at which rail mode activates |
| `snap_duration_ms` | Duration of line-snap animations (ms) |
| `scroll_speed_start` | Initial horizontal scroll speed (page points/sec) |
| `scroll_speed_max` | Maximum scroll speed after holding (page points/sec) |
| `scroll_ramp_time` | Seconds to reach max speed from start |
| `analysis_lookahead_pages` | Number of pages to pre-analyze ahead (0 to disable) |
| `colour_effect` | Colour filter: `None`, `HighContrast`, `HighVisibility`, `Amber`, `Invert` |
| `colour_effect_intensity` | Effect intensity from 0.0 (off) to 1.0 (full) |
| `navigable_classes` | Which block types rail mode navigates (array of class names). Configurable via Settings → Advanced. Add `"formula"` to include formulas, remove `"document_title"` to skip headings, etc. |

## Building

### Dependencies

- .NET 10 SDK
- ONNX model (see below)

### ONNX model

Download the PP-DocLayoutV3 model for AI layout analysis:

```bash
./scripts/download-model.sh
```

The model is placed in `models/PP-DocLayoutV3.onnx`. Without it, a simple fallback layout (horizontal strips) is used.

### Build

```bash
dotnet build src/RailReader2
```

### Publish self-contained

```bash
# Linux
dotnet publish src/RailReader2 -c Release -r linux-x64 --self-contained

# Windows
dotnet publish src/RailReader2 -c Release -r win-x64 --self-contained
```
