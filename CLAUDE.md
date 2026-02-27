# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**railreader2** is a desktop PDF viewer with AI-guided "rail reading" for high magnification viewing. Built in C# with .NET 10, Avalonia 11 (UI framework), PDFtoImage/PDFium (PDF rasterisation), SkiaSharp 3 (GPU rendering via Avalonia's Skia backend), and PP-DocLayoutV3 (ONNX layout detection).

## Build and Development Commands

```bash
# Build app + tests (default solution, excludes AI agent)
dotnet build RailReader2.slnx

# Build everything including the AI agent CLI
dotnet build RailReader2-full.slnx

# Run the application (note: src/RailReader2 is the correct project path; -- separates dotnet args from app args)
dotnet run -c Release --project src/RailReader2 -- <path-to-pdf>

# Run without arguments (shows welcome screen)
dotnet run -c Release --project src/RailReader2

# Run tests
dotnet test tests/RailReader.Core.Tests

# Run the AI agent CLI (requires RailReader2-full.slnx or direct project build)
dotnet run --project src/RailReader.Agent -- "Open test.pdf and tell me how many pages it has"

# Publish self-contained release (Linux)
dotnet publish src/RailReader2 -c Release -r linux-x64 --self-contained

# Publish self-contained release (Windows)
dotnet publish src/RailReader2 -c Release -r win-x64 --self-contained

# Download ONNX model (required for AI layout)
./scripts/download-model.sh
```

## Architecture

The codebase is split into four projects across two solution files:

```
RailReader2.slnx              # Default: app + core + tests (no agent)
├── src/RailReader.Core/        # UI-free library: all business logic, models, services
├── src/RailReader2/            # Thin Avalonia UI shell (references Core)
└── tests/RailReader.Core.Tests/# xUnit headless tests against Core

RailReader2-full.slnx         # Full: includes experimental AI agent CLI
└── src/RailReader.Agent/       # AI agent CLI via Microsoft.Extensions.AI (references Core)
```

### RailReader.Core (UI-free library)

All business logic lives here with zero Avalonia dependencies. SkiaSharp is used for rendering (it's a graphics library, not a UI framework).

```
src/RailReader.Core/
├── RailReader.Core.csproj      # PDFtoImage, OnnxRuntime, SkiaSharp (no Avalonia)
├── IThreadMarshaller.cs        # Abstraction for UI thread posting
├── DocumentState.cs            # Per-document state (PDF, camera, rail nav, analysis cache)
├── DocumentController.cs       # Headless controller facade (all orchestration logic)
├── Models/                     # Data models (13 files: Annotations, Camera, LayoutBlock, etc.)
├── Services/                   # Business services (12 files + AnnotationRenderer)
│   ├── AnalysisWorker.cs       # Background thread for ONNX inference (Channel<T> queues)
│   ├── AnnotationExportService.cs # Export PDF with annotations rendered into pages
│   ├── AnnotationRenderer.cs   # Pure SkiaSharp annotation drawing and hit-testing
│   ├── AnnotationService.cs    # Annotation persistence (JSON sidecar files)
│   ├── AppConfig.cs            # Config persistence (~/.config/railreader2/config.json)
│   ├── PdfService.cs           # PDF access (page rendering, DPI scaling, page info)
│   ├── PdfTextService.cs       # Text extraction with per-character bounding boxes
│   ├── RailNav.cs              # Rail navigation state machine (snap, scroll, clamp)
│   └── ...                     # CleanupService, ColourEffectShaders, LayoutAnalyzer, etc.
└── Commands/
    └── Results.cs              # Typed result records for agent/headless consumption
```

### RailReader2 (Avalonia UI shell)

Thin wrapper that delegates all logic to `DocumentController` and `DocumentState` in Core.

```
src/RailReader2/
├── Program.cs                  # Avalonia entry point
├── App.axaml / App.axaml.cs    # App bootstrap (config load, cleanup, MainWindow creation)
├── AvaloniaThreadMarshaller.cs # IThreadMarshaller → Dispatcher.UIThread.Post()
├── ViewModels/
│   ├── MainWindowViewModel.cs  # Thin wrapper: delegates to DocumentController, handles UI-only concerns
│   └── TabViewModel.cs         # Thin wrapper: surfaces [ObservableProperty] for DocumentState binding
├── Controls/
│   └── RadialMenu.cs           # Radial context menu for annotation tools (Skia custom draw)
└── Views/                      # All Avalonia views (MainWindow, layers, dialogs, panels)
```

### RailReader.Agent (AI agent CLI)

```
src/RailReader.Agent/
├── RailReader.Agent.csproj     # Microsoft.Extensions.AI, references Core
├── Program.cs                  # CLI entry point with agent loop
└── RailReaderTools.cs          # [Description]-annotated tool methods wrapping DocumentController
```

### Tests

```
tests/RailReader.Core.Tests/
├── RailReader.Core.Tests.csproj
├── TestFixtures.cs             # Generates test PDFs via SkiaSharp
├── DocumentControllerTests.cs  # Open/close/navigate/fit tests
├── CameraTests.cs              # Zoom, pan, clamp, center tests
├── AnnotationTests.cs          # Add/remove/undo/redo/hit-test tests
└── AppConfigTests.cs           # Load/save round-trip tests
```

### Supporting files

```
installer/
├── railreader2.iss             # Inno Setup script for Windows installer
├── icon.ico                    # Generated from PNG at CI time (gitignored)
└── output/                     # ISCC output directory (gitignored)
models/
└── PP-DocLayoutV3.onnx         # Layout model (gitignored, ~50MB)
scripts/
└── download-model.sh           # Downloads ONNX model from HuggingFace
```

### Key Concepts

- **Rendering pipeline**: PDF bytes held in memory → PDFtoImage (PDFium) rasterises pages to `SKBitmap` at zoom-proportional DPI (150–600, capped to avoid ~35 MP bitmaps) → `SKImage` uploaded to GPU → drawn via Avalonia's `ICustomDrawOperation` / `ISkiaSharpApiLeaseFeature` → `SKCanvas` using `SKCubicResampler.Mitchell` for sharp text. Camera pan/zoom are compositor-level `MatrixTransform` on the `CameraPanel` (no bitmap repaint needed). DPI upgrades happen asynchronously via `Task.Run`; `SKImage.FromBitmap()` is called on the UI thread (inside the `Dispatcher.Post` callback) to avoid GPU texture upload on the wrong thread. Old bitmap/image are explicitly disposed after swap to release GPU memory promptly. DPI upgrade hysteresis is 1.5x to avoid frequent re-renders near threshold boundaries.
- **Layout analysis**: Page bitmap → BGRA-to-RGB → 800×800 bilinear rescale → CHW float tensor (pixels/255) → PP-DocLayoutV3 ONNX (`im_shape`, `image`, `scale_factor` inputs) → `[N,7]` detection tensor `[classId, confidence, xmin, ymin, xmax, ymax, readingOrder]` → confidence filter (0.4) → NMS (IoU 0.5) → sort by reading order → horizontal projection line detection per block. Input pixmap preparation (`RenderPagePixmap`) runs on a background thread via `Task.Run`; ONNX inference runs on a dedicated `Channel<T>`-based background worker thread (`AnalysisWorker`).
- **Rail mode**: Activates above configurable zoom threshold when analysis is available. Navigation locks to detected text blocks, advances line-by-line with cubic ease-out snap animations. Horizontal scrolling uses hold-to-scroll with quadratic speed ramping (integrated for frame-rate-independent displacement); `StartScroll()` seeds the hold timer with ~16ms of virtual elapsed time so the first animation frame produces visible displacement immediately. Block edge clamping uses a soft asymptotic ease (`SoftEase`: `over * k / (k + over)`, k=20px) instead of a hard stop to eliminate visual judder. Click-to-select jumps to any navigable block. Rail overlay (None palette) uses `DimExcludesBlock=true` with a yellow highlighter-style line tint for readable contrast. `VerticalBias` preserves user's panned vertical offset across line navigation (instead of always centering). Home/End keys snap to line start/end horizontally. Auto-scroll (`P` key) advances lines at a configurable interval.
- **Config**: `AppConfig` reads/writes `~/.config/railreader2/config.json` (Linux) or `%APPDATA%\railreader2\config.json` (Windows) via `System.Text.Json` with snake_case naming. Loaded at startup; editable live via Settings panel; changes auto-save. `UiFontScale` is applied by setting `Window.FontSize = 14.0 * scale` (inherited by all child controls); dialogs receive the current font size at creation time. `RecentFiles` (max 10) is a `List<RecentFileEntry>` tracking recently opened PDFs with per-document reading position (page, zoom, camera offset); updated on each `OpenDocument()` call, persisted in config JSON, and shown in File → Recent Files submenu. A custom `RecentFilesConverter` handles backward compatibility with the old plain-string format.
- **Resume reading position**: Each `RecentFileEntry` stores `FilePath`, `Page`, `Zoom`, `OffsetX`, `OffsetY`. Position is saved via `AppConfig.SaveReadingPosition()` when a tab is closed (`CloseTab`) and when the app exits (`window.Closing` → `SaveAllReadingPositions()`). On reopen, `OpenDocument()` checks `GetReadingPosition()` and restores the saved page, zoom, and camera offset instead of centering. If the same file is open in multiple tabs, the last-closed tab's position wins.
- **Fit Width**: `TabViewModel.FitWidth()` sets zoom so the page fills the viewport horizontally (top-aligned if taller than viewport). Accessible via View → Fit Width menu. Complements the existing `CenterPage()` (fit-page) which fits both dimensions.
- **Analysis caching**: Per-tab `Dictionary<int, PageAnalysis> AnalysisCache` avoids re-running ONNX inference on revisited pages. Lookahead analysis pre-processes upcoming pages when the worker is idle.
- **Minimap**: Draws from `TabViewModel.MinimapBitmap` — a small thumbnail (≤200×280 px) rendered once per page change via `PdfService.RenderThumbnail()`. This avoids downsampling the full 600 DPI bitmap (~35 MP) on every animation frame.
- **Headless controller**: `DocumentController` is the central facade in Core. It owns the document list, analysis worker, viewport size, search state, annotation tool state, and animation tick loop. The UI's `MainWindowViewModel` is a thin wrapper that delegates all logic to the controller and handles only Avalonia-specific concerns (file dialogs, animation frame scheduling, clipboard, invalidation callbacks). `DocumentState` (Core) holds per-document state; `TabViewModel` (UI) is a thin `[ObservableProperty]` wrapper for data binding.
- **IThreadMarshaller**: Abstracts UI thread posting. `AvaloniaThreadMarshaller` wraps `Dispatcher.UIThread.Post()`; `SynchronousThreadMarshaller` (in Core) executes inline for tests and agent CLI.
- **TickResult**: `DocumentController.Tick(dt)` returns a `TickResult` record struct indicating what changed (camera, page, overlay, search, annotations, still animating). The UI maps this to granular invalidations.
- **MVVM**: CommunityToolkit.Mvvm source generators (`[ObservableProperty]`, `[RelayCommand]`). Performance-sensitive paths (camera transforms, canvas invalidation) use direct method calls and `InvalidationCallbacks` for granular repaint targeting rather than pure data binding.
- **Colour effects**: Four SkSL shaders compiled at startup via `SKRuntimeEffect.CreateColorFilter()` — HighContrast (luminance inversion + S-curve), HighVisibility (yellow-on-black), Amber (warm tint), Invert. Applied via `canvas.SaveLayer(paint)` around page drawing. Each effect has a matching `OverlayPalette` for rail overlay colours.
- **Compositor camera**: `MainWindow.UpdateCameraTransform()` applies a `MatrixTransform` to `CameraPanel` for GPU-compositor-level pan/zoom — bitmap doesn't repaint on every frame, only on DPI changes. `GetWindowSize()` returns the actual viewport bounds (fed by `Viewport.SizeChanged`), not the full window `ClientSize`, so `CenterPage` positions correctly.
- **Motion blur**: Subtle directional blur during rail horizontal scroll (horizontal-only) and zoom (uniform). Uses a cubic speed curve (`speed^3 * maxSigma * intensity`) so blur stays barely perceptible at low/medium speeds. Configurable via `motion_blur` (toggle) and `motion_blur_intensity` (0.0–1.0) in config/Settings. `Camera.ZoomSpeed` tracks zoom velocity with exponential decay (~80ms half-life); `RailNav.ScrollSpeed` is the normalized instantaneous scroll speed. Applied via `SKImageFilter.CreateBlur` in `PdfPageLayer`. The blur filter and layer `SKPaint` are cached per render thread (`[ThreadStatic]`) and only recreated when sigma values change, avoiding per-frame allocation and GC pressure during animation.
- **Splash screen**: `SplashWindow` shows on startup while config loads and ONNX worker initializes. Heavy initialization (config, cleanup, ONNX) is deferred via `Dispatcher.UIThread.Post()` at `Background` priority with an extra yield, so the splash actually paints before blocking work begins (fixes splash not displaying on Linux/X11). Closed when the main window opens. Status bar shows "Analyzing..." while ONNX inference is in progress for the current page (`PendingRailSetup`).
- **Startup sequencing**: `window.Opened` can fire before `MainWindow.OnLoaded` finishes wiring `_invalidation`. `OnLoaded` guards against this by re-centering and forcing a camera update if a tab is already present. `PdfPageLayer.Render` uses tab page dimensions for the draw-op bounds (not `Bounds.Width/Height`) so the compositor does not cull the draw operation before the first layout pass completes.
- **Multi-tab**: `DocumentState` (Core) holds per-document state (PDF, camera, rail nav, analysis cache, outline, cached bitmap, minimap thumbnail). `DocumentController` (Core) owns the document list and shared resources (ONNX session, config, shaders). `TabViewModel` (UI) is a thin binding wrapper around `DocumentState`.
- **Search**: Full-text search across all pages with regex and case-sensitivity support. `PdfTextService` extracts text with per-character bounding boxes via PDFium P/Invoke. Matches are highlighted via `SearchHighlightLayer` with the active match emphasized. Search bar overlay (Ctrl+F) with match count and prev/next navigation.
- **Annotations**: Five annotation tools (Highlight, Pen, Rectangle, TextNote, Eraser) accessible via right-click radial menu. Highlight and Pen have colour pickers (outer ring of colour dots on the radial menu segment; `ColorOption` records with `SelectAction` callbacks). TextNotes render as collapsible popup notes (16px folded-corner icon; click to expand/collapse in browse mode; word-wrapped floating popup). Annotations can be selected, moved (drag in browse mode), and resized (freehand: 8-handle bounding box with proportional scaling) via `HandleBrowsePointerDown/Move/Up` in `DocumentController`. Selected annotations can be deleted with Delete/Backspace. All actions (add, move, resize, delete) support undo/redo via `MoveAnnotationAction`, `ResizeFreehandAction`, etc. Annotations are persisted as JSON sidecar files (`<pdf>.annotations.json`) via `AnnotationService`. Export to PDF via `AnnotationExportService` which renders annotations into page bitmaps.
- **Text selection**: Browse/Text Select/Copy toolbar (top-left overlay, `ToolBarView`) provides mode switching. Text select mode uses character-level hit testing from `PdfTextService` to build selection rectangles. Selected text is rendered via `AnnotationLayer` and copied to clipboard via Ctrl+C.
- **Radial menu**: `RadialMenu` is a Skia-rendered radial context menu with Font Awesome icons (embedded `fa-solid-900.ttf`). Shows annotation tools only. Centre button closes. Segments with `ColorOptions` (Highlight, Pen) show an outer ring of colour dots when tapped; tapping a dot selects the colour and activates the tool. `_expandedSegment` / `_hoveredColorIndex` track the outer ring state. Configured once in `SetupRadialMenu`.
- **Toolbar**: `ToolBarView` provides Browse (pan mode), Text Select, and Copy buttons as a floating overlay. Uses `avares://` font resource for Font Awesome icons. Copy button appears only when text is selected. Active mode is indicated by a blue toggle highlight.
- **Status bar annotation indicator**: When an annotation tool or text select is active, the status bar shows the tool name in amber with a clickable exit button, matching the Rail Mode display pattern.
- **Rail toolbar**: `RailToolBar` is a slim vertical toolbar docked inline to the right edge of the viewport (pushes content aside, not an overlay), visible only in rail mode. Contains vertical sliders for scroll speed and motion blur intensity. Values sync bidirectionally with `AppConfig` and persist across sessions. Labels inherit `Window.FontSize` for proper UI scaling. Keyboard shortcuts `[`/`]` adjust speed, `Shift+[`/`Shift+]` adjust blur.
- **Auto-scroll**: Toggleable via `P` key in rail mode. Continuously scrolls horizontally along the current line at the current speed setting, then advances to the next line when reaching the block's right edge. Holding `D`/`Right` during auto-scroll doubles the speed (boost). Disengages on opposing navigation (Up, Left), panning, or zooming. Status bar shows green "Auto-Scroll" indicator with pause button when active. Stops automatically when leaving rail mode.
- **Tab styling**: `TabBarView` uses programmatic styling with distinct active/inactive appearances — active tab has blue background with bold white text and a blue bottom indicator bar; inactive tabs have muted grey styling.
- **Fullscreen**: F11 toggles fullscreen mode — hides menu bar, tab bar, status bar, and window decorations (`SystemDecorations.None` + `WindowState.FullScreen`). Escape exits fullscreen. Also accessible via View → Fullscreen menu. `IsFullScreen` is an `[ObservableProperty]` on `MainWindowViewModel`; the `PropertyChanged` handler in `MainWindow.axaml.cs` toggles `WindowState` and `SystemDecorations`. Chrome elements bind `IsVisible="{Binding !IsFullScreen}"`.
- **Tab reorder**: Tabs can be rearranged by dragging. Implemented via tunnelling pointer events (`RoutingStrategies.Tunnel`) on `TabPanel` so press/move/release are intercepted before buttons consume them. A 5px drag threshold distinguishes clicks from drags. Visual feedback: dragged tab dims to 50% opacity, a 2px blue insertion indicator appears at the drop target. `MainWindowViewModel.MoveTab()` uses `ObservableCollection.Move()` and tracks the active tab by reference. Pointer capture is only released when an actual drag occurred, preserving normal button click behaviour.
- **Pixel snapping**: Reduces text shimmer at high zoom by quantizing camera positions to a pixel grid. Snap targets (`ComputeTargetCamera`) round Y to integer and X to 1/4 pixel. `ClampX` also quantizes its return value. When rail mode is stable (no animation), Y is snapped to integer in `OnAnimationFrame`. Controlled by `config.PixelSnapping` (default true).
- **DPI tier rounding**: `PdfService.CalculateRenderDpi` rounds to nearest 75 DPI step (150, 225, 300, 375, 450, 525, 600) instead of continuous `zoom*150`, keeping GPU upsampling ratios closer to simple fractions and reducing anti-aliasing shimmer.
- **Line focus blur**: When enabled, applies uniform Gaussian blur to the entire page except the active line in rail mode. Rendered in `PdfPageLayer` in two passes inside the colour effect layer: (1) blurred page with active line clipped out via `SKClipOperation.Difference`, (2) sharp active line on top. The blur spans the full page width (not just the active block). Sigma scales with `config.LineFocusBlurIntensity` (0.0–1.0, max sigma 4.0). Configured via `config.LineFocusBlur` and `config.LineFocusBlurIntensity`.
- **Auto-scroll pause**: Auto-scroll pauses briefly at the end of each line before advancing. Configurable via `config.AutoScrollLinePauseMs` (default 400ms) and `config.AutoScrollBlockPauseMs` (default 600ms for block/page transitions). Implemented in `RailNav.TickAutoScroll` with a `Stopwatch`-based pause timer. Set to 0 to disable.
- **Jump mode**: Toggled via `J` key. When active, D/Right and A/Left perform saccade-style jumps (percentage of visible width) instead of hold-to-scroll. Jump distance configurable via `config.JumpPercentage` (default 25%). Holding Shift with Right/Left performs a short jump at half the normal distance. Uses a fast 120ms snap animation. Status bar shows amber "Jump" indicator with exit button. `RailNav.Jump()` computes new camera position clamped to block bounds.
- **Tabbed settings**: `SettingsWindow` uses a `TabControl` with four tabs: Appearance (font scale, motion blur, colour effects), Rail Reading (navigation params, pixel snapping, line focus blur, jump distance), Auto-Scroll (pause durations), Advanced (navigable block types).

### Dependencies

**RailReader.Core** (no Avalonia):
- `PDFtoImage` 5.* — PDF rasterisation via PDFium
- `Microsoft.ML.OnnxRuntime` 1.* — ONNX Runtime for layout inference
- `SkiaSharp` 3.* — GPU-accelerated 2D drawing (overrides Avalonia's bundled 2.88 for `SKRuntimeEffect.CreateColorFilter`)

**RailReader2** (UI shell):
- `Avalonia` 11.* — cross-platform UI framework (Desktop, Fluent theme, Inter font)
- `CommunityToolkit.Mvvm` 8.* — MVVM source generators

**RailReader.Agent** (AI agent CLI):
- `Microsoft.Extensions.AI` 10.* — AI abstraction layer with function calling
- `Microsoft.Extensions.AI.OpenAI` 10.* — OpenAI-compatible chat client

## Configuration

Config file location: `~/.config/railreader2/config.json` (Linux) or `%APPDATA%\railreader2\config.json` (Windows). Auto-created on first run with defaults.

```json
{
  "rail_zoom_threshold": 3.0,
  "snap_duration_ms": 300.0,
  "scroll_speed_start": 10.0,
  "scroll_speed_max": 30.0,
  "scroll_ramp_time": 1.5,
  "analysis_lookahead_pages": 2,
  "ui_font_scale": 1.25,
  "colour_effect": "None",
  "colour_effect_intensity": 1.0,
  "motion_blur": true,
  "motion_blur_intensity": 0.33,
  "pixel_snapping": true,
  "line_focus_blur": false,
  "line_focus_blur_intensity": 0.5,
  "auto_scroll_line_pause_ms": 400.0,
  "auto_scroll_block_pause_ms": 600.0,
  "jump_percentage": 25.0,
  "navigable_classes": [
    "abstract", "algorithm", "display_formula",
    "footnote", "paragraph_title", "text"
  ],
  "recent_files": [
    {
      "file_path": "/path/to/document.pdf",
      "page": 4,
      "zoom": 3.2,
      "offset_x": -120.5,
      "offset_y": -45.0
    }
  ]
}
```

The `navigable_classes` array controls which PP-DocLayoutV3 block types are navigable in rail mode. Configurable live via Settings → Advanced. Line detection runs for all blocks regardless, so toggling classes doesn't require ONNX re-inference.

## Key Development Notes

- ONNX model outputs `[N, 7]` tensors: `[class_id, confidence, xmin, ymin, xmax, ymax, reading_order]`. The 7th column is the model's predicted reading order via its Global Pointer Mechanism.
- Layout analysis runs asynchronously via `AnalysisWorker` (background thread using `System.Threading.Channels`). Input pixmap preparation (`RenderPagePixmap`) now runs on a background thread via `Task.Run`; the worker submission is marshalled back to the UI thread for thread-safe access to `_inFlight`. Results are polled each animation frame.
- App can run without CLI arguments — shows welcome screen with "Open a PDF file (Ctrl+O)" prompt.
- **Run with `-c Release`** for production use — debug builds are significantly slower due to JIT and `dotnet run` always restoring/recompiling. Use `dotnet run -c Release --project src/RailReader2`.
- **Cleanup**: `CleanupService.RunCleanup()` runs on startup and on-demand via Help menu. Removes `cache/` contents, `.tmp` files, and `.log` files older than 7 days. Skips `config.json`, `.lock`, and `.onnx` files.
- **PdfOutlineExtractor** uses direct PDFium P/Invoke (`FPDF_*` / `FPDFBookmark_*`) since `PDFtoImage` doesn't expose the bookmark API.
- SkiaSharp 3.x is explicitly referenced to override Avalonia 11's bundled SkiaSharp 2.88 — required for `SKRuntimeEffect.CreateColorFilter()` used by colour effect shaders.
- **Unit tests**: 21 xUnit tests in `tests/RailReader.Core.Tests/` covering DocumentController, Camera, Annotations, and AppConfig. Run via `dotnet test tests/RailReader.Core.Tests`.
- **Outdated documentation**: TODO.md and DISTRIBUTION.md reference Rust implementation (cargo, rail.rs, config.rs). These are legacy files from a previous Rust version and should be disregarded; the current implementation is C#/.NET.

## Debugging & Development

### Debug Overlay

Press `Shift+D` to toggle the debug overlay, which visualizes:
- Detected layout blocks with bounding boxes and class labels
- Confidence scores for each detected region
- Reading order predictions (numbers within blocks)
- Navigation anchor points in rail mode

This is invaluable for understanding why rail navigation might skip blocks or for validating ONNX model output on specific PDFs.

### Testing Rail Mode

Rail mode activates at the configurable `rail_zoom_threshold` (default 3.0x). To test:
1. Open a PDF with a complex layout (multi-column, footnotes, etc.)
2. Zoom to >3x (use `+` key or mouse wheel)
3. Press `Shift+D` to see detected blocks
4. Use arrow keys or `W/A/S/D` to navigate

If blocks are not detected or reading order is incorrect, first check that the ONNX model was downloaded via `./scripts/download-model.sh`.

### Layout Analysis Fallback

If the ONNX model is missing or fails to load, layout analysis falls back to simple horizontal strip detection. This is still usable but provides no reading order or semantic block classification. Watch the startup logs for model loading status.

### Performance Profiling

- **Frame rate**: The animation loop targets the display refresh rate. Monitor CPU/GPU usage in system tools while panning/zooming.
- **ONNX inference time**: The `AnalysisWorker` logs inference latency. On typical modern hardware, expect ~50-200ms per page (480×800 input).
- **Memory usage**: PDFs with many pages can accumulate `SKImage` objects. The minimap thumbnail also consumes memory per-tab. Monitor via system tools or Task Manager.

## Common Development Tasks

### Rebuild and test with a specific PDF

```bash
# Release build is significantly faster than Debug
dotnet run -c Release --project src/RailReader2 -- /path/to/document.pdf
```

### Test rail mode parameters without recompiling

Edit `~/.config/railreader2/config.json` (Linux) or `%APPDATA%\railreader2\config.json` (Windows), then restart the app. The Settings panel also provides live editing without restart.

### Iterate on layout detection

1. Open a PDF that shows poor layout detection
2. Enable debug overlay (`Shift+D`)
3. If the model is missing, run `./scripts/download-model.sh` and restart
4. Adjust `navigable_classes` in Settings → Advanced to include/exclude block types
5. Note: changing which classes are navigable doesn't require ONNX re-inference; line detection runs for all blocks

### Profile DPI scaling behavior

The app automatically selects rasterization DPI based on zoom level (150–600 DPI). DPI upgrades use 1.5x hysteresis to avoid frequent re-renders near threshold boundaries. To observe DPI tier changes:
1. Open a PDF at 1x zoom (zoom = 1.0)
2. Enable debug overlay to watch the bitmap quality
3. Gradually zoom in; you'll see the bitmap upgrade to higher DPI as you cross thresholds
4. The 35 MP bitmap cap prevents excessive memory usage on very high zoom levels

### Test multi-tab state isolation

Each tab maintains independent camera position, analysis cache, and outline state. To verify:
1. Open two PDFs in separate tabs
2. Zoom and pan the first to a specific location
3. Switch to the second and zoom/pan differently
4. Switch back to the first; position should be preserved

### Examine ONNX model output

The `LayoutAnalyzer.InferenceAsync()` method in `Services/LayoutAnalyzer.cs` performs the ONNX inference. The raw `[N, 7]` output tensor is logged before NMS and reading order sorting. Enable application logging to trace model outputs on specific pages.

## Thread Safety & Concurrency

### Thread Model

- **UI thread**: Handles all Avalonia UI updates, keyboard/mouse input, and viewport rendering. Uses standard `InvalidationCallback` for fine-grained repaint targeting.
- **Analysis Worker thread**: `AnalysisWorker` runs a single dedicated background thread consuming a `Channel<PageAnalysisRequest>`. All ONNX inference happens here, unblocking the UI.
- **Task Pool threads**: `RenderPagePixmap()` (input pixmap preparation) and DPI upgrades run via `Task.Run()` on the .NET thread pool.

### Safe Cross-Thread Communication

- **Model → UI**: `AnalysisWorker` pushes completed `PageAnalysis` results to `DocumentState.AnalysisCache` (a `Dictionary`). This dictionary is accessed only from the UI thread during animation frame polls, avoiding lock contention.
- **UI → Model**: `DocumentController` queues analysis requests on the channel. The channel itself is thread-safe; the marshalling back to the UI thread for dictionary writes is handled inside the worker via `IThreadMarshaller`.
- **PDFium**: `PdfService` holds a single `PdfDocument` instance per tab. PDFium is not thread-safe for concurrent page access, so all rendering calls must be from the UI thread (they are).

### Avoid Common Pitfalls

- Do not call `PdfService` methods from background threads — PDFium will crash.
- Do not modify `DocumentState` properties from the analysis worker — use the `AnalysisCache` dictionary or property setters only from the UI thread (via `IThreadMarshaller`).
- `InvalidationCallback` delegates execute on the UI thread, so they are safe for property updates.

## Performance Tuning

### DPI Scaling & Bitmap Memory

Rasterization DPI is chosen as: `max(150, min(600, zoom * 150))`, capped to avoid bitmaps exceeding ~35 MP. For a standard A4 PDF (210×297 mm):
- At 1x zoom: ~150 DPI → ~2 MP
- At 3x zoom: ~450 DPI → ~18 MP
- At 5x zoom: 600 DPI (capped) → ~35 MP

If you need to adjust these constants, see `PdfService.RenderPageAsync()` in `Services/PdfService.cs`.

### ONNX Model Cache Efficiency

- **Per-tab analysis cache**: `DocumentState.AnalysisCache` is a `Dictionary<int, PageAnalysis>` that persists across navigation. Revisiting a previously analyzed page returns the cached result instantly (no re-inference).
- **Lookahead pre-analysis**: When the analysis worker is idle, it analyzes upcoming pages (controlled by `config.analysis_lookahead_pages`, default 2). Disable lookahead (set to 0) if you need to reduce CPU usage or VRAM pressure on slower machines.

### Minimap Rendering

The minimap draws from `DocumentState.MinimapBitmap`, a small thumbnail (≤200×280 px) rendered once per page change. This avoids downsampling the full 600 DPI bitmap every frame. If the minimap feels sluggish, the bottleneck is typically `PdfService.RenderThumbnail()` on a slow PDF or large file.

### GPU Colour Effect Shaders

The four colour effect shaders (HighContrast, HighVisibility, Amber, Invert) are compiled once at startup via `SKRuntimeEffect.CreateColorFilter()` and reused. Applying them is GPU-accelerated via `canvas.SaveLayer(paint)`. Performance impact is minimal; the bottleneck is always the PDF rasterization or ONNX inference, not the shader itself.

### Animation Frame Loop

`OnAnimationFrame` is driven by the compositor's vsync via `RequestAnimationFrame`. Frame delta (`dt`) is measured by restarting `_frameTimer` at the **top** of the callback (not the end), so dt captures true frame-to-frame interval including the frame's own work time. This prevents variable dt caused by excluding work time, which would cause uneven scroll displacement. `dt` is capped at 50ms to avoid large jumps after stalls.

### Per-Frame Allocation Reduction

`PdfPageLayer.PageDrawOperation` avoids per-frame heap allocation during animation:
- `SKImageFilter` for motion blur is cached per render thread (`[ThreadStatic]`) and only recreated when sigma values change beyond a 0.001 threshold
- `SKPaint` for the layer is reused across frames (ColorFilter/ImageFilter are cleared after each `canvas.Restore()`)
- `SKSamplingOptions` is a `static readonly` field (immutable struct, constructed once)

## Platform-Specific Gotchas

### Windows DPI Scaling

Windows high-DPI displays (125%, 150%, 200%) can interact poorly with PDFium if not handled correctly. The app uses `PdfService.SetupDpiAwareness()` to set process DPI awareness before PDFium initialization. If you see blurry or distorted PDF text on high-DPI monitors:
1. Check that `app.manifest` includes the DPI awareness declarations
2. Verify `SetupDpiAwareness()` is called before any PDFium rendering
3. Test with a simple PDF to isolate whether the issue is app-wide or PDF-specific

### Linux Font Discovery

Avalonia on Linux uses system fontconfig to locate fonts. If the Inter font (specified in the theme) is not installed:
1. Install via `sudo apt-get install fonts-inter` (Ubuntu/Debian) or equivalent
2. Alternatively, Avalonia will fall back to the system default serif font (usually acceptable)
3. In packaged AppImage releases, fontconfig configuration may need adjustment. The AppRun script may need to set `FONTCONFIG_PATH` to point to bundled fonts or system directories.

### macOS (Not Currently Supported)

The CI/Release workflow only builds for Linux (AppImage) and Windows (Inno Setup). macOS support would require:
- A native macOS runner in CI
- Testing Avalonia 11, PDFtoImage, and ONNX Runtime on macOS
- Bundling as a `.app` or `.dmg` via `notarization` if distributing via the App Store
- Potential code signing and gatekeeper issues

### Model Path Resolution Across Platforms

The model search paths in `FindModelPath()` are tried in order:
1. `AppContext.BaseDirectory/models/` — works for packaged installers (Windows + Linux AppImage)
2. `$APPDIR/models/` — Linux AppImage specific
3. `LocalApplicationData/railreader2/models/` — user data directory (last resort)
4. `CWD/models/` — development (dotnet run from repo root)
5. Walk-up (`../models/`, `../../models/`, etc.) — for nested build output directories

If the model is not found, the app logs a warning and falls back to horizontal-strip layout detection. Always run `./scripts/download-model.sh` before first-time use during development.

## Keyboard Shortcuts

| Key | Action |
|---|---|
| `Ctrl+O` | Open file |
| `Ctrl+W` | Close tab |
| `Ctrl+Q` | Quit |
| `Ctrl+Tab` | Next tab |
| `F1` | Shortcuts dialog |
| `PgDn` / `Space` | Next page (or next line in rail mode) |
| `PgUp` | Previous page |
| `Home` | First page |
| `End` | Last page |
| `Ctrl+Home` | First page |
| `Ctrl+End` | Last page |
| `+` / `-` | Zoom in / out |
| `0` | Reset zoom / fit page |
| `Ctrl+G` | Go to page |
| `Ctrl+Shift+O` | Toggle outline panel |
| `Ctrl+M` | Toggle minimap |
| `Ctrl+,` | Settings |
| `F11` | Toggle fullscreen |
| `Shift+D` | Toggle debug overlay |
| `↓` / `S` | Next line (rail) / pan down |
| `↑` / `W` | Previous line (rail) / pan up |
| `→` / `D` | Scroll right (rail) / pan right |
| `←` / `A` | Scroll left (rail) / pan left |
| `Home` | Line start (rail) / first page |
| `End` | Line end (rail) / last page |
| `P` | Toggle auto-scroll (rail) |
| `J` | Toggle jump mode (rail) |
| `F` | Toggle line focus blur (rail) |
| `Shift+Right` / `Shift+Left` | Short jump — half distance (jump mode) |
| `[` / `]` | Adjust scroll speed or jump distance (rail) |
| `Shift+[` / `Shift+]` | Adjust blur intensity (rail) |
| Click | Jump to block (rail mode) |
| `Ctrl+F` | Open search bar |
| `F3` / `Shift+F3` | Next / previous search match |
| `Escape` | Stop auto-scroll / close search / cancel annotation tool / exit fullscreen |
| `1` | Highlight tool |
| `2` | Pen tool |
| `3` | Rectangle tool |
| `4` | Text note tool |
| `5` | Eraser tool |
| Right-click | Open annotation radial menu (colour picker for Highlight/Pen) |
| `Ctrl+Z` | Undo annotation |
| `Ctrl+Y` / `Ctrl+Shift+Z` | Redo annotation |
| `Delete` / `Backspace` | Delete selected annotation (browse mode) |
| `Ctrl+C` | Copy selected text |

On-screen `◀` / `▶` buttons in the status bar provide mouse-accessible page navigation.

## CI / Release Packaging

Releases are triggered by pushing a `v*` tag. The workflow (`.github/workflows/release.yml`) builds for both platforms and creates a GitHub Release.

### Linux — AppImage

Uses `appimagetool` (not `linuxdeploy`) to package the self-contained .NET publish output. A custom `AppRun` script in the AppDir launches the .NET executable. The ONNX model is placed at `AppDir/models/` — found at runtime via the `$APPDIR` environment variable (`FindModelPath()` candidate #2 in `MainWindowViewModel`).

`linuxdeploy` was deliberately avoided because it traces ELF shared library dependencies, which fails for .NET self-contained apps (`libcoreclrtraceptprovider.so` links against `liblttng-ust.so.0` which doesn't exist on Ubuntu 24.04's ABI v1). Since `dotnet publish --self-contained` already bundles all required runtime libraries, `appimagetool` just packages the AppDir without dependency resolution.

### Windows — Inno Setup installer

`installer/railreader2.iss` defines the installer. CI installs Inno Setup via `choco`, converts `assets/railreader2.png` to `installer/icon.ico` with ImageMagick (`magick`), then compiles with `ISCC`. The version is extracted from the git tag and passed via `/DAPP_VERSION`.

The installer provides:
- Install to `Program Files\railreader2` (or per-user via `PrivilegesRequired=lowest`)
- Start Menu shortcuts (app + uninstaller)
- Optional Desktop shortcut (unchecked by default)
- Optional `.pdf` file association via `OpenWithProgids` registry (unchecked by default)
- Add/Remove Programs entry with icon and publisher info
- "Launch railreader2" checkbox on final page
- LZMA2 solid compression

**Inno Setup path resolution**: All paths in the `.iss` file are relative to the `.iss` file's own directory (`installer/`), not the working directory. Source files reference `"..\publish\*"` to reach the repo root's `publish/` directory. `OutputDir=output` produces `installer/output/`. This is a common gotcha — ISCC does not use the current working directory for relative paths.

### Model loading at runtime

`FindModelPath()` in `DocumentController` searches these locations in order:
1. `AppContext.BaseDirectory/models/` — works for Windows installer (model installed alongside exe) and bare `dotnet publish` output
2. `$APPDIR/models/` — works for Linux AppImage (model at AppImage root)
3. `LocalApplicationData/railreader2/models/` — user data directory
4. `CWD/models/` — works for `dotnet run` from repo root during development
5. `../models/`, `../../models/`, `../../../models/` — walk-up for nested `dotnet run`
