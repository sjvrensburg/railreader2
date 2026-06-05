# RenderHarness.Headless — documentation-screenshot generator

A **headless** tool that captures documentation screenshots of the **real
RailReader2 UI** — the genuine menu bar, tab strip, accordion side panel, status
bar, and the composition-thread PDF page + overlay layers — without a desktop
session.

It boots the actual `App` / `MainWindow` / `MainWindowViewModel` under
`Avalonia.Headless` with **real Skia drawing** (`UseHeadlessDrawing = false`),
drives a known UI state per shot (page, zoom, side pane, active tool, rail mode),
pumps the dispatcher + render timer until the state settles, and saves the
captured frame to `docs/img/`.

Because it renders the real controls and the real layout/rail pipeline, the
output is faithful to the running app — the Figures pane shows real detected
thumbnails, rail mode frames an actual detected line, etc. It builds as a
**separate tool** (it references the GUI project) and does not ship with or bloat
the GUI package.

## Running

```bash
# Regenerate every screenshot in screenshots.json
dotnet run --project src/Tools/RenderHarness.Headless -c Release

# Just one, by name
dotnet run --project src/Tools/RenderHarness.Headless -c Release --only rail-mode-reading

# Custom config
dotnet run --project src/Tools/RenderHarness.Headless -c Release path/to/shots.json
```

No X11/Wayland/display is required, so it runs in Docker and GitHub Actions.
PDFium and the ONNX layout model must be available (the model is found via the
app's normal `models/` search path — run `./scripts/download-model.sh` if rail
mode / the Figures pane come up empty).

## Adding / updating a screenshot

Edit [`screenshots.json`](screenshots.json). Each `shots` entry:

| Field             | Meaning                                                                  |
|-------------------|--------------------------------------------------------------------------|
| `name`            | Output filename (no extension), e.g. `sidebar-navigation-active`.         |
| `pdf`             | Source PDF path, relative to the repo root.                              |
| `page`            | 1-based page number.                                                     |
| `zoom`            | Absolute camera zoom (`1.0` = 100%). `0` = fit page. Above ≈`3.0` engages rail mode. |
| `sidebar`         | `none` \| `outline` \| `bookmarks` \| `figures` \| `search`.             |
| `tool`            | `none` \| `highlight` \| `pen` \| `rectangle` \| `textNote` \| `eraser`. |
| `requireAnalysis` | Wait for ONNX layout analysis before capturing (needed for rail mode and a populated Figures pane). |

Top-level: `outputDir`, `width`, `height` (window size, device-independent px),
and `theme` (`dark` or `light`).

**When the UI changes**, nothing here usually needs editing — the screenshots
follow the real UI automatically. Just re-run the harness and commit the
refreshed PNGs. Only touch the harness if you add a new UI state to capture
(wire it in `Program.cs` and add a field here).

## How it stays deterministic

- Fixed window size; theme pinned via the config.
- The real render path is pumped with `AvaloniaHeadlessPlatform.ForceRenderTimerTick()`
  and `Dispatcher.UIThread.RunJobs()` until the document is loaded, analysis is
  ready, and the zoom/rail animation has settled, so the captured frame is stable.
- Reproducibility across machines requires the same SkiaSharp/PDFium native libs,
  the bundled Inter font (shipped with the app), and the same ONNX model. Pin
  these in any screenshot-CI image.

## Notes / limitations

- Zoom is driven through the app's real keyboard-zoom path so rail mode engages
  via the genuine controller transition; the final zoom lands within a few percent
  of the requested value (logged per shot).
- All shots reuse a single booted app instance; switching PDFs re-opens the
  document in the same window.
