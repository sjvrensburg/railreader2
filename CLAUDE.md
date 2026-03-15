# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**railreader2** is a desktop PDF viewer with AI-guided "rail reading" for high magnification viewing. Built in C# with .NET 10, Avalonia 11 (UI framework), PDFtoImage/PDFium (PDF rasterisation), SkiaSharp 3 (GPU rendering via Avalonia's Skia backend), and PP-DocLayoutV3 (ONNX layout detection).

## Build and Development Commands

```bash
# Build app + CLI + tests (default solution, excludes AI agent)
dotnet build RailReader2.slnx

# Build everything including the AI agent CLI
dotnet build RailReader2-full.slnx

# Run the application (-- separates dotnet args from app args)
dotnet run -c Release --project src/RailReader2 -- <path-to-pdf>

# Run without arguments (shows welcome screen)
dotnet run -c Release --project src/RailReader2

# Run tests (all)
dotnet test tests/RailReader.Core.Tests

# Run specific test class
dotnet test tests/RailReader.Core.Tests --filter "ClassName=RailReader.Core.Tests.CameraTests"

# Run specific test method
dotnet test tests/RailReader.Core.Tests --filter "FullyQualifiedName~TestMethodName"

# Run the CLI (one-shot or REPL)
dotnet run -c Release --project src/RailReader.Cli -- document open test.pdf
dotnet run -c Release --project src/RailReader.Cli -- repl
dotnet run -c Release --project src/RailReader.Cli -- --json text extract --page 1

# Run the AI agent CLI (requires RailReader2-full.slnx or direct project build)
dotnet run --project src/RailReader.Agent -- "Open test.pdf and tell me how many pages it has"

# Publish self-contained release
dotnet publish src/RailReader2 -c Release -r linux-x64 --self-contained   # Linux
dotnet publish src/RailReader2 -c Release -r win-x64 --self-contained     # Windows

# Download ONNX model (required for AI layout)
./scripts/download-model.sh
```

**Always use `-c Release`** — debug builds are significantly slower.

## Architecture

```
RailReader2.slnx              # Default: app + core + CLI + tests (no agent)
├── src/RailReader.Core/        # UI-free library: all business logic, models, services
├── src/RailReader2/            # Thin Avalonia UI shell (references Core)
├── src/RailReader.Cli/         # Command-line interface (System.CommandLine, references Core)
└── tests/RailReader.Core.Tests/# xUnit headless tests against Core

RailReader2-full.slnx         # Full: includes experimental AI agent CLI
└── src/RailReader.Agent/       # AI agent CLI via Microsoft.Extensions.AI (references Core)
```

### RailReader.Core (UI-free library)

All business logic with zero Avalonia dependencies. Key files:

- `DocumentController.cs` — headless controller facade (orchestration, animation tick loop, viewport, search, annotation tool state)
- `DocumentState.cs` — per-document state (PDF, camera, rail nav, analysis cache, annotations, bookmarks)
- `Models/` — data models (Annotations, BookmarkEntry, Camera, LayoutBlock, etc.)
- `Services/AnalysisWorker.cs` — background ONNX inference thread (`Channel<T>` queue)
- `Services/RailNav.cs` — rail navigation state machine (snap, scroll, clamp, auto-scroll, jump mode)
- `Services/PdfService.cs` — PDF rendering, DPI scaling, page info
- `Services/PdfTextService.cs` — text extraction with per-character bounding boxes
- `Services/AppConfig.cs` — config persistence (`~/.config/railreader2/config.json`)
- `Services/ColourEffectShaders.cs` — SkSL shaders (HighContrast, HighVisibility, Amber, Invert)
- `Services/AnnotationService.cs` — JSON sidecar persistence for annotations and bookmarks

### RailReader2 (Avalonia UI shell)

Thin wrapper delegating all logic to `DocumentController`/`DocumentState` in Core.

- `ViewModels/MainWindowViewModel.cs` — thin wrapper handling Avalonia-specific concerns (file dialogs, clipboard, invalidation)
- `ViewModels/TabViewModel.cs` — `[ObservableProperty]` wrapper for `DocumentState` binding
- `Views/MainWindow.axaml.cs` — keyboard shortcuts, camera transform, animation frame scheduling
- `Views/` — layers (PdfPageLayer, RailOverlayLayer, AnnotationLayer, SearchHighlightLayer), dialogs, panels
- `Controls/RadialMenu.cs` — Skia-rendered radial context menu with Font Awesome icons

### RailReader.Cli (command-line interface)

Native C# CLI built on `System.CommandLine` (v2.0.5). References `RailReader.Core` directly — no subprocess overhead. Included in the default solution.

- `Program.cs` — entry point, root command setup, global `--json` option
- `CliSession.cs` — shared session state (`DocumentController`, `AppConfig`, ONNX worker)
- `Commands/` — one file per command group: `DocumentCommands`, `TextCommands`, `NavCommands`, `AnalysisCommands`, `AnnotationCommands`, `BookmarkCommands`, `ConfigCommands`, `ExportCommands`, `ReplCommand`
- `Output/` — `IOutputFormatter` with `HumanFormatter` (tables/text) and `JsonFormatter` (JSON envelope with `ok`/`data`/`error`)
- `SessionBinder.cs` — static context providing session and formatter to command handlers

Command groups: `document`, `text`, `nav`, `analysis`, `annotation`, `bookmark`, `config`, `export`, `repl`. All commands support `--json` for machine-readable output. Page numbers are 1-based in CLI, converted to 0-based internally.

### RailReader.Agent (AI agent CLI)

- `RailReaderTools.cs` — `[Description]`-annotated tool methods wrapping `DocumentController`
- Configured via env vars: `OPENAI_API_KEY` (required), `RAILREADER_MODEL` (optional, default `gpt-4o`), `RAILREADER_BASE_URL` (optional, for OpenAI-compatible APIs)
- **Not included in binary releases** — build from source via `RailReader2-full.slnx` or direct project build

### Tests

21 xUnit tests in `tests/RailReader.Core.Tests/` — DocumentController, Camera, Annotations, AppConfig. `TestFixtures.cs` generates test PDFs via SkiaSharp.

## Key Concepts

### Rendering Pipeline

PDF → PDFium rasterises to `SKBitmap` at zoom-proportional DPI (150–600, capped at ~35 MP) → `SKImage` uploaded to GPU → drawn via `ICustomDrawOperation`/`ISkiaSharpApiLeaseFeature` with `SKCubicResampler.Mitchell`. Camera pan/zoom is compositor-level `MatrixTransform` on `CameraPanel` (no bitmap repaint). DPI upgrades async via `Task.Run`; `SKImage.FromBitmap()` must be called on UI thread. DPI tier rounding to 75 DPI steps with 1.5x hysteresis.

### Layout Analysis

Page bitmap → BGRA-to-RGB → 800x800 rescale → CHW float tensor → PP-DocLayoutV3 ONNX → `[N,7]` tensor `[classId, confidence, xmin, ymin, xmax, ymax, readingOrder]` → confidence filter (0.4) → NMS (IoU 0.5) → sort by reading order → line detection per block. Pixmap prep runs on thread pool; inference on dedicated `AnalysisWorker` thread. Results cached per-tab in `DocumentState.AnalysisCache`.

**Fallback (model unavailable)**: Without the ONNX model, layout falls back to simple horizontal strip detection. Rail mode still activates but with basic fixed-height blocks instead of detected regions. Download the model via `./scripts/download-model.sh` for full functionality.

### Rail Mode

Activates above `rail_zoom_threshold` when analysis is available. Locks to detected text blocks, advances line-by-line with cubic ease-out snap. Key mechanics: hold-to-scroll with quadratic speed ramping, soft asymptotic block edge clamping (`SoftEase`), `VerticalBias` for preserving vertical offset, pixel snapping for text shimmer reduction. Sub-features: auto-scroll (`P`), jump mode (`J`), line focus blur (`F`), line highlight tint, named bookmarks (`B`).

### Controller/ViewModel Pattern

`DocumentController` (Core) is the central facade. `MainWindowViewModel` (UI) is a thin wrapper handling only Avalonia concerns. `DocumentState` (Core) holds per-document state; `TabViewModel` (UI) is a thin `[ObservableProperty]` wrapper. `TickResult` from `Controller.Tick(dt)` drives granular UI invalidation. `IThreadMarshaller` abstracts UI thread posting.

### Annotations

Five tools (Highlight, Pen, Rectangle, TextNote, Eraser) via right-click radial menu with colour pickers. Select/move/resize in browse mode. Undo/redo stack. Stored internally in `ConfigDir/annotations/<hash>.json`, keyed by SHA256 hash of the PDF's full path. Legacy sidecar files (alongside the PDF) are loaded as a migration fallback but never written to. Export to PDF via `AnnotationExportService`. Named bookmarks also stored in the annotation file. `AnnotationService` handles all persistence; `AnnotationService.CleanOrphaned()` removes annotation files whose source PDFs no longer exist.

### Colour Effects

Four SkSL shaders compiled at startup via `SKRuntimeEffect.CreateColorFilter()`. Per-document: each `DocumentState` holds its own `ColourEffect`. Press `C` to cycle. Each effect has a matching `OverlayPalette` for rail overlay colours.

## Configuration

Config: `~/.config/railreader2/config.json` (Linux), `%APPDATA%\railreader2\config.json` (Windows), or `~/Library/Application Support/railreader2/config.json` (macOS). Auto-created with defaults. Editable live via Settings panel. See `Services/AppConfig.cs` for all fields and defaults.

Key fields: `rail_zoom_threshold`, `snap_duration_ms`, `scroll_speed_start/max`, `scroll_ramp_time`, `analysis_lookahead_pages`, `ui_font_scale`, `colour_effect/intensity`, `motion_blur/intensity`, `pixel_snapping`, `line_focus_blur/intensity`, `line_highlight_tint/opacity`, `auto_scroll_line_pause_ms/block_pause_ms`, `jump_percentage`, `dark_mode`, `navigable_classes[]`, `recent_files[]`.

`navigable_classes` controls which block types are navigable in rail mode. Line detection runs for all blocks regardless, so toggling classes doesn't require ONNX re-inference.

## Key Development Notes

- ONNX model outputs `[N, 7]` tensors with reading order as the 7th column (Global Pointer Mechanism)
- `PdfOutlineExtractor` uses direct PDFium P/Invoke (`FPDF_*`/`FPDFBookmark_*`) since PDFtoImage doesn't expose bookmarks
- `PdfiumResolver.Initialize()` must be called before any PDFium P/Invoke — registers a `DllImportResolver` that maps "pdfium" to the correct platform-specific native library (pdfium.dll / libpdfium.so / libpdfium.dylib). Called in all entry points (GUI, CLI, Agent)
- ONNX runtime pre-loading in `LayoutAnalyzer` static constructor handles Linux (.so) and macOS (.dylib); Windows uses OnnxRuntime's own resolver
- SkiaSharp 3.x explicitly overrides Avalonia 11's bundled 2.88 — required for `SKRuntimeEffect.CreateColorFilter()`
- TODO.md is a legacy Rust file — disregard it
- DISTRIBUTION.md documents the release process for all channels (GitHub, Microsoft Store)
- Without the ONNX model, layout falls back to simple horizontal strip detection
- `CleanupService.RunCleanup()` runs at startup and via Help menu (removes cache, temp, old logs)
- `SplashWindow` shows during startup; heavy init deferred via `Dispatcher.Post` at Background priority
- `window.Opened` can fire before `OnLoaded` wiring — guard against this in startup sequencing

## Thread Safety

- **UI thread**: All Avalonia UI, keyboard/mouse, viewport rendering, PDFium calls
- **Analysis Worker**: Single dedicated thread via `Channel<T>` for ONNX inference
- **Thread pool**: `RenderPagePixmap()` and DPI upgrades via `Task.Run()`
- **Critical**: Never call `PdfService` from background threads (PDFium crashes). Never modify `DocumentState` from the analysis worker — use `IThreadMarshaller` to post to UI thread.
- `AnalysisCache` is written via UI thread marshalling, read during animation frame polls — no locks needed.

## CI / Release Packaging

Releases triggered by pushing a `v*` tag (`.github/workflows/release.yml`).

- **Linux**: `appimagetool` (not `linuxdeploy` — avoids ELF dependency tracing issues with .NET self-contained). Model at `$APPDIR/models/`.
- **Windows (Inno Setup)**: `installer/railreader2.iss`. **Gotcha**: `.iss` paths are relative to the `.iss` file's directory, not CWD.
- **Windows (Microsoft Store)**: MSIX built by CI `build-msix` job (unsigned — Microsoft re-signs during Store review). Manifest at `msix/Package.appxmanifest`, visual assets in `msix/Assets/`. See `DISTRIBUTION.md` for the Store release workflow.
- **Model search order** (`FindModelPath()`): `AppContext.BaseDirectory/models/` → `$APPDIR/models/` → `LocalApplicationData/railreader2/models/` → `CWD/models/` → walk-up `../models/`

## Debugging

Press `Shift+D` for debug overlay (layout blocks, confidence, reading order, nav anchors). Rail mode activates at >3x zoom — if blocks aren't detected, run `./scripts/download-model.sh`. Animation frame dt is measured at the top of the callback and capped at 50ms.
