# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**railreader2** is a desktop PDF viewer with AI-guided "rail reading" for high magnification viewing. Built in C# with .NET 10, Avalonia 11 (UI framework), PDFtoImage/PDFium (PDF rasterisation), SkiaSharp 3 (GPU rendering via Avalonia's Skia backend), and PP-DocLayoutV3 (ONNX layout detection).

## Build and Development Commands

```bash
# Build the project
dotnet build src/RailReader2

# Run the application
dotnet run --project src/RailReader2 -- <path-to-pdf>

# Run without arguments (shows welcome screen)
dotnet run --project src/RailReader2

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

- **Rendering pipeline**: PDF bytes held in memory → PDFtoImage (PDFium) rasterises pages to `SKBitmap` at zoom-proportional DPI (150–600) → `SKImage` uploaded to GPU → drawn via Avalonia's `ICustomDrawOperation` / `ISkiaSharpApiLeaseFeature` → `SKCanvas`. Camera pan/zoom are compositor-level `MatrixTransform` on the `CameraPanel` (no bitmap repaint needed). DPI upgrades happen asynchronously via `Task.Run`.
- **Layout analysis**: Page bitmap → BGRA-to-RGB → 800×800 bilinear rescale → CHW float tensor (pixels/255) → PP-DocLayoutV3 ONNX (`im_shape`, `image`, `scale_factor` inputs) → `[N,7]` detection tensor `[classId, confidence, xmin, ymin, xmax, ymax, readingOrder]` → confidence filter (0.4) → NMS (IoU 0.5) → sort by reading order → horizontal projection line detection per block. Input preparation runs on the main thread; ONNX inference runs on a dedicated `Channel<T>`-based background worker thread (`AnalysisWorker`).
- **Rail mode**: Activates above configurable zoom threshold when analysis is available. Navigation locks to detected text blocks, advances line-by-line with cubic ease-out snap animations. Horizontal scrolling uses hold-to-scroll with quadratic speed ramping (integrated for frame-rate-independent displacement). Click-to-select jumps to any navigable block.
- **Config**: `AppConfig` reads/writes `~/.config/railreader2/config.json` (Linux) or `%APPDATA%\railreader2\config.json` (Windows) via `System.Text.Json` with snake_case naming. Loaded at startup; editable live via Settings panel; changes auto-save.
- **Analysis caching**: Per-tab `Dictionary<int, PageAnalysis> AnalysisCache` avoids re-running ONNX inference on revisited pages. Lookahead analysis pre-processes upcoming pages when the worker is idle.
- **MVVM**: CommunityToolkit.Mvvm source generators (`[ObservableProperty]`, `[RelayCommand]`). Performance-sensitive paths (camera transforms, canvas invalidation) use direct method calls and `InvalidationCallbacks` for granular repaint targeting rather than pure data binding.
- **Colour effects**: Four SkSL shaders compiled at startup via `SKRuntimeEffect.CreateColorFilter()` — HighContrast (luminance inversion + S-curve), HighVisibility (yellow-on-black), Amber (warm tint), Invert. Applied via `canvas.SaveLayer(paint)` around page drawing. Each effect has a matching `OverlayPalette` for rail overlay colours.
- **Compositor camera**: `MainWindow.UpdateCameraTransform()` applies a `MatrixTransform` to `CameraPanel` for GPU-compositor-level pan/zoom — bitmap doesn't repaint on every frame, only on DPI changes.
- **Multi-tab**: `TabViewModel` holds per-document state (PDF, camera, rail nav, analysis cache, outline, cached bitmap). `MainWindowViewModel` owns the tab collection and shared resources (ONNX session, config, shaders).

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
- Layout analysis runs asynchronously via `AnalysisWorker` (background thread using `System.Threading.Channels`). Input preparation (pixmap render + tensor build) happens on the main thread; ONNX inference runs on the worker. Results are polled each animation frame.
- App can run without CLI arguments — shows welcome screen with "Open a PDF file (Ctrl+O)" prompt.
- **Cleanup**: `CleanupService.RunCleanup()` runs on startup and on-demand via Help menu. Removes `cache/` contents, `.tmp` files, and `.log` files older than 7 days. Skips `config.json`, `.lock`, and `.onnx` files.
- **PdfOutlineExtractor** uses direct PDFium P/Invoke (`FPDF_*` / `FPDFBookmark_*`) since `PDFtoImage` doesn't expose the bookmark API.
- SkiaSharp 3.x is explicitly referenced to override Avalonia 11's bundled SkiaSharp 2.88 — required for `SKRuntimeEffect.CreateColorFilter()` used by colour effect shaders.
- No unit tests currently exist in the project.

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
