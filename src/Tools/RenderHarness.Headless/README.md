# RenderHarness.Headless — documentation-screenshot generator

A **headless** tool that captures the README / website screenshots of the **real
RailReader2 UI** — the genuine menu bar, tab strip, accordion side panel, status
bar, and the composition-thread PDF page + overlay layers — without a desktop
session.

It boots the actual `App` / `MainWindow` / `MainWindowViewModel` under
`Avalonia.Headless` with **real Skia drawing** (`UseHeadlessDrawing = false`),
drives a known UI state per shot (page, zoom, theme, side pane, tool, colour
effect, debug overlay, rail line, search, annotations), pumps the dispatcher +
render timer until the state settles, and saves the captured frame.

Because it renders the real controls and the real layout/rail pipeline, the
output is faithful to the running app — the debug overlay shows real detected
blocks, rail mode frames an actual detected line, the search pane shows real
matches, etc. It builds as a **separate tool** (it references the GUI project)
and does not ship with or bloat the GUI package.

## Running

```bash
# Regenerate every screenshot in screenshots.json (writes into docs/img/)
dotnet run --project src/Tools/RenderHarness.Headless -c Release

# Just one, by name
dotnet run --project src/Tools/RenderHarness.Headless -c Release --only rail_mode

# Custom config
dotnet run --project src/Tools/RenderHarness.Headless -c Release path/to/shots.json
```

The default config writes **straight into `docs/img/`**, overwriting the
committed images. No X11/Wayland/display is required, so it runs in Docker and
GitHub Actions. PDFium and the ONNX layout model must be available (the model is
found via the app's normal `models/` search path — run
`./scripts/download-model.sh` if rail mode / the debug overlay come up empty).

The source PDFs referenced by `screenshots.json` live in `experiments/PDFs/`.

## Adding / updating a screenshot

Edit [`screenshots.json`](screenshots.json). Each `shots` entry:

| Field              | Meaning                                                                  |
|--------------------|--------------------------------------------------------------------------|
| `name`             | Output filename (no extension), e.g. `rail_mode`.                        |
| `pdf`              | Source PDF path, relative to the repo root.                             |
| `page`             | 1-based page number.                                                     |
| `zoom`             | Absolute camera zoom (`1.0` = 100%). `0` = fit page. Above ≈`3.0` engages rail mode. |
| `theme`            | Per-shot `dark` \| `light` override.                                     |
| `uiScale`          | Per-shot UI font-scale override (live `Window.FontSize`, no rebuild).    |
| `width` / `height` | Per-shot window size (device-independent px).                            |
| `sidebar`          | `none` \| `outline` \| `bookmarks` \| `figures` \| `search`.             |
| `tool`             | `none` \| `highlight` \| `pen` \| `rectangle` \| `textNote` \| `eraser`. |
| `colourEffect`     | `none` \| `highContrast` \| `highVisibility` \| `amber` \| `invert`.     |
| `debugOverlay`     | Show the layout-analysis overlay (detected blocks + reading order).      |
| `lineHighlight`    | Rail mode: tint the active line.                                         |
| `lineFocusBlur`    | Rail mode: blur/dim the non-active lines.                               |
| `railLineFraction` | Rail mode: place the active line this fraction down the viewport (e.g. `0.33`). |
| `railAdvanceLines` | Rail mode: advance this many lines from the page top onto a body line.   |
| `search`           | Run this query, open the Search pane, and centre the first on-page match.|
| `annotate`         | Inject demo annotations (highlight/underline/freehand) over real text.   |
| `annotationMode`   | Enter annotation mode so the tool controls are visible.                  |
| `requireAnalysis`  | Wait for ONNX layout analysis before capturing.                          |

Top-level: `outputDir`, `width`, `height`, `theme`, `uiScale`.

**When the UI changes**, nothing here usually needs editing — the screenshots
follow the real UI automatically. Just re-run the harness and commit the
refreshed PNGs. Only touch the harness if you add a new UI state to capture
(wire it in `Program.cs` and add a field here).

## How it stays deterministic

- Fixed window size and theme per shot; UI scale pinned via `uiScale`.
- The real render path is pumped with `AvaloniaHeadlessPlatform.ForceRenderTimerTick()`
  and `Dispatcher.UIThread.RunJobs()` until the document is loaded, analysis is
  ready, the high-DPI re-raster has completed, and the zoom/rail animation has
  settled, so the captured frame is stable.
- Rail shots reset to the top of the page (page-aware) before advancing a fixed
  number of lines, so the active line is the same every run regardless of where a
  previous shot left the shared tab.
- Reproducibility across machines requires the same SkiaSharp/PDFium native libs,
  the bundled Inter font (shipped with the app), and the same ONNX model. Pin
  these in any screenshot-CI image.

## Notes / limitations

- Zoom/pan is set precisely on the tab's per-viewport `Camera` (`t.Camera.Zoom`/
  `OffsetX`/`OffsetY` on the `TabViewModel`, which delegates to its Core `Viewport`),
  followed by `UpdateRenderDpiIfNeeded()` so the page is re-rastered sharp at high zoom.
- All shots reuse a single booted app instance; switching PDFs re-opens the
  document in the same window. An in-flight async DPI raster is drained between
  shots to avoid PDFium contention.
- Injected demo annotations are written only to the in-memory annotation file and
  cleared after capture — they are never persisted to the user's annotation store.
