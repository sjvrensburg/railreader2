# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**railreader2** is a desktop PDF viewer with AI-guided "rail reading" for high magnification viewing. Built in Rust with MuPDF (PDF parsing), Skia (GPU rendering via OpenGL), and PP-DocLayoutV3 (ONNX layout detection).

## Build and Development Commands

```bash
# Build optimized release binary (preferred — debug builds are slow and large)
cargo build --release

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
├── egui_integration.rs  # egui UI framework integration
└── ui/
    ├── mod.rs           # UiState, UiAction enum, top-level build_ui()
    ├── menu.rs          # Menu bar (File, View, Navigation, Help)
    ├── tab_bar.rs       # Custom tab bar widget with styled close buttons
    ├── settings.rs      # Interactive settings window with DragValue controls
    ├── minimap.rs       # Interactive minimap overlay (click/drag to navigate)
    ├── outline.rs       # Outline/TOC side panel
    ├── about.rs         # About dialog with version info
    ├── status_bar.rs    # Status bar (page, zoom, rail mode info)
    ├── loading.rs       # Loading overlay with spinner
    └── icons.rs         # Icon helpers (placeholder for Fluent UI icons)
examples/
└── dump_layout.rs       # CLI tool to inspect layout analysis output
models/
└── PP-DocLayoutV3.onnx  # Layout model (gitignored, ~50MB)
scripts/
└── download-model.sh    # Downloads ONNX model from HuggingFace
```

### Key concepts

- **Rendering pipeline**: PDF → SVG (MuPDF) → Skia SVG DOM → OpenGL canvas. Zoom/pan are canvas transforms, text stays vector-sharp.
- **Layout analysis**: PDF page → 800px pixmap → ImageNet-normalized CHW tensor → PP-DocLayoutV3 ONNX → NMS-filtered blocks with 23 class types. Reading order comes from the model's native output (Global Pointer Mechanism, column 7 of `[N,7]` tensor). Line detection uses horizontal projection profiling on pixmap crops.
- **Rail mode**: Activates above configurable zoom threshold. Navigation locks to detected text blocks, advances line-by-line with snap animations. Horizontal scrolling is continuous hold-to-scroll with speed ramping.
- **Config**: User parameters in `config.json` (auto-created on first run, gitignored). Loaded at startup via `Config::load()`. Editable live via Settings panel; changes auto-save and propagate to all tabs.
- **Analysis caching**: Per-tab `analysis_cache: HashMap<i32, PageAnalysis>` avoids re-running ONNX inference on revisited pages. Lookahead analysis pre-processes upcoming pages one-per-frame in `handle_redraw()`.
- **Action dispatch**: UI returns `Vec<UiAction>` from `build_ui()`, processed by `App::process_actions()`. Actions include `SetCamera`, `ConfigChanged`, `RunCleanup`, etc.
- **Colour effects**: GPU-accelerated SkSL colour filters (`ColourEffect` enum) applied via `RuntimeEffect::make_for_color_filter()` and Skia save layers. Effects filter only PDF content (inside GL scissor), not egui panels. Rail-mode overlays use per-effect `OverlayPalette` colours so highlighting complements each scheme. Configurable via View → Colour Effects menu or Settings panel; effect + intensity persisted in `config.json`.

### Dependencies

- `skia-safe` 0.93 (GL + SVG features) — GPU rendering
- `glutin` 0.32 + `winit` 0.30 — OpenGL context and windowing
- `mupdf` 0.6 — PDF parsing, SVG export, pixmap rendering
- `ort` 2.0.0-rc.11 — ONNX Runtime for layout inference
- `egui` 0.31 + `egui-winit` 0.31 + `egui_glow` 0.31 — Immediate mode GUI framework
- `rfd` 0.15 — Native file dialog (parented to main window)
- `serde` + `serde_json` — Config serialization

## Configuration

Default `config.json` values (created on first run, gitignored):

```json
{
  "rail_zoom_threshold": 3.0,
  "snap_duration_ms": 300.0,
  "scroll_speed_start": 60.0,
  "scroll_speed_max": 400.0,
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

The `navigable_classes` array controls which of the 23 PP-DocLayoutV3 block types are navigable in rail mode. Values are class name strings (see `LAYOUT_CLASSES` in `layout.rs`). Adding e.g. `"formula"` enables formula blocks; removing `"document_title"` skips headings. Configurable live via Settings → Advanced: Navigable Block Types. Line detection runs for all blocks regardless of this setting, so toggling classes doesn't require ONNX re-inference.

## Key Development Notes

- **Prefer release builds.** Debug builds compile Skia from source (~15-20 min) and produce 10GB+ artifacts. Use `cargo build --release` and `cargo check` for fast iteration.
- ONNX model outputs `[N, 7]` tensors: `[class_id, confidence, xmin, ymin, xmax, ymax, reading_order]`. The 7th column is the model's predicted reading order via its Global Pointer Mechanism.
- Layout analysis runs synchronously on page load (~100-200ms). Lookahead pages are analyzed one-per-frame to avoid blocking.
- Debug SVG dumping: Set `DUMP_SVG=1` environment variable to write each page's SVG to `/tmp/pageN.svg` for inspection.
- **Modern GUI**: Full egui UI with menu bar, tab bar, outline panel, interactive minimap, settings window, about dialog, and status bar. Multi-tab support with independent per-tab state. Rendering pipeline: egui `build_ui()` → capture `content_rect` → Skia renders PDF into content area with GL scissor → egui `paint()` on top.
- **Multi-tab architecture**: Per-tab state (`TabState` in `tab.rs`) holds document, camera, rail nav, SVG DOM, outline, minimap texture, analysis cache, and pending lookahead queue. Shared state: `ort::Session`, `Config`, `EguiIntegration`, `Env`.
- App can run without CLI arguments — shows welcome screen with "Open a PDF file (Ctrl+O)" prompt.
- **Cleanup**: `cleanup::run_cleanup()` runs on startup and on-demand via Help menu. Removes `cache/` contents, `.tmp` files, and `.log` files older than 7 days. Skips `config.json`, `.lock`, and `.onnx` files.
- No unit tests currently exist in the project.
