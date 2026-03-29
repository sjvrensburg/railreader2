# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**railreader2** is a desktop PDF viewer with AI-guided "rail reading" for high magnification viewing. Built in C# with .NET 10, Avalonia 11 (UI framework), PDFtoImage/PDFium (PDF rasterisation), SkiaSharp 3 (GPU rendering via Avalonia's Skia backend), and PP-DocLayoutV3 (ONNX layout detection).

## Build and Development Commands

**Prerequisites**: .NET 10 SDK (all projects target `net10.0`).

```bash
# Build app + CLI + tests (default solution)
dotnet build RailReader2.slnx

# Run the application (-- separates dotnet args from app args)
dotnet run -c Release --project src/RailReader2 -- <path-to-pdf>

# Run without arguments (shows welcome screen)
dotnet run -c Release --project src/RailReader2

# Run the CLI headless tool
dotnet run -c Release --project src/RailReader2.Cli -- render <pdf> --output-dir ./out
dotnet run -c Release --project src/RailReader2.Cli -- structure <pdf> --output out.json
dotnet run -c Release --project src/RailReader2.Cli -- annotations <pdf> --include-text --output ann.json

# Run tests (all)
dotnet test tests/RailReader.Core.Tests

# Run specific test class
dotnet test tests/RailReader.Core.Tests --filter "ClassName=RailReader.Core.Tests.CameraTests"

# Run specific test method
dotnet test tests/RailReader.Core.Tests --filter "FullyQualifiedName~TestMethodName"

# Publish self-contained release
dotnet publish src/RailReader2 -c Release -r linux-x64 --self-contained   # Linux
dotnet publish src/RailReader2 -c Release -r win-x64 --self-contained     # Windows

# Download ONNX model (required for AI layout)
./scripts/download-model.sh
```

**Always use `-c Release`** — debug builds are significantly slower.

## Architecture

```
RailReader2.slnx              # Default: app + core + renderer + CLI + tests
├── src/RailReader.Core/        # UI-free library: all business logic, models, services (zero rendering deps)
├── src/RailReader.Renderer.Skia/ # SkiaSharp rendering, PDFium PDF services (implements Core interfaces)
├── src/RailReader2/            # Thin Avalonia UI shell (references Core + Renderer.Skia)
├── src/RailReader2.Cli/        # Headless CLI tool (references Core + Renderer.Skia, zero Avalonia)
└── tests/RailReader.Core.Tests/# xUnit headless tests against Core
```

### RailReader.Core (UI-free library)

All business logic with zero Avalonia and zero SkiaSharp dependencies. Key files:

- `DocumentController.cs` — headless controller facade (orchestration, animation tick loop, viewport). Delegates zoom animation to `ZoomAnimationController.cs`, auto-scroll to `AutoScrollController.cs`, annotation interaction to `AnnotationInteractionHandler.cs`, search to `Services/SearchService.cs`
- `DocumentState.cs` — per-document state (PDF via `IPdfService`, camera, rail nav, analysis cache, annotations, bookmarks)
- `ZoomAnimationController.cs` — smooth zoom animation with easing, focus point preservation, rail position restoration
- `AutoScrollController.cs` — auto-scroll toggle/stop, jump mode exclusivity, speed management
- `Models/` — data models (Annotations, BookmarkEntry, Camera, LayoutBlock, RectF, ColorRGBA, etc.)
- `Services/AnalysisWorker.cs` — background ONNX inference thread (`Channel<T>` queue)
- `Services/RailNav.cs` — rail navigation state machine (snap, scroll, clamp, auto-scroll, jump mode)
- `Services/IPdfService.cs` — rendering-agnostic PDF service interfaces (`IPdfService`, `IRenderedPage`, `IPdfServiceFactory`)
- `Services/PdfTextService.cs` — text extraction with per-character bounding boxes (PDFium P/Invoke)
- `Services/AppConfig.cs` — config persistence (`~/.config/railreader2/config.json`)
- `Services/AnnotationService.cs` — JSON persistence for annotations and bookmarks, import/export with merge support
- `Services/AnnotationGeometry.cs` — pure-geometry annotation hit testing, bounds computation
- `AnnotationInteractionHandler.cs` — annotation tool input handling (drag, resize, text notes)
- `Services/SearchService.cs` — full-text search with regex/case sensitivity, result grouping by page
- `ILogger.cs` — logging abstraction (`ILogger`, `NullLogger`, `ConsoleLogger`)

### RailReader2 (Avalonia UI shell)

Thin wrapper delegating all logic to `DocumentController`/`DocumentState` in Core.

- `ViewModels/MainWindowViewModel.cs` — thin wrapper handling Avalonia-specific concerns (file dialogs, clipboard, invalidation)
- `ViewModels/TabViewModel.cs` — `[ObservableProperty]` wrapper for `DocumentState` binding
- `Views/MainWindow.axaml.cs` — keyboard shortcuts, camera transform, animation frame scheduling
- `Views/` — layers (PdfPageLayer, RailOverlayLayer, AnnotationLayer, SearchHighlightLayer), dialogs, panels
- `Controls/RadialMenu.cs` — Skia-rendered radial context menu with Font Awesome icons

**AXAML bindings**: `AvaloniaUseCompiledBindingsByDefault` is enabled — all bindings are compiled by default. Use `{x:Bind}`-style compiled bindings in AXAML files.

### RailReader.Renderer.Skia (SkiaSharp rendering)

Implements Core's `IPdfService`/`IPdfTextService` interfaces using PDFium + SkiaSharp. Also contains all Skia drawing code:

- `SkiaPdfService.cs` / `SkiaPdfTextService.cs` / `SkiaPdfServiceFactory.cs` — PDFium-backed PDF services
- `SkiaRenderedPage.cs` — `IRenderedPage` wrapping `SKBitmap`
- `AnnotationRenderer.cs` — Skia annotation drawing (highlight, freehand, text note, rectangle)
- `OverlayRenderer.cs` — rail overlay drawing (dim, block outline, line highlight)
- `ScreenshotCompositor.cs` — multi-layer composition to `SKBitmap`
- `ColourEffectShaders.cs` — SkSL shader compilation (HighContrast, HighVisibility, Amber, Invert)
- `AnnotationExportService.cs` — annotation export to PDF via `SKDocument`
- `SkiaConversions.cs` — `ColorRGBA`↔`SKColor`, `RectF`↔`SKRect` conversion helpers

### Tests

xUnit tests in `tests/RailReader.Core.Tests/` — DocumentController, Camera, Annotations (including merge), AppConfig, RailNav, SearchService, AnnotationGeometry, ZoomAnimation, AutoScroll. `TestFixtures.cs` generates test PDFs via SkiaSharp (test project references both Core and Renderer.Skia for this reason).

### RailReader2.Cli (Headless CLI)

Separate console binary (`RailReader2.Cli`) for automated extraction. Zero Avalonia deps — references Core + Renderer.Skia only. Three commands:

- `render <pdf>` — Render pages as PNG with optional colour effects (`--effect highcontrast|highvisibility|amber|invert`) and annotation overlay. Uses `IPdfService.RenderPage()` → `SkiaRenderedPage.Bitmap` → `ColourEffectShaders` + `AnnotationRenderer` directly (no `DocumentState`/`DocumentController`).
- `structure <pdf>` — Extract outline + ONNX layout blocks + per-block text as JSON. Uses `LayoutAnalyzer` directly (no `AnalysisWorker`), `IPdfTextService` for text extraction, `CharBox`↔`BBox` centre-point matching for block text.
- `annotations <pdf>` — Export annotations as JSON or annotated PDF. Rich mode (`--include-text` + `--include-blocks`): correlates annotations with layout blocks via `AnnotationGeometry.GetAnnotationBounds()` → `RectF`↔`BBox` overlap, extracts text under each annotation, finds nearest heading from outline + `paragraph_title`/`doc_title` blocks.

Shipped as additional artifacts on GitHub Releases (Linux + Windows). ONNX model NOT bundled — uses shared model cache from GUI install.

### Cross-Project Internals

Core uses `InternalsVisibleTo` to expose internals to `RailReader.Core.Tests`, `RailReader.Renderer.Skia`, `RailReader2`, and `RailReader2.Cli`. Renderer.Skia also exposes internals to `RailReader2` and `RailReader2.Cli`. This allows the thin UI shell and CLI to access internal types without making them public.

## Key Concepts

### Rendering Pipeline

PDF → PDFium rasterises to `SKBitmap` at zoom-proportional DPI (150–600, capped at ~35 MP) → `SKImage` uploaded to GPU → drawn via `ICustomDrawOperation`/`ISkiaSharpApiLeaseFeature` with `SKCubicResampler.Mitchell`. Camera pan/zoom is compositor-level `MatrixTransform` on `CameraPanel` (no bitmap repaint). DPI upgrades async via `Task.Run`; `SKImage.FromBitmap()` must be called on UI thread. DPI tier rounding to 75 DPI steps with 1.5x hysteresis.

### Layout Analysis

Page bitmap → BGRA-to-RGB → 800x800 rescale → CHW float tensor → PP-DocLayoutV3 ONNX → `[N,7]` tensor `[classId, confidence, xmin, ymin, xmax, ymax, readingOrder]` → confidence filter (0.4) → NMS (IoU 0.5) → sort by reading order → line detection per block. Pixmap prep runs on thread pool; inference on dedicated `AnalysisWorker` thread. Results cached per-tab in `DocumentState.AnalysisCache`.

**Fallback (model unavailable)**: Without the ONNX model, layout falls back to simple horizontal strip detection. Rail mode still activates but with basic fixed-height blocks instead of detected regions. Download the model via `./scripts/download-model.sh` for full functionality.

### Rail Mode

Activates above `rail_zoom_threshold` when analysis is available. Locks to detected text blocks, advances line-by-line with cubic ease-out snap. Key mechanics: hold-to-scroll with quadratic speed ramping, soft asymptotic block edge clamping (`SoftEase`), `VerticalBias` for preserving vertical offset, pixel snapping for text shimmer reduction. Sub-features: auto-scroll (`P`), jump mode (`J`), line focus blur (`F`), line highlight (`H`), named bookmarks (`B`). Free pan (Ctrl+drag) temporarily exits rail mode for unrestricted pan/zoom to inspect images or equations, snapping back on Ctrl release. Zooming in rail mode preserves horizontal scroll position and line screen position rather than snapping to line start.

### Controller/ViewModel Pattern

`DocumentController` (Core) is the central facade. `MainWindowViewModel` (UI) is a thin wrapper handling only Avalonia concerns. `DocumentState` (Core) holds per-document state; `TabViewModel` (UI) is a thin `[ObservableProperty]` wrapper. `TickResult` from `Controller.Tick(dt)` drives granular UI invalidation. `IThreadMarshaller` abstracts UI thread posting.

### Annotations

Five tools (Highlight, Pen, Rectangle, TextNote, Eraser) via right-click radial menu with colour pickers. Select/move/resize in browse mode. Undo/redo stack. Stored internally in `ConfigDir/annotations/<hash>.json`, keyed by SHA256 hash of the PDF's full path. Legacy sidecar files (alongside the PDF) are loaded as a migration fallback but never written to. Export to PDF via `AnnotationExportService`. Export/import as JSON for sharing between users — `AnnotationService.MergeInto()` appends imported annotations per page and deduplicates bookmarks. Named bookmarks also stored in the annotation file. `AnnotationService` handles all persistence; `AnnotationService.CleanOrphaned()` removes annotation files whose source PDFs no longer exist.

### Colour Effects

Four SkSL shaders compiled at startup via `SKRuntimeEffect.CreateColorFilter()`. Per-document: each `DocumentState` holds its own `ColourEffect`. Press `C` to cycle. Each effect has a matching `OverlayPalette` for rail overlay colours.

## Configuration

Config: `~/.config/railreader2/config.json` (Linux), `%APPDATA%\railreader2\config.json` (Windows), or `~/Library/Application Support/railreader2/config.json` (macOS). Auto-created with defaults. Editable live via Settings panel. See `Services/AppConfig.cs` for all fields and defaults.

Key fields: `rail_zoom_threshold`, `snap_duration_ms`, `scroll_speed_start/max`, `scroll_ramp_time`, `analysis_lookahead_pages`, `ui_font_scale`, `colour_effect/intensity`, `motion_blur/intensity`, `pixel_snapping`, `line_focus_blur/intensity`, `line_highlight_tint/opacity`, `auto_scroll_line_pause_ms/block_pause_ms`, `auto_scroll_trigger_enabled`, `auto_scroll_trigger_delay_ms`, `jump_percentage`, `dark_mode`, `navigable_classes[]`, `centering_classes[]`, `recent_files[]`.

`navigable_classes` controls which block types are navigable in rail mode. `centering_classes` controls which block types are horizontally centered when narrower than the viewport (excludes headings by default). Line detection runs for all blocks regardless, so toggling classes doesn't require ONNX re-inference.

## Key Development Notes

- ONNX model outputs `[N, 7]` tensors with reading order as the 7th column (Global Pointer Mechanism)
- `PdfOutlineExtractor` uses direct PDFium P/Invoke (`FPDF_*`/`FPDFBookmark_*`) since PDFtoImage doesn't expose bookmarks
- `PdfiumResolver.Initialize()` must be called before any PDFium P/Invoke — registers a `DllImportResolver` that maps "pdfium" to the correct platform-specific native library (pdfium.dll / libpdfium.so / libpdfium.dylib). Called in the GUI entry point
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
- **Windows (Microsoft Store)**: MSIX built by CI `build-msix` job (unsigned — Microsoft re-signs during Store review). Manifest at `msix/Package.appxmanifest`, visual assets in `msix/Assets/`. **Gotcha**: Store requires the version's 4th component (revision) to be `0` — CI forces this regardless of the git tag. See `DISTRIBUTION.md` for the Store release workflow.
- **Model search order** (`FindModelPath()`): `AppContext.BaseDirectory/models/` → `$APPDIR/models/` → `LocalApplicationData/railreader2/models/` → `CWD/models/` → walk-up `../models/`

## Debugging

Press `Shift+D` for debug overlay (layout blocks, confidence, reading order, nav anchors). Rail mode activates at >3x zoom — if blocks aren't detected, run `./scripts/download-model.sh`. Animation frame dt is measured at the top of the callback and capped at 50ms.
