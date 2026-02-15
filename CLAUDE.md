# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**railreader2** is a desktop PDF viewer optimized for high magnification viewing. The application is built in Rust.

## Build and Development Commands

```bash
# Build the project
cargo build

# Build optimized release binary
cargo build --release

# Run the application (debug build)
cargo run

# Run the application (release build)
cargo run --release

# Run tests
cargo test

# Run a specific test
cargo test test_name -- --nocapture

# Run tests with all output shown
cargo test -- --nocapture

# Format code
cargo fmt

# Check code without building
cargo check

# Lint with clippy
cargo clippy

# Generate documentation
cargo doc --open
```

## Project Structure

The project uses standard Rust/Cargo layout:
- `src/` - Source code
- `Cargo.toml` - Project manifest with dependencies
- `target/` - Build output (gitignored)

Expected high-level architecture for a PDF viewer:
- **PDF handling**: Dependency on a PDF library (e.g., `pdfium-render`, `mupdf-rs`)
- **UI framework**: Desktop GUI framework (e.g., `druid`, `iced`, `gtk-rs`)
- **Rendering**: High-quality PDF rendering optimized for zoom/magnification
- **Event handling**: Input handling for panning, zooming, navigation

## Key Development Notes

- The project is configured with Rust best practices (rustfmt, clippy, mutation testing support in .gitignore)
- Focus on accessibility and rendering quality for high magnification viewing
- Consider performance optimization for large PDF files with zoom capabilities
