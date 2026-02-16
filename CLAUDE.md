# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**railreader2** is a desktop PDF viewer with AI-guided "rail reading" for high magnification viewing. Built in Rust with MuPDF (PDF parsing), Skia (GPU rendering via OpenGL), and PP-DocLayoutV3 (ONNX layout detection).

## Build and Development Commands

```bash
# Build the project
cargo build

# Build optimized release binary
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
├── lib.rs          # Module exports, render_page_svg(), render_page_pixmap()
├── config.rs       # User-configurable parameters (config.json, serde)
├── layout.rs       # ONNX inference, NMS, reading order, line detection
├── rail.rs         # Rail navigation state machine (snap, scroll, clamp)
├── main.rs         # Winit/glutin/skia event loop, rendering, input handling
examples/
└── dump_layout.rs  # CLI tool to inspect layout analysis output
models/
└── PP-DocLayoutV3.onnx  # Layout model (gitignored, ~50MB)
scripts/
└── download-model.sh    # Downloads ONNX model from HuggingFace
```

### Key concepts

- **Rendering pipeline**: PDF → SVG (MuPDF) → Skia SVG DOM → OpenGL canvas. Zoom/pan are canvas transforms, text stays vector-sharp.
- **Layout analysis**: PDF page → 800px pixmap → ImageNet-normalized CHW tensor → PP-DocLayoutV3 ONNX → NMS-filtered blocks with 23 class types. Reading order comes from the model's native output (Global Pointer Mechanism, column 7 of `[N,7]` tensor). Line detection uses horizontal projection profiling on pixmap crops.
- **Rail mode**: Activates above configurable zoom threshold. Navigation locks to detected text blocks, advances line-by-line with snap animations. Horizontal scrolling is continuous hold-to-scroll with speed ramping.
- **Config**: User parameters in `config.json` (auto-created on first run, gitignored). Loaded at startup via `Config::load()`.

### Dependencies

- `skia-safe` 0.93 (GL + SVG features) — GPU rendering
- `glutin` 0.32 + `winit` 0.30 — OpenGL context and windowing
- `mupdf` 0.6 — PDF parsing, SVG export, pixmap rendering
- `ort` 2.0.0-rc.11 — ONNX Runtime for layout inference
- `serde` + `serde_json` — Config serialization

## Configuration

Default `config.json` values (created on first run, gitignored):

```json
{
  "rail_zoom_threshold": 3.0,
  "snap_duration_ms": 300.0,
  "scroll_speed_start": 60.0,
  "scroll_speed_max": 400.0,
  "scroll_ramp_time": 1.5
}
```

## Key Development Notes

- First build is slow (~15min) due to Skia C++ compilation. Subsequent builds are fast.
- The `target/debug/` directory can grow very large (10GB+) due to Skia build artifacts. Use `cargo clean` cautiously.
- ONNX model outputs `[N, 7]` tensors: `[class_id, confidence, xmin, ymin, xmax, ymax, reading_order]`. The 7th column is the model's predicted reading order via its Global Pointer Mechanism.
- Layout analysis runs synchronously on page load (~100-200ms). Input is blocked during analysis.
- Debug SVG dumping: Set `DUMP_SVG=1` environment variable to write each page's SVG to `/tmp/pageN.svg` for inspection.
- No unit tests currently exist in the project.
