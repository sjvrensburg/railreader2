# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**railreader2** is a desktop PDF viewer with AI-guided "rail reading" for high magnification viewing. Built in C# with .NET 10, Avalonia 11 (UI framework), PDFtoImage/PDFium (PDF rasterisation), SkiaSharp 3 (GPU rendering via Avalonia's Skia backend), and PP-DocLayoutV3 (ONNX layout detection).

## Build and Development Commands

```bash
# Build the project
dotnet build src/RailReader2

# Run the application (note: src/RailReader2 is the correct project path; -- separates dotnet args from app args)
dotnet run -c Release --project src/RailReader2 -- <path-to-pdf>

# Run without arguments (shows welcome screen)
dotnet run -c Release --project src/RailReader2

# Publish self-contained release (Linux)
dotnet publish src/RailReader2 -c Release -r linux-x64 --self-contained

# Publish self-contained release (Windows)
dotnet publish src/RailReader2 -c Release -r win-x64 --self-contained

# Download ONNX model (required for AI layout)
./scripts/download-model.sh
```

## Architecture

```
src/RailReader2/
├── Program.cs                  # Avalonia entry point
├── App.axaml / App.axaml.cs    # App bootstrap (config load, cleanup, MainWindow creation)
├── RailReader2.csproj          # Project file (dependencies, target framework)
├── app.manifest                # Windows application manifest
├── Models/
│   ├── BBox.cs                 # Bounding box (X, Y, W, H) in page-point coordinates
│   ├── Camera.cs               # Viewport state (OffsetX, OffsetY, Zoom)
│   ├── ColourEffect.cs         # ColourEffect enum, OverlayPalette, display names
│   ├── LayoutBlock.cs          # Detected layout region (BBox, ClassId, Confidence, Order, Lines)
│   ├── LineInfo.cs             # Text line within a block (Y center, Height)
│   ├── NavResult.cs            # Navigation result enum (Ok, PageBoundaryNext/Prev)
│   ├── OutlineEntry.cs         # PDF bookmark tree node
│   └── PageAnalysis.cs         # Full analysis for one page (blocks, dimensions)
├── Services/
│   ├── AnalysisWorker.cs       # Background thread for ONNX inference (Channel<T> queues)
│   ├── AppConfig.cs            # Config persistence (~/.config/railreader2/config.json)
│   ├── CleanupService.cs       # Disk cleanup (cache, temp files, old logs)
│   ├── ColourEffectShaders.cs  # SkSL GPU shaders for accessibility colour effects
│   ├── LayoutAnalyzer.cs       # ONNX inference, NMS, reading order, line detection
│   ├── LayoutConstants.cs      # Class labels, input tensor size, thresholds
│   ├── PdfOutlineExtractor.cs  # PDFium P/Invoke for bookmark extraction
│   ├── PdfService.cs           # PDF access (page rendering, DPI scaling, page info)
│   └── RailNav.cs              # Rail navigation state machine (snap, scroll, clamp)
├── ViewModels/
│   ├── MainWindowViewModel.cs  # Central orchestrator (tabs, worker, animation loop, input)
│   └── TabViewModel.cs         # Per-document state (PDF, camera, rail nav, analysis cache)
└── Views/
    ├── MainWindow.axaml/.cs    # Window with compositor camera transforms, keyboard handling
    ├── ViewportPanel.cs        # Custom panel for zoom/pan/click input handling
    ├── PdfPageLayer.cs         # Custom draw: renders PDF bitmap via ICustomDrawOperation
    ├── RailOverlayLayer.cs     # Custom draw: rail mode dim/highlight/debug overlays
    ├── MinimapControl.axaml/.cs    # Interactive minimap overlay
    ├── OutlinePanel.axaml/.cs      # TOC/bookmark side panel
    ├── TabBarView.axaml/.cs        # Custom tab bar with close buttons
    ├── MenuBarView.axaml/.cs       # File/View/Navigation/Help menus
    ├── StatusBarView.axaml/.cs     # Page number, zoom level, rail mode status
    ├── SettingsWindow.axaml/.cs    # Live-editable settings panel
    ├── ShortcutsDialog.axaml/.cs   # Keyboard shortcuts help dialog (F1)
    ├── AboutDialog.axaml/.cs       # Version info and credits
    └── LoadingOverlay.axaml/.cs    # Loading spinner overlay
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

- **Rendering pipeline**: PDF bytes held in memory → PDFtoImage (PDFium) rasterises pages to `SKBitmap` at zoom-proportional DPI (150–600, capped to avoid ~35 MP bitmaps) → `SKImage` uploaded to GPU → drawn via Avalonia's `ICustomDrawOperation` / `ISkiaSharpApiLeaseFeature` → `SKCanvas` using `SKCubicResampler.Mitchell` for sharp text. Camera pan/zoom are compositor-level `MatrixTransform` on the `CameraPanel` (no bitmap repaint needed). DPI upgrades happen asynchronously via `Task.Run`; the swap is atomic (new `SKImage` is built before replacing the old one) to avoid blank frames.
- **Layout analysis**: Page bitmap → BGRA-to-RGB → 800×800 bilinear rescale → CHW float tensor (pixels/255) → PP-DocLayoutV3 ONNX (`im_shape`, `image`, `scale_factor` inputs) → `[N,7]` detection tensor `[classId, confidence, xmin, ymin, xmax, ymax, readingOrder]` → confidence filter (0.4) → NMS (IoU 0.5) → sort by reading order → horizontal projection line detection per block. Input pixmap preparation (`RenderPagePixmap`) runs on a background thread via `Task.Run`; ONNX inference runs on a dedicated `Channel<T>`-based background worker thread (`AnalysisWorker`).
- **Rail mode**: Activates above configurable zoom threshold when analysis is available. Navigation locks to detected text blocks, advances line-by-line with cubic ease-out snap animations. Horizontal scrolling uses hold-to-scroll with quadratic speed ramping (integrated for frame-rate-independent displacement). Click-to-select jumps to any navigable block. Rail overlay (None palette) uses `DimExcludesBlock=true` with a yellow highlighter-style line tint for readable contrast.
- **Config**: `AppConfig` reads/writes `~/.config/railreader2/config.json` (Linux) or `%APPDATA%\railreader2\config.json` (Windows) via `System.Text.Json` with snake_case naming. Loaded at startup; editable live via Settings panel; changes auto-save. `UiFontScale` is applied by setting `Window.FontSize = 14.0 * scale` (inherited by all child controls); dialogs receive the current font size at creation time.
- **Analysis caching**: Per-tab `Dictionary<int, PageAnalysis> AnalysisCache` avoids re-running ONNX inference on revisited pages. Lookahead analysis pre-processes upcoming pages when the worker is idle.
- **Minimap**: Draws from `TabViewModel.MinimapBitmap` — a small thumbnail (≤200×280 px) rendered once per page change via `PdfService.RenderThumbnail()`. This avoids downsampling the full 600 DPI bitmap (~35 MP) on every animation frame.
- **MVVM**: CommunityToolkit.Mvvm source generators (`[ObservableProperty]`, `[RelayCommand]`). Performance-sensitive paths (camera transforms, canvas invalidation) use direct method calls and `InvalidationCallbacks` for granular repaint targeting rather than pure data binding.
- **Colour effects**: Four SkSL shaders compiled at startup via `SKRuntimeEffect.CreateColorFilter()` — HighContrast (luminance inversion + S-curve), HighVisibility (yellow-on-black), Amber (warm tint), Invert. Applied via `canvas.SaveLayer(paint)` around page drawing. Each effect has a matching `OverlayPalette` for rail overlay colours.
- **Compositor camera**: `MainWindow.UpdateCameraTransform()` applies a `MatrixTransform` to `CameraPanel` for GPU-compositor-level pan/zoom — bitmap doesn't repaint on every frame, only on DPI changes. `GetWindowSize()` returns the actual viewport bounds (fed by `Viewport.SizeChanged`), not the full window `ClientSize`, so `CenterPage` positions correctly.
- **Startup sequencing**: `window.Opened` can fire before `MainWindow.OnLoaded` finishes wiring `_invalidation`. `OnLoaded` guards against this by re-centering and forcing a camera update if a tab is already present. `PdfPageLayer.Render` uses tab page dimensions for the draw-op bounds (not `Bounds.Width/Height`) so the compositor does not cull the draw operation before the first layout pass completes.
- **Multi-tab**: `TabViewModel` holds per-document state (PDF, camera, rail nav, analysis cache, outline, cached bitmap, minimap thumbnail). `MainWindowViewModel` owns the tab collection and shared resources (ONNX session, config, shaders).

### Dependencies

- `Avalonia` 11.* — cross-platform UI framework (Desktop, Fluent theme, Inter font)
- `CommunityToolkit.Mvvm` 8.* — MVVM source generators
- `PDFtoImage` 5.* — PDF rasterisation via PDFium
- `Microsoft.ML.OnnxRuntime` 1.* — ONNX Runtime for layout inference
- `SkiaSharp` 3.* — GPU-accelerated 2D drawing (overrides Avalonia's bundled 2.88 for `SKRuntimeEffect.CreateColorFilter`)

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

The `navigable_classes` array controls which PP-DocLayoutV3 block types are navigable in rail mode. Configurable live via Settings → Advanced. Line detection runs for all blocks regardless, so toggling classes doesn't require ONNX re-inference.

## Key Development Notes

- ONNX model outputs `[N, 7]` tensors: `[class_id, confidence, xmin, ymin, xmax, ymax, reading_order]`. The 7th column is the model's predicted reading order via its Global Pointer Mechanism.
- Layout analysis runs asynchronously via `AnalysisWorker` (background thread using `System.Threading.Channels`). Input pixmap preparation (`RenderPagePixmap`) now runs on a background thread via `Task.Run`; the worker submission is marshalled back to the UI thread for thread-safe access to `_inFlight`. Results are polled each animation frame.
- App can run without CLI arguments — shows welcome screen with "Open a PDF file (Ctrl+O)" prompt.
- **Run with `-c Release`** for production use — debug builds are significantly slower due to JIT and `dotnet run` always restoring/recompiling. Use `dotnet run -c Release --project src/RailReader2`.
- **Cleanup**: `CleanupService.RunCleanup()` runs on startup and on-demand via Help menu. Removes `cache/` contents, `.tmp` files, and `.log` files older than 7 days. Skips `config.json`, `.lock`, and `.onnx` files.
- **PdfOutlineExtractor** uses direct PDFium P/Invoke (`FPDF_*` / `FPDFBookmark_*`) since `PDFtoImage` doesn't expose the bookmark API.
- SkiaSharp 3.x is explicitly referenced to override Avalonia 11's bundled SkiaSharp 2.88 — required for `SKRuntimeEffect.CreateColorFilter()` used by colour effect shaders.
- No unit tests currently exist in the project.
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

The app automatically selects rasterization DPI based on zoom level (150–600 DPI). To observe DPI tier changes:
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

- **Model → UI**: `AnalysisWorker` pushes completed `PageAnalysis` results to `TabViewModel._analysisCache` (a `Dictionary`). This dictionary is accessed only from the UI thread during animation frame polls, avoiding lock contention.
- **UI → Model**: `MainWindowViewModel.SubmitAnalysisTask()` queues analysis requests on the channel. The channel itself is thread-safe; the marshalling back to the UI thread for dictionary writes is handled inside the worker.
- **PDFium**: `PdfService` holds a single `PdfDocument` instance per tab. PDFium is not thread-safe for concurrent page access, so all rendering calls must be from the UI thread (they are).

### Avoid Common Pitfalls

- Do not call `PdfService` methods from background threads — PDFium will crash.
- Do not modify `TabViewModel` properties from the analysis worker — use the `_analysisCache` dictionary or `ObservableProperty` setters only from the UI thread.
- `InvalidationCallback` delegates execute on the UI thread, so they are safe for property updates.

## Performance Tuning

### DPI Scaling & Bitmap Memory

Rasterization DPI is chosen as: `max(150, min(600, zoom * 150))`, capped to avoid bitmaps exceeding ~35 MP. For a standard A4 PDF (210×297 mm):
- At 1x zoom: ~150 DPI → ~2 MP
- At 3x zoom: ~450 DPI → ~18 MP
- At 5x zoom: 600 DPI (capped) → ~35 MP

If you need to adjust these constants, see `PdfService.RenderPageAsync()` in `Services/PdfService.cs`.

### ONNX Model Cache Efficiency

- **Per-tab analysis cache**: `TabViewModel.AnalysisCache` is a `Dictionary<int, PageAnalysis>` that persists across navigation. Revisiting a previously analyzed page returns the cached result instantly (no re-inference).
- **Lookahead pre-analysis**: When the analysis worker is idle, it analyzes upcoming pages (controlled by `config.analysis_lookahead_pages`, default 2). Disable lookahead (set to 0) if you need to reduce CPU usage or VRAM pressure on slower machines.

### Minimap Rendering

The minimap draws from `TabViewModel.MinimapBitmap`, a small thumbnail (≤200×280 px) rendered once per page change. This avoids downsampling the full 600 DPI bitmap every frame. If the minimap feels sluggish, the bottleneck is typically `PdfService.RenderThumbnail()` on a slow PDF or large file.

### GPU Colour Effect Shaders

The four colour effect shaders (HighContrast, HighVisibility, Amber, Invert) are compiled once at startup via `SKRuntimeEffect.CreateColorFilter()` and reused. Applying them is GPU-accelerated via `canvas.SaveLayer(paint)`. Performance impact is minimal; the bottleneck is always the PDF rasterization or ONNX inference, not the shader itself.

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
| `Ctrl+Shift+O` | Toggle outline panel |
| `Ctrl+M` | Toggle minimap |
| `Ctrl+,` | Settings |
| `Shift+D` | Toggle debug overlay |
| `↓` / `S` | Next line (rail) / pan down |
| `↑` / `W` | Previous line (rail) / pan up |
| `→` / `D` | Scroll right (rail) / pan right |
| `←` / `A` | Scroll left (rail) / pan left |
| Click | Jump to block (rail mode) |

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

`FindModelPath()` in `MainWindowViewModel` searches these locations in order:
1. `AppContext.BaseDirectory/models/` — works for Windows installer (model installed alongside exe) and bare `dotnet publish` output
2. `$APPDIR/models/` — works for Linux AppImage (model at AppImage root)
3. `LocalApplicationData/railreader2/models/` — user data directory
4. `CWD/models/` — works for `dotnet run` from repo root during development
5. `../models/`, `../../models/`, `../../../models/` — walk-up for nested `dotnet run`
