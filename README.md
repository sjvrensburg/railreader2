# railreader2

Desktop PDF viewer optimised for high magnification viewing. Built in Rust with MuPDF for PDF parsing and Skia for GPU-accelerated vector rendering.

## How it works

PDF pages are converted to SVG via MuPDF, then rendered by Skia's SVG DOM renderer onto an OpenGL canvas. Zoom and pan are canvas transforms — text stays sharp at any magnification since it's rendered as vector paths with no re-rasterisation needed.

## Usage

```bash
cargo run --release -- <path-to-pdf>
```

### Controls

| Key | Action |
|-----|--------|
| PgDown / PgUp | Next / previous page |
| Home / End | First / last page |
| +/- | Zoom in / out |
| 0 | Reset zoom and position |
| Arrow keys | Pan |
| Mouse drag | Pan |
| Mouse wheel | Zoom |
| q / Esc | Quit |

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
