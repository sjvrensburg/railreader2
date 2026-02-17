# railreader2

Desktop PDF viewer optimised for high magnification viewing with AI-guided "rail reading". Built in Rust with MuPDF for PDF parsing, Skia for GPU-accelerated vector rendering, and a modern egui-based GUI.

## How it works

PDF pages are converted to SVG via MuPDF, then rendered by Skia's SVG DOM renderer onto an OpenGL canvas. Zoom and pan are canvas transforms — text stays sharp at any magnification since it's rendered as vector paths with no re-rasterisation needed.

### Rail reading

At high zoom levels, navigation switches to "rail mode" — the viewer locks onto detected text blocks and advances line-by-line, like a typewriter carriage return. This is powered by PP-DocLayoutV3 (ONNX), which detects document regions (text, titles, footnotes, etc.) and predicts reading order natively via its Global Pointer Mechanism — correctly handling multi-column layouts, headers, footnotes, etc. Non-active regions are dimmed so you can focus on the current block and line.

### Features

- **Multi-tab support** — open multiple PDFs with independent per-tab state
- **Menu bar** — File, View, Navigation, Help menus with keyboard shortcuts
- **Interactive minimap** — click or drag to navigate the page
- **Outline panel** — table of contents with collapsible hierarchy
- **Settings panel** — live-editable rail reading parameters with persistence
- **About dialog** — version info and credits (Help → About)
- **Disk cleanup** — removes cache, old logs, temp files (Help → Clean Up Temp Files)
- **Analysis lookahead** — pre-analyzes upcoming pages in the background for instant navigation
- **Colour effects** — GPU-accelerated accessibility filters (High Contrast, High Visibility, Amber, Invert) with adjustable intensity
- **Debug overlay** — visualise detected layout blocks with class labels and confidence

## Usage

```bash
# Open a specific PDF
cargo run --release -- <path-to-pdf>

# Or launch without arguments and use File → Open (Ctrl+O)
cargo run --release
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
| D (shift) | Toggle debug overlay (shows detected blocks) |

### Configuration

Rail reading parameters are editable via the Settings panel (gear icon in menu bar) and persisted to `config.json`:

```json
{
  "rail_zoom_threshold": 3.0,
  "snap_duration_ms": 300.0,
  "scroll_speed_start": 60.0,
  "scroll_speed_max": 400.0,
  "scroll_ramp_time": 1.5,
  "analysis_lookahead_pages": 2,
  "colour_effect": "None",
  "colour_effect_intensity": 1.0
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

## Building

### Dependencies

- Rust toolchain
- clang/clang++ (for skia source build)
- ninja (for skia source build)
- pkg-config, fontconfig, freetype (system libraries)

On Ubuntu/Debian:

```bash
sudo apt install clang ninja-build pkg-config libfontconfig-dev libfreetype-dev
```

### ONNX model

Download the PP-DocLayoutV3 model for AI layout analysis:

```bash
./scripts/download-model.sh
```

The model is placed in `models/PP-DocLayoutV3.onnx`. Without it, a simple fallback layout (horizontal strips) is used.

### Build

```bash
cargo build --release
```

The first build takes a while as Skia compiles from source. Subsequent builds are fast (incremental).

## Debug

Set `DUMP_SVG=1` to write each page's SVG to `/tmp/pageN.svg` for inspection:

```bash
DUMP_SVG=1 cargo run --release -- document.pdf
```

Inspect layout analysis output for a specific page:

```bash
cargo run --example dump_layout -- document.pdf [page_number]
```
