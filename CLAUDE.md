# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**railreader2** is a desktop PDF viewer with AI-guided "rail reading" for high magnification viewing. Built in Rust with MuPDF (PDF parsing), Skia (GPU rendering via OpenGL), and PP-DocLayoutV3 (ONNX layout detection).

## Build and Development Commands

```bash
# Build optimized release binary (preferred — debug builds are slow and large)
cargo build --release

# Fast syntax/type check without linking
cargo check

# Run the application
cargo run --release -- <path-to-pdf>

# Run layout analysis diagnostic
cargo run --example dump_layout -- <pdf> [page_number]

# Run tests
cargo test

# Format code
cargo fmt

# Lint with clippy
cargo clippy

# Download ONNX model (required for AI layout)
./scripts/download-model.sh
```

**Prefer release builds.** Debug builds compile Skia from source (~15-20 min) and produce 10GB+ artifacts. Use `cargo check` for fast iteration without a full build.

On Windows, release builds suppress the console window (`windows_subsystem = "windows"`). For `println!`/logging output while debugging on Windows, use a debug build or redirect output.

## Architecture

```
src/
├── lib.rs               # Module exports, render_page_svg(), render_page_pixmap()
├── main.rs              # Winit/glutin/skia event loop, App orchestrator, rendering
├── colour_effect.rs     # SkSL GPU shaders for accessibility colour effects
├── config.rs            # User-configurable parameters (config.json, serde)
├── cleanup.rs           # Disk cleanup (cache, old logs, temp files)
├── layout.rs            # ONNX inference, NMS, reading order, line detection
├── rail.rs              # Rail navigation state machine (snap, scroll, clamp)
├── tab.rs               # Camera, TabState (per-document state), outline, analysis cache
├── worker.rs            # Background thread for async ONNX layout analysis
├── egui_integration.rs  # egui UI framework integration
└── ui/
    ├── mod.rs           # UiState, UiAction enum, top-level build_ui()
    ├── menu.rs          # Menu bar (File, View, Navigation, Help)
    ├── tab_bar.rs       # Custom tab bar widget with styled close buttons
    ├── settings.rs      # Interactive settings window with DragValue controls
    ├── minimap.rs       # Interactive minimap overlay (click/drag to navigate)
    ├── outline.rs       # Outline/TOC side panel
    ├── shortcuts.rs     # Keyboard shortcuts help dialog (F1)
    ├── about.rs         # About dialog with version info
    ├── status_bar.rs    # Status bar (page, zoom, rail mode info)
    └── loading.rs       # Loading overlay with spinner
examples/
└── dump_layout.rs       # CLI tool to inspect layout analysis output
models/
└── PP-DocLayoutV3.onnx  # Layout model (gitignored, ~50MB)
scripts/
└── download-model.sh    # Downloads ONNX model from HuggingFace
```

### Key concepts

- **Rendering pipeline**: PDF → SVG (MuPDF) → Skia SVG DOM → OpenGL canvas. Zoom/pan are canvas transforms, text stays vector-sharp.
- **Layout analysis**: PDF page → 800px pixmap → ImageNet-normalized CHW tensor → PP-DocLayoutV3 ONNX → NMS-filtered blocks with 23 class types. Reading order comes from the model's native output (Global Pointer Mechanism, column 7 of `[N,7]` tensor). Line detection uses horizontal projection profiling on pixmap crops. Input preparation runs on the main thread; ONNX inference runs on a dedicated background worker thread (`worker.rs`) to avoid blocking the UI.
- **Rail mode**: Activates above configurable zoom threshold. Navigation locks to detected text blocks, advances line-by-line with snap animations. Horizontal scrolling is continuous hold-to-scroll with speed ramping.
- **Config**: User parameters stored in the platform config directory (`~/.config/railreader2/config.json` on Linux, `%APPDATA%\railreader2\config.json` on Windows). Loaded at startup via `Config::load()`. Editable live via Settings panel; changes auto-save and propagate to all tabs.
- **Analysis caching**: Per-tab `analysis_cache: HashMap<i32, PageAnalysis>` avoids re-running ONNX inference on revisited pages. Lookahead analysis pre-processes upcoming pages one-per-frame in `handle_redraw()`.
- **Action dispatch**: UI returns `Vec<UiAction>` from `build_ui()`, processed by `App::process_actions()`. Full `UiAction` enum: `OpenFile`, `CloseTab(usize)`, `SelectTab(usize)`, `DuplicateTab`, `GoToPage(i32)`, `SetZoom(f64)`, `SetCamera(f64, f64)`, `FitPage`, `ToggleDebug`, `ToggleOutline`, `ToggleMinimap`, `SetColourEffect(ColourEffect)`, `ConfigChanged`, `RunCleanup`, `Quit`.
- **Colour effects**: GPU-accelerated SkSL colour filters (`ColourEffect` enum) applied via `RuntimeEffect::make_for_color_filter()` and Skia save layers. Effects filter only PDF content (inside GL scissor), not egui panels. Rail-mode overlays use per-effect `OverlayPalette` colours so highlighting complements each scheme. Configurable via View → Colour Effects menu or Settings panel; effect + intensity persisted in `config.json`.
- **Multi-tab architecture**: Per-tab state (`TabState` in `tab.rs`) holds document, camera, rail nav, SVG DOM, outline, minimap texture, analysis cache, and pending lookahead queue. Shared state: `ort::Session`, `Config`, `EguiIntegration`, `Env`. The `Env` struct drop order is intentional — `DirectContext` must drop before `Window` to avoid AMD GPU segfaults.

### Dependencies

- `skia-safe` 0.93 (GL + SVG features) — GPU rendering
- `glutin` 0.32 + `winit` 0.30 — OpenGL context and windowing
- `mupdf` 0.6 — PDF parsing, SVG export, pixmap rendering
- `ort` 2.0.0-rc.11 — ONNX Runtime for layout inference
- `egui` 0.31 + `egui-winit` 0.31 + `egui_glow` 0.31 — Immediate mode GUI framework
- `rfd` 0.15 — Native file dialog (parented to main window)
- `serde` + `serde_json` — Config serialization

## Configuration

Config file location: `~/.config/railreader2/config.json` (Linux) or `%APPDATA%\railreader2\config.json` (Windows). Auto-created on first run with defaults.

```json
{
  "rail_zoom_threshold": 3.0,
  "snap_duration_ms": 300.0,
  "scroll_speed_start": 10.0,
  "scroll_speed_max": 50.0,
  "scroll_ramp_time": 1.5,
  "analysis_lookahead_pages": 2,
  "ui_font_scale": 1.0,
  "colour_effect": "None",
  "colour_effect_intensity": 1.0,
  "navigable_classes": [
    "abstract", "algorithm", "aside_text", "document_title",
    "footnote", "paragraph_title", "references", "text"
  ]
}
```

The `navigable_classes` array controls which PP-DocLayoutV3 block types are navigable in rail mode. All 23 class names: `document_title`, `paragraph_title`, `text`, `page_number`, `abstract`, `table_of_contents`, `references`, `footnote`, `table`, `header`, `footer`, `algorithm`, `formula`, `formula_number`, `image`, `figure_caption`, `table_caption`, `seal`, `figure_title`, `figure`, `header_image`, `footer_image`, `aside_text`. Configurable live via Settings → Advanced. Line detection runs for all blocks regardless, so toggling classes doesn't require ONNX re-inference.

## Key Development Notes

- ONNX model outputs `[N, 7]` tensors: `[class_id, confidence, xmin, ymin, xmax, ymax, reading_order]`. The 7th column is the model's predicted reading order via its Global Pointer Mechanism.
- Layout analysis runs asynchronously via `AnalysisWorker` (background thread). Input preparation (pixmap render + tensor build) happens on the main thread; ONNX inference runs on the worker. Results are polled each frame in `handle_redraw()`. Lookahead pages are submitted one-per-frame when the worker is idle.
- Debug SVG dumping: Set `DUMP_SVG=1` environment variable to write each page's SVG to `/tmp/pageN.svg` for inspection.
- App can run without CLI arguments — shows welcome screen with "Open a PDF file (Ctrl+O)" prompt.
- **Cleanup**: `cleanup::run_cleanup()` runs on startup and on-demand via Help menu. Removes `cache/` contents, `.tmp` files, and `.log` files older than 7 days. Skips `config.json`, `.lock`, and `.onnx` files.
- No unit tests currently exist in the project.
