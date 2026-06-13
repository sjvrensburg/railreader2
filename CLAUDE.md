# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**railreader2** is a desktop PDF viewer with AI-guided "rail reading" for high magnification viewing. Built in C# with .NET 10, Avalonia 12 (UI framework), PDFtoImage/PDFium (PDF rasterisation), SkiaSharp 3 (GPU rendering via Avalonia's Skia backend), and ONNX layout detection (Docling Heron INT8 bundled by default, PP-DocLayoutV3 or PP-S available as alternatives).

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
dotnet run -c Release --project src/RailReader2.Cli -- vlm <pdf> --output vlm.json
dotnet run -c Release --project src/RailReader2.Cli -- export <pdf> --no-vlm --output doc.md

# Run tests (Export is the test project in this repo; Core tests live upstream in RailReaderCore)
dotnet test tests/RailReader.Export.Tests

# Run specific test class
dotnet test tests/RailReader.Export.Tests --filter "ClassName=RailReader.Export.Tests.HeadingLevelResolverTests"

# Run specific test method
dotnet test tests/RailReader.Export.Tests --filter "FullyQualifiedName~TestMethodName"

# Regenerate README/website screenshots from the real UI (headless, writes into docs/img/)
dotnet run --project src/Tools/RenderHarness.Headless -c Release            # all shots
dotnet run --project src/Tools/RenderHarness.Headless -c Release --only rail_mode

# Publish self-contained release
dotnet publish src/RailReader2 -c Release -r linux-x64 --self-contained   # Linux
dotnet publish src/RailReader2 -c Release -r win-x64 --self-contained     # Windows

# Download ONNX model (required for AI layout)
./scripts/download-model.sh
```

**Always use `-c Release`** — debug builds are significantly slower.

## Architecture

```
RailReader2.slnx                  # Default: app + CLI + screenshot tool + tests
├── src/RailReader2/              # Thin Avalonia UI shell
├── src/RailReader2.Cli/          # Headless CLI tool (zero Avalonia)
├── src/Tools/RenderHarness.Headless/ # Headless doc-screenshot generator (references the GUI project)
└── tests/RailReader.Export.Tests/ # xUnit tests for the upstream Export package (Core tests live upstream)
```

The portable core — `RailReader.Core`, `RailReader.Core.Pdfium`, `RailReader.Core.Analysis`, `RailReader.Renderer.Skia`, `RailReader.Core.Vlm.OpenAI`, `RailReader.Export` — lives in the separate [RailReaderCore](https://github.com/sjvrensburg/RailReaderCore) repository and is consumed here as NuGet packages. All references in this document to types like `DocumentController`, `DocumentState`, `AppConfig`, `AnnotationService`, `LayoutAnalyzer`, `SkiaPdfService`, `OverlayRenderer`, `RailReaderLogging`, etc. resolve through those packages. Logger bootstrap goes via `RailReaderLogging.Logger = new ConsoleLogger();` once at startup; the per-service Logger setters that previously existed are gone.

### RailReader2 (Avalonia UI shell)

Thin wrapper delegating all logic to `DocumentController`/`DocumentState` in Core.

- `ViewModels/MainWindowViewModel.cs` (+ `.Annotations.cs` / `.Documents.cs` / `.Navigation.cs` / `.Search.cs` / `.Vlm.cs` partials) — thin wrapper handling Avalonia-specific concerns (file dialogs, clipboard, invalidation).
- `ViewModels/TabViewModel.cs` — `[ObservableProperty]` wrapper for `DocumentState` binding
- `Views/MainWindow.axaml.cs` — window chrome + keyboard shortcuts; wires `InvalidationCallbacks` to `DocumentView`, which owns the composition layers and builds their per-tab state
- `Views/DocumentView.axaml(.cs)` — the layered viewport (extracted from MainWindow): the four composition layers + minimap + `ToolBarView`, per-tab state-building, and layer invalidation
- `Views/CompositionLayerControl.cs` — generic base class for `CompositionCustomVisual`-backed layers (manages visual lifecycle, state/message dispatch)
- `Views/` — composition layers (PdfPageLayer, RailOverlayLayer, AnnotationLayer, SearchHighlightLayer, `PortalMarkerLayer`), `ToolBarView` (annotation toolbar), `StatusBarView`/`MenuBarView`/`TabBarView`, dialogs (ConfirmUrlDialog, BookmarkNameDialog, …), the detachable `PortalWindow` (+ `Controls/ZoomPanImage.cs`), and `OutlinePanel` — a single-open accordion with six self-contained sub-views: `OutlineView`/`BookmarksView`/`IndexView` (figures, tables, equations)/`SearchView`/`CommentsView`/`PortalsView`
- `Controls/Icon.cs` + `Assets/Icons.axaml` — Lucide icons as native Avalonia vector geometry (decorative, theme-aware, scales with the UI font-scale); no icon font — add one by converting a Lucide SVG to a `StreamGeometry`
- `Views/DocumentViewportAutomationPeer.cs` — publishes the GPU viewport's live state (page/zoom/rail mode/current-line text/page outline) to the platform accessibility/automation tree (AT-SPI on Linux, UIA on Windows). Rail role/line text comes from Core's `DocumentController.GetReadingPosition()` and the on-demand page outline from `GetPageDescription()` (no hand-rolled text extraction); announcements are push-driven by Core's `PageChanged`/`ReadingPositionChanged` callbacks (via `InvalidationCallbacks.AnnounceAccessibility`) plus the render path as a backstop

**AXAML bindings**: `AvaloniaUseCompiledBindingsByDefault` is enabled — all bindings are compiled by default. Use `{x:Bind}`-style compiled bindings in AXAML files.

### Tests

One test project in this repo:
- `tests/RailReader.Export.Tests/` — HeadingLevelResolver (outline matching, depth clamping, Levenshtein), PageMarkdownBuilder (all block types, annotations, plain-text fallback), MarkdownExportService (end-to-end with real PDFs: plain-text fallback, page range, progress reporting, cancellation, page break options). References both Core and Renderer.Skia (the latter for SkiaSharp-generated test PDFs).

Tests for the portable core (DocumentController, Camera, Annotations, AppConfig, RailNav, SearchService, etc.) live in the upstream [RailReaderCore](https://github.com/sjvrensburg/RailReaderCore) repo — the mirrored `RailReader.Core.Tests` project was dropped from this repo once Core moved to NuGet.

### RailReader2.Cli (Headless CLI)

Separate console binary (`RailReader2.Cli`) for automated extraction. Zero Avalonia deps — references Core + Renderer.Skia + Export. Five commands:

- `render <pdf>` — Render pages as PNG with optional colour effects (`--effect highcontrast|highvisibility|amber|invert`) and annotation overlay. Uses `IPdfService.RenderPage()` → `SkiaRenderedPage.Bitmap` → `ColourEffectShaders` + `AnnotationRenderer` directly (no `DocumentState`/`DocumentController`).
- `structure <pdf>` — Extract outline + ONNX layout blocks + per-block text as JSON. Uses `LayoutAnalyzer` directly (no `AnalysisWorker`), `IPdfTextService` for text extraction, `CharBox`↔`BBox` centre-point matching for block text.
- `annotations <pdf>` — Export annotations as JSON or annotated PDF. Supports `--pages <range>` to filter by page. Rich mode (`--include-text` + `--include-blocks`): correlates annotations with layout blocks via `AnnotationGeometry.GetAnnotationBounds()` → `RectF`↔`BBox` overlap, extracts text under each annotation, finds nearest heading from outline + `paragraph_title`/`doc_title` blocks.
- `vlm <pdf>` — Transcribe detected equations/tables/figures via an OpenAI-compatible vision API. Outputs LaTeX/Markdown/descriptions as JSON.
- `export <pdf>` — Export PDF to structured Markdown. Uses `MarkdownExportService` from the Export library. Per-page pipeline: layout analysis → text extraction → heading resolution (outline fuzzy-match) → VLM transcription (equations → LaTeX, tables → pipe tables, figures → descriptions/images) → annotation blockquotes. Graceful degradation: ONNX+VLM → ONNX-only (`[equation]`/`[figure]`/code-block tables) → plain text with outline headings.

Shipped as additional artifacts on GitHub Releases (Linux + Windows). ONNX model bundled in `models/` subdirectory within the archive.

### RailReader.Export (Markdown export pipeline — upstream package)

Structured PDF-to-Markdown export library. Lives in the RailReaderCore repo and is consumed here as the `RailReader.Export` NuGet package (since 0.28.0); the in-repo copy was removed. Zero Avalonia deps — references Core + Renderer.Skia.

- `MarkdownExportService.cs` — `IMarkdownExportService` implementation. Orchestrates per-page pipeline: layout analysis → text extraction → heading level resolution → VLM dispatch → annotation extraction → Markdown assembly. Writes to `TextWriter` for flexible output (file, stdout, StringWriter).
- `HeadingLevelResolver.cs` — Maps `doc_title`/`paragraph_title` blocks to Markdown heading levels (H1–H6) by fuzzy-matching extracted text against the flattened PDF outline tree (case-insensitive containment, then Levenshtein similarity > 80%). Falls back to doc_title → H1, paragraph_title → H2.
- `PageMarkdownBuilder.cs` — Walks blocks in reading order, renders each to Markdown by class (headings, paragraphs, `$$latex$$`, pipe tables, `![desc](path)`, `*captions*`). Skips page furniture (header/footer/number/seal). Appends highlight blockquotes and text notes from annotations.

### RenderHarness.Headless (documentation screenshots)

`src/Tools/RenderHarness.Headless/` boots the real `App`/`MainWindow`/`MainWindowViewModel` under `Avalonia.Headless` with **real Skia drawing** (`UseHeadlessDrawing = false`) — no X11/Wayland needed — to capture the README/website screenshots faithfully (real menu bar, accordion, composition layers, detected blocks, rail line). It references the GUI project but builds as a separate tool that doesn't ship with the GUI package. Shots are declared in `screenshots.json` and written into `docs/img/`; source PDFs come from `experiments/PDFs/`. PDFium + the ONNX model must be resolvable (run `./scripts/download-model.sh` if rail mode / debug overlay come up empty). See its own README for adding a shot.

### Cross-Project Internals

The upstream packages declare `InternalsVisibleTo` for `RailReader2`, `RailReader2.Cli`, `RailReader.Export`, and `RailReader.Export.Tests` (plus their own dev siblings). This lets the consumers in this repo access internal types without making them part of the upstream public surface. Export exposes internals to `RailReader.Export.Tests`. If a referenced upstream symbol stops resolving after a package bump, check whether it was made non-internal or moved — the friend-assembly grant only covers `internal`, not `private`.

## Key Concepts

### Rendering Pipeline

PDF → PDFium rasterises to `SKBitmap` at zoom-proportional DPI (150 DPI floor; the max-DPI cap and tier step come from the configurable **render-quality preset** — default `High` caps at 525 DPI / 85-DPI tiers, presets span 350→800 DPI, `Custom` up to 1200, all guarded by an in-Core ~64 MP area ceiling) → `SKImage` uploaded as mipmapped GPU texture via `SKImage.ToTextureImage(grContext, mipmapped: true)` → drawn on Avalonia's composition thread via `CompositionCustomVisual`/`CompositionCustomVisualHandler` with trilinear sampling (`SKFilterMode.Linear` + `SKMipmapMode.Linear`). Camera transform is applied atomically inside Skia draw calls (not via Avalonia `MatrixTransform`) — this eliminates Windows jitter caused by stale-draw/new-transform frame mismatches. Four rendering layers (`PdfPageLayer`, `SearchHighlightLayer`, `AnnotationLayer`, `RailOverlayLayer`) each inherit from `CompositionLayerControl<THandler>`, a generic base class that manages `CompositionCustomVisual` lifecycle. State is passed to handlers via `SendHandlerMessage()`. Retired `SKImage` instances are disposed on the composition thread via `RetireImage` messages to avoid cross-thread access violations. DPI upgrades async via `Task.Run`; `SKImage.FromBitmap()` must be called on UI thread. DPI tier rounding uses the preset's tier step (default 85 DPI) with 1.5x hysteresis. The preset→DPI math lives entirely in Core (RailReaderCore 0.24.0, `CalculateRenderDpi`); the desktop only persists the chosen preset and re-applies it via `AppConfig.ToCoreSettings()` → `DocumentController.OnConfigChanged()`, which invalidates the page cache so the open page re-rasterises live with no restart.

### Layout Analysis

Page bitmap → BGRA-to-RGB → 800×800 rescale (PP-DocLayoutV3) or model-specific size (Heron/PP-S) → CHW float tensor → ONNX inference → post-processing (confidence filter, NMS) → reading order determination (native for PP-DocLayoutV3, XY-Cut++ for Heron/PP-S) → sort by reading order → line detection per block. Pixmap prep runs on thread pool; inference on dedicated `AnalysisWorker` thread. Results cached per-tab in `DocumentState.AnalysisCache`.

**Background read-ahead**: A dedicated `DispatcherTimer` (500ms) progressively analyses all pages when idle, scanning outward from the current page via `BackgroundAnalysisQueue`. Pauses during rail mode to avoid PDFium contention. Results never evicted from cache. The Index section of the OutlinePanel accordion (`Ctrl+Shift+I`) uses `PeekIndexBuilder` to surface detected figures, tables, and equations — showing thumbnails for visual blocks and extracted text (via `PageText.ExtractTextInRect`) for equations.

**VLM integration (Copy as LaTeX)**: `VlmService` in Core sends block crops to any OpenAI-compatible vision API (Ollama, cloud, etc.) via the `OpenAI` NuGet package. `BlockCropRenderer` in Renderer.Skia renders block regions as PNG at 300 DPI with 5% padding. Three access paths: `Ctrl+L` (current rail block), `Ctrl+right-click` (any block), Edit menu. Adapts prompt by block type: equations → LaTeX, tables → Markdown, figures → description. Configured via `AppConfig.VlmEndpoint`/`VlmModel`/`VlmApiKey` (Settings > VLM tab).

**Fallback (model unavailable)**: Without the ONNX model, layout falls back to simple horizontal strip detection. Rail mode still activates but with basic fixed-height blocks instead of detected regions. Download the model via `./scripts/download-model.sh` for full functionality.

### Rail Mode

Activates above `rail_zoom_threshold` when analysis is available. Locks to detected text blocks, advances line-by-line with cubic ease-out snap. Key mechanics: hold-to-scroll with quadratic speed ramping, soft asymptotic block edge clamping (`SoftEase`), `VerticalBias` for preserving vertical offset, pixel snapping for text shimmer reduction. Sub-features: auto-scroll (`P`), jump mode (`J`), line focus blur (`F`), line highlight (`H`), named bookmarks (`B`), semantic jumps to the next/previous heading·figure·table·equation (`DocumentController.NavigateToRole`, exposed via the Navigation menu's "Jump to Next/Previous" submenus + `Ctrl+Shift+H/G/T/E` for forward). Free pan (Ctrl+drag) temporarily exits rail mode for unrestricted pan/zoom to inspect images or equations, snapping back on Ctrl release. Zooming in rail mode preserves horizontal scroll position and line screen position rather than snapping to line start.

### Controller/ViewModel Pattern

`DocumentController` (Core) is the central facade. `MainWindowViewModel` (UI) is a thin wrapper handling only Avalonia concerns. `DocumentState` (Core) holds per-document state; `TabViewModel` (UI) is a thin `[ObservableProperty]` wrapper. `TickResult` from `Controller.Tick(dt)` drives granular UI invalidation. `IThreadMarshaller` abstracts UI thread posting.

### Annotations

Authoring happens in an explicit **annotation mode** (toggle via `ToolBarView`, `Ctrl+E`, the Edit menu, right-click, or tool keys 1–5). The always-on `ToolBarView` carries Browse / Text-Select; in annotation mode it expands to the tools: text-markup (Highlight, Underline, StrikeOut, Squiggly — drag over text, sticky) plus Pen, Rectangle, TextNote, FreeText (drag a box → dialog), and Eraser. A colour/thickness flyout applies to the palette tools (Highlight/Pen/Rectangle). Plain right-click over a detected block gives the block actions (Copy as LaTeX/Markdown/Description/Image) + an Annotation-Mode item.

Annotations render in z-order (highlights below freehand/rectangles, text notes on top; stable sort within each tier). Select/move/resize in browse mode; per-tab undo/redo stacks. The `CommentsView` accordion section lists all annotations (author/body/date + a reviewer-vs-you source badge); clicking one navigates + selects with a rail-aware recenter.

**Stylus (basic, no-pressure)** is X11-first and App-side only (`ViewportPanel`): a pen draws like the mouse in annotation mode, **eraser tip → Eraser tool** for the stroke (`IsEraser`), and **barrel button → free-pan** (`IsBarrelButtonPressed`, Ctrl+drag-equivalent, rail-aware). Design rule: pen detection only *adds* behaviour, never gates the mouse paths — so a pen XWayland misreports as a generic pointer still works as a mouse. Pressure (needs a Core per-point-width model) and all touch/finger/palm-rejection (mobile-app territory) are out of scope. Wayland goes through XWayland today (no shipped native Avalonia backend), where the pen conveniences degrade silently to mouse behaviour — see `docs/stylus-support.md`.

**Storage** goes through Core's `CompositeAnnotationStore`: an annotation lives either **in the PDF** (`Source.InPdf` — native PDF annotations read/written via RailReaderCore) or in an app-managed sidecar at `ConfigDir/annotations/<sha256-of-path>.json` (legacy PDF-adjacent sidecars are migrated, never written back). `AnnotationFileManager` shares one reference-counted in-memory file per PDF across tabs (no last-writer-wins loss); auto-save is one debounced timer per unique PDF. Export to a flattened/annotated PDF via `AnnotationExportService`; export/import JSON (`MergeInto()` appends per page, dedups bookmarks). Named bookmarks live in the same file; `CleanOrphaned()` removes files whose source PDFs are gone.

### Portals (linked context viewports)

A **portal** keeps a referenced figure/table/equation in view while you rail-read past the text that cites it. Entirely shell-side (no Core change); design in `docs/portals-design.md`.

- **Saved portals** link a source block (the referencing text, **line-precise** via `PortalAnchor.Line`) → a target block. Stored in a SHA-256-keyed sidecar at `ConfigDir/portals/<sha>.json` (`Services/PortalSet.cs`), anchored by role + normalized bbox. The sync loop `EvaluatePortals` in `ViewModels/MainWindowViewModel.Portals.cs` renders the target whose source the reading position has reached into the docked `PortalsView` preview. **Pin-until-different**: the shown target persists until a *different* portal's source is crossed (keyed off a single `_displayedPortalId`). Authoring is via the **viewport block right-click** menu (one-shot "Create Portal", or two-step set-target/link).
- **Auto-pinning** (`TryAutoPin` + `Services/ReferenceIndex.cs`): a rail line mentioning "Figure N"/"Table N" auto-pins the float+caption with no manual link. Caption-label parsing + nearest-float association, cached per `PageAnalysis`; toggle CheckBox atop `PortalsView` persisted in `ConfigDir/portal_prefs.json` (`Services/PortalPreferences.cs`). Auto pins share the pin-until-different machinery via an `auto:`-prefixed `_displayedPortalId` and a transient `Portal` (`BuildAutoPinPortal`) that flows through the same marker pass as saved portals.
- **On-page markers** (`PortalMarkerLayer`, always-on): a gutter dot per source line + a corner badge per target block, accent = currently-pinned. `BuildPortalMarkers` groups by anchor; click acts on it (source → show target / pop out on double-click, target → go to source), multi-portal anchors open a chooser. Hit-test in `ViewportPanel`.
- **Pop-out window** (`PortalWindow`, borderless always-on-top, multi-monitor): detach via "Pop out ↗"; **Lock** (`IsPortalViewLocked`) freezes the shown target against all reading-driven switching (auto-releases on dock/close/tab-switch). Shown when `ShouldShowPortalWindow` (detached tracking OR an active peek).
- **Temporary peek** (`ShowBlockInPortal` → `PortalPeekImage`): a non-persisted glance at any block in the pop-out window only, auto-dismissing as you read on (unless Locked). Opened by **right-clicking a detected block** ("Open in Portal (Temporary)") **or right-clicking an Index entry** (`IndexView`). The docked saved-portal preview is never touched.

### Colour Effects

Four SkSL shaders compiled at startup via `SKRuntimeEffect.CreateColorFilter()`. Per-document: each `DocumentState` holds its own `ColourEffect`. Press `C` to cycle. Each effect has a matching `OverlayPalette` for rail overlay colours.

## Configuration

Config: `~/.config/railreader2/config.json` (Linux), `%APPDATA%\railreader2\config.json` (Windows), or `~/Library/Application Support/railreader2/config.json` (macOS). Auto-created with defaults. Editable live via Settings panel. See `Services/AppConfig.cs` for all fields and defaults.

Key fields: `rail_zoom_threshold`, `snap_duration_ms`, `scroll_speed_start/max`, `scroll_ramp_time`, `analysis_lookahead_pages`, `ui_font_scale`, `colour_effect/intensity`, `motion_blur/intensity`, `pixel_snapping`, `line_focus_blur/intensity`, `line_highlight_tint/opacity`, `auto_scroll_line_pause_ms/block_pause_ms`, `auto_scroll_trigger_enabled`, `auto_scroll_trigger_delay_ms`, `jump_percentage`, `dark_mode`, `render_quality`, `custom_max_render_dpi`, `custom_render_tier_step`, `navigable_classes[]`, `centering_classes[]`, `recent_files[]`.

`render_quality` selects a render-DPI preset (`Ultra`/`Quality`/`High`/`Balanced`/`Medium`/`Performance`/`Custom`; the enum is persisted as an integer so member order is fixed). `custom_max_render_dpi` (≥150) and `custom_render_tier_step` (≥1) apply only when `Custom`. Edited live in **Settings → Rendering** (preset dropdown; Custom reveals validated numeric inputs). The desktop seeds `High` as its first-run default (Core's own default is `Quality`); see `App.DefaultRenderQuality`.

`navigable_classes` controls which block types are navigable in rail mode. `centering_classes` controls which block types are horizontally centered when narrower than the viewport (excludes headings by default). Line detection runs for all blocks regardless, so toggling classes doesn't require ONNX re-inference.

## Key Development Notes

- PP-DocLayoutV3 outputs `[N, 7]` tensors with reading order as the 7th column (Global Pointer Mechanism). Heron and PP-S output `[N, 6]` without reading order; reading order is determined post-inference via XY-Cut++
- `PdfOutlineExtractor` uses direct PDFium P/Invoke (`FPDF_*`/`FPDFBookmark_*`) since PDFtoImage doesn't expose bookmarks
- `PdfiumResolver.Initialize()` must be called before any PDFium P/Invoke — registers a `DllImportResolver` that maps "pdfium" to the correct platform-specific native library (pdfium.dll / libpdfium.so / libpdfium.dylib). Called in the GUI entry point
- ONNX runtime pre-loading in `LayoutAnalyzer` static constructor handles Linux (.so) and macOS (.dylib); Windows uses OnnxRuntime's own resolver
- SkiaSharp 3.x explicitly overrides Avalonia's bundled SkiaSharp 2.88 — required for `SKRuntimeEffect.CreateColorFilter()`
- DISTRIBUTION.md documents the release process for all channels (GitHub, Microsoft Store)
- `scripts/` contains a single helper: `download-model.sh` (downloads Heron-INT8 + PP-DocLayoutV3 ONNX models)
- `CleanupService.RunCleanup()` runs at startup and via Help menu (removes cache, temp, old logs)
- `SplashWindow` shows during startup; heavy init deferred via `Dispatcher.Post` at Background priority
- `window.Opened` can fire before `OnLoaded` wiring — guard against this in startup sequencing
- Accessibility/automation: `DocumentViewportAutomationPeer` (on `ViewportPanel`) exposes viewport state to AT-SPI/UIA; chrome carries `AutomationProperties.Name`/`AutomationId`. Inert with no a11y client connected. Avalonia's AT-SPI backend ignores `AutomationProperties.LiveSetting` (Windows/UIA only) — Linux line-advance announcements come from the peer raising `NameProperty`. The peer reads structured state from RailReaderCore's `RailReader.Core.Commands` API (`GetReadingPosition` for the rail line, `GetPageDescription` for the page outline) and is announced via the `PageChanged`/`ReadingPositionChanged` controller callbacks; all extraction is cached on the UI thread so D-Bus-thread queries only read cached strings. `NavigateToRole` (semantic jumps) is AT-SPI-actionable through its Navigation-menu items

## Thread Safety

- **UI thread**: All Avalonia UI, keyboard/mouse, state building, PDFium calls
- **Composition thread**: `CompositionCustomVisualHandler.OnRender()` draws via Skia. Receives immutable state snapshots from UI thread via `SendHandlerMessage()`. Disposes retired `SKImage` instances. Never accesses `DocumentState` directly.
- **Analysis Worker**: Single dedicated thread via `Channel<T>` for ONNX inference
- **Thread pool**: `RenderPagePixmap()` and DPI upgrades via `Task.Run()`
- **Critical**: Never call `PdfService` from background threads (PDFium crashes). Never modify `DocumentState` from the analysis worker — use `IThreadMarshaller` to post to UI thread. Never dispose `SKImage` on the UI thread if the composition thread may still be drawing it — use `RetireImage` message for deferred disposal.
- `AnalysisCache` is written via UI thread marshalling, read during animation frame polls — no locks needed.

## CI / Release Packaging

Releases triggered by pushing a `v*` tag (`.github/workflows/release.yml`).

- **Linux**: `appimagetool` (not `linuxdeploy` — avoids ELF dependency tracing issues with .NET self-contained). Model at `$APPDIR/models/`.
- **Windows (Inno Setup)**: `installer/railreader2.iss`. **Gotcha**: `.iss` paths are relative to the `.iss` file's directory, not CWD.
- **Windows (Microsoft Store)**: MSIX built by CI `build-msix` job (unsigned — Microsoft re-signs during Store review). Manifest at `msix/Package.appxmanifest`, visual assets in `msix/Assets/`. **Gotcha**: Store requires the version's 4th component (revision) to be `0` — CI forces this regardless of the git tag. See `DISTRIBUTION.md` for the Store release workflow.
- **Model search order** (`FindModelPath()`): `AppContext.BaseDirectory/models/` → `$APPDIR/models/` → `AppConfig.ConfigDir/models/` → `LocalApplicationData/railreader2/models/` → `CWD/models/` → walk-up `../models/`

## Debugging

Press `Shift+D` for debug overlay (layout blocks, confidence, reading order, nav anchors). Rail mode activates at >3x zoom — if blocks aren't detected, run `./scripts/download-model.sh`. Animation frame dt is capped at 1/30s to prevent large jumps after stalls.
