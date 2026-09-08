# Rendering / dispatcher performance plan — issues #222, #223, #224

**Status**: plan only, nothing implemented. Branch `perf/render-hitches-222-224` (created off `main` at
`c1518ff`, app version 3.62.1.0, RailReaderCore 0.61.1).

**Audience**: the agent implementing this. Read the whole document before touching code — the three
issues share two files (`Views/PdfPageLayer.cs`, `ViewModels/MainWindowViewModel.cs`) and one live-test
pass, so they are best done as one branch with separate commits.

All three issues came out of the 2026-09-07 rendering-performance investigation that followed #221
(the splash `ProgressBar` UI-thread spin).

---

## 0. Prerequisite — bump RailReaderCore 0.61.1 → 0.61.3

Core has moved on twice since the version this repo pins, and both releases are pure performance
fixes that land directly on #222's problem surface. Do this first, as its own commit, so the later
measurements are taken against the code that will ship.

| Core release | Change | Relevance |
|---|---|---|
| **0.61.2** | `SkiaPdfService.GetPageSize/GetPageSizes` read from a page-size array built once instead of re-opening and re-parsing the PDF on every call (~7.8 ms/call on a 15-page file, under `PdfiumGate.Lock`, from `Viewport.LoadPageBitmap` / `PrefetchPage` / `RenderPagePixmap`'s `FitPageToTarget`). Core #114. | Removes ~8 ms of gated UI-thread work per page render — the same code path that produces the bitmaps #222 then uploads. |
| **0.61.3** | `Viewport.EnsureRenderWindow` rasterises continuous-scroll **neighbour** pages one DPI tier below the anchor (`DocumentModel.CalculateNeighbourRenderDpi`); a neighbour promoted to anchor (`Viewport.TryTakeFromWindow`) is force-re-rendered at full quality on the next tick. Core #115 (measured ~1.25 GB RSS on a 15-page Letter doc at 300 % zoom before the fix). | **Directly reduces #222**: each neighbour texture is now `(anchorDpi − tierStep)² / anchorDpi²` of its previous pixel count — at the `High` preset (525 DPI cap, 85 DPI tier step) that is ~0.70× the bytes uploaded per neighbour. |
| **0.61.3** | New additive setting `CoreSettings.ContinuousRenderWindowMaxMegapixels` / `AppConfig.ContinuousRenderWindowMaxMegapixels` (default 128 MP), replacing the hardcoded `RenderDpi.MaxMegapixels × 2` window budget. | Gives us a knob to cap the aggregate size of the render window — i.e. how much texture can land in one frame — without touching the anchor's own render quality. |

### Steps

1. Bump `Version` **0.61.1 → 0.61.3** in all 7 `PackageReference`s across three project files:
   - `src/RailReader2/RailReader2.csproj:39-45`
   - `src/RailReader2.Cli/RailReader2.Cli.csproj:13-18`
   - `tests/RailReader.Export.Tests/RailReader.Export.Tests.csproj:19-23`
2. `dotnet build RailReader2.slnx -c Release` and `dotnet test tests/RailReader.Export.Tests`.
3. Bump `<Version>` in `src/RailReader2/RailReader2.csproj:6` → **3.63.0.0** (single source of truth;
   the About dialog and the Inno installer both derive from it — see memory `MEMORY.md § Versioning`).

`AppConfig` lives in Core (`RailReader.Core.Pdfium/AppConfig.cs`), so the new field, its default and its
`ToCoreSettings()` mapping all arrive with the package bump — no shell change is required for it to
work. **Do not add a Settings-window control for it in this pass** (scope); it is reachable from
`config.json` if the user wants to experiment, and §2 below decides whether we want a non-default value
at all.

---

## 1. Issue #223 — motion blur costs a filter pass for an invisible ≤0.12 px sigma

**Smallest and most self-contained. Do it first.**

### Confirmed diagnosis

`Views/PdfPageLayer.cs:183-202`:

```csharp
float maxSigma = state.MotionBlurIntensity * MaxBlurSigma;   // MaxBlurSigma = 0.35f  (line 62)
float zoom = Math.Max(state.Zoom, 0.01f);
motionSigmaX = (float)(s * s * s * maxSigma) / zoom;         // s = ScrollSpeed, 0..1
```

The sigma is expressed in **page units**, and the draw happens under `canvas.Concat(page.Camera)`
where `PageCamera` (`Views/DocumentView.axaml.cs:493-497`) is `SKMatrix.CreateScaleTranslation(zoom, zoom, …)`.
Skia maps the filter sigma through the CTM, so:

```
sigma_device = sigma_local × (canvas.TotalMatrix.ScaleX × zoom)
             = (speed³ × intensity × 0.35 / zoom) × (dpiScale × zoom)
             = speed³ × intensity × 0.35 × dpiScale
```

The `/ zoom` exactly cancels the camera scale, so the device sigma is zoom-independent and, at the
shipped default `motion_blur_intensity = 0.33` and full speed, is **0.1155 px at 1.0 DPI scale**. Skia
skips a blur below sigma ≈ 0.03 but not at 0.1, so every animating frame pays a real Gaussian
image-filter pass over the visible region for an effect nobody can see.

`MinBlurSigma = 0.5f` (line 71) already exists but is only consulted for the *neighbour* line-focus
blur (line 215) — the motion path has no threshold at all.

### Change

In `OnRender`, compute the sigma in **device pixels**, threshold it, then convert back to local units
for the filter. Replace the `MaxBlurSigma` page-unit constant with a device-pixel one:

```csharp
// Motion-blur sigma at full speed, in DEVICE pixels at intensity 1.0. The old constant (0.35, in
// page units, divided by zoom) worked out to <= intensity * 0.35 px on screen — invisible, but still
// a full Gaussian pass every animating frame (#223).
private const float MotionBlurMaxDeviceSigma = 3.0f;
```

```csharp
float motionSigmaX = 0, motionSigmaY = 0;
if (state.MotionBlur && state.MotionBlurIntensity > 0)
{
    float zoom = Math.Max(state.Zoom, 0.01f);
    // canvas.TotalMatrix is read BEFORE the per-page camera concat, so ScaleX is the compositor's
    // DPI scale; the camera adds `zoom` on top. Skia maps the filter sigma through the full CTM,
    // so divide the wanted device sigma by both to get the local-space value to hand the filter.
    float ctmScale = Math.Max(canvas.TotalMatrix.ScaleX * zoom, 0.0001f);
    float maxDevice = state.MotionBlurIntensity * MotionBlurMaxDeviceSigma;

    float deviceX = 0, deviceY = 0;
    if (state.ScrollSpeed > MinSpeedThreshold)
    {
        double s = state.ScrollSpeed;
        deviceX = (float)(s * s * s * maxDevice);
    }
    if (state.ZoomSpeed > MinSpeedThreshold)
    {
        double z = state.ZoomSpeed;
        float zDevice = (float)(z * z * z * maxDevice);
        deviceX = Math.Max(deviceX, zDevice);
        deviceY = Math.Max(deviceY, zDevice);
    }

    // Below half a device pixel the blur is imperceptible but still costs a full image-filter pass
    // over the visible region — skip it entirely (#223).
    if (deviceX >= MinBlurSigma || deviceY >= MinBlurSigma)
    {
        motionSigmaX = deviceX / ctmScale;
        motionSigmaY = deviceY / ctmScale;
    }
}
var motionBlurFilter = GetCachedBlurFilter(motionSigmaX, motionSigmaY);
```

`GetCachedBlurFilter` already returns `null` when both sigmas are `<= 0`, and `OnRender`'s draw already
takes the filter-free `DrawImage` overload when `effectFilter` and `blurFilter` are both null — so the
skip needs no further plumbing.

**Note on the cache tolerance**: `GetCachedBlurFilter` invalidates on a local-sigma delta > 0.05f
(lines 327-329). Local sigma is now `device / (dpiScale × zoom)`, so at high rail zoom the local values
get small and the filter is rebuilt less often — that is fine (fewer native allocations), but be aware
the tolerance is now zoom-relative. Do not "fix" it by comparing device sigmas unless you also key the
cache on `ctmScale`.

### Choosing `MotionBlurMaxDeviceSigma`

`3.0f` is the recommended value:

- At the shipped default intensity `0.33` it gives ~0.99 px at full speed — a real but subtle smear.
- Because sigma ∝ speed³, the 0.5 px threshold is crossed only above speed ≈ **0.79** (`(0.5/0.99)^(1/3)`),
  so the filter pass is paid in roughly the top fifth of the speed curve and is free everywhere else.
  This is strictly cheaper than today *and* the effect finally does something.
- At intensity 1.0 it gives 3 px at full speed, engaging above speed ≈ 0.55.

If the user prefers the effect stay invisible, set `MotionBlurMaxDeviceSigma = 0.35f` instead: the
threshold then never trips at any intensity ≤ 1.0 and the motion-blur filter is dead code at runtime —
a pure win with zero visual change. **This is a user-facing visual decision: ask before picking.**
Recommendation is `3.0f` (make the feature real, and cheaper than it is now).

### Secondary finding while you are in this code (optional, low priority)

`NeighborFocusSigmaPerIntensity = 4.0f` (line 70) is **not** divided by zoom, so the continuous-scroll
neighbour blur's device sigma is `4 × intensity × zoom × dpiScale` — at a typical rail zoom of 4–6× that
is 16–24 device px per intensity unit over a whole page. Skia downsamples for large sigmas so the cost
is sublinear, but consider clamping the device sigma to ~16 px. Only do this if the live test shows
continuous-scroll + line-focus-blur is slow; otherwise leave it alone and note it here.

### Verification

`RenderHarness.Headless` renders **static** frames only (`ScrollSpeed`/`ZoomSpeed` = 0), so it
**cannot** exercise the motion-blur path — it will confirm the non-animating case is byte-identical,
which is the useful regression guard. The blur itself must be live-tested by the user (rail scroll hold,
zoom animation).

---

## 2. Issue #222 — synchronous 72–128 MB mipmapped texture upload inside `OnRender`

`Views/PdfPageLayer.cs:304-322`, `GetOrUploadTexture`, called from `OnRender` at line 229. The first
frame after a new bitmap arrives, `SKImage.ToTextureImage(grContext, mipmapped: …)` runs on the
compositor thread and that frame stalls for the whole upload + mip generation. At the `High` preset's
525 DPI cap a Letter page is 4462 × 5775 px ≈ 25.8 MP ≈ **103 MB** (+~33 % with mips). Under continuous
scroll several pages can need an upload in the same frame.

### What is already done — do not undo it

`MipmapSkipMagnifyFactor` (line 80) already skips the mip chain when the texture is clearly *magnified*.
Per the v3.31.0.0 code-review conclusion recorded in memory, the decision is frozen per upload, so
**defaulting to mips (`!magnified`) is the quality-safe direction** — a near-1:1 upload made mip-less
would shimmer when zoomed back out. Do not widen the skip to "minified by ≥1.5×".

Also recorded as deliberately **not** touched: the anchor's raster DPI / megapixel cap (lowering it
costs exactly the high-zoom text sharpness the user relies on), the background-analysis window, and the
ONNX model choice.

### Phase 1 — measure (mandatory; do not skip to the fixes)

The issue's own first bullet. The estimate ("tens to low hundreds of ms") has never been measured.

Add a `Stopwatch` around the `ToTextureImage` call in `GetOrUploadTexture` and log page number, source
`Width × Height`, megapixels, `mipmapped` flag and elapsed ms.

**Two constraints on the instrumentation:**

- `ConsoleLogger.Debug` writes to `session.log` through a `StreamWriter` with `AutoFlush = true`
  (Core `RailReader.Core.Pdfium/ConsoleLogger.cs`). A synchronous flushing file write on the
  **composition thread** would itself perturb what you are measuring.
- So: gate it behind an env var (`RR_GPU_UPLOAD_TIMING=1`, parsed once into a `static readonly bool`)
  **and** only log when the elapsed time exceeds a threshold (e.g. 3 ms). Uploads happen only on
  source change, not per frame, so this is a handful of lines per session.

Collect numbers for: (a) a zoom settle from below to above the DPI cap on a Letter page, single-page
mode; (b) continuous scroll through a 15-page document at 300 % zoom; (c) the same with the render
window budget lowered. **Record the numbers in this file (or the issue) before implementing Phase 2** —
they decide whether Phase 3 is worth it.

#### Phase 1 results (2026-09-08, live-driven via xdotool, `experiments/PDFs/Attention_is_all_you_need.pdf`, 15 pages, `render_quality=High`/525 DPI cap)

Zoom settle on page 0/1, `RR_GPU_UPLOAD_TIMING=1`, real `ToTextureImage` timings from `session.log`:

| Source size | Megapixels | Mipmapped | Elapsed |
|---|---|---|---|
| 1275×1649 | 2.1 MP | true | 33.0–49.8 ms |
| 2550×3299 | 8.4 MP | true | 188.4 ms |
| 3187×4125 | 13.1 MP | false (magnified) | 119.6 ms |
| 4462×5775 | 25.8 MP | false (magnified) | 216.0 ms |
| 5100×6599 | 33.7 MP | false (magnified) | 269.7 ms |

The 4462×5775/25.8 MP row is exactly this document's own reference case from this plan's §2 intro
(Letter page at the 525 DPI cap, ≈103 MB before mips) — **confirmed at 216 ms**, a single-frame stall
~13× a 16 ms frame budget. This settles the Phase 3 question: **yes, the anchor upload is the hitch**,
and Phase 3 (upload only the visible sub-rect) is worth doing on a future pass. Not implemented in this
branch — out of scope for this session's budget-only fix (§Phase 2).

CPU comparison, `perf/render-hitches-222-224` (post-#224 fix) vs `main` @ `c1518ff` (pre-fix), same PDF,
same interaction (zoom to 939%, engage rail, hold Right for a sustained scroll, sampled via
`top -H -d 0.15`, 3 runs each): the compositor/render thread cost is unchanged (~27–29% avg either
branch — inherent Skia draw cost, not a regression target). The **main dispatcher thread** (where
`DispatcherTimer` ticks run) dropped from **~20–21% avg / ~33–47% max (main)** to **~7–9% avg / ~19–64%
max (fixed)** — directly confirms the #224 poll-timer fix removes real, measurable UI-thread cost during
sustained animation, not just a theoretical one.

Continuous-scroll RSS (browse mode, 197% zoom — the highest zoom reliably below `rail_zoom_threshold=3`
for this window size, so continuous scroll's neighbour-page path stays engaged; scrolled start to end of
the 15-page doc): both branches held steady around 900 MB–1.05 GB through the bulk of the document, well
under the ~1.25 GB Core #115 baseline this plan cites. **Inconclusive as a Core-0.61.3-specific
comparison** — the branch under test (with the Core bump) actually spiked slightly *higher* at the very
last page (~1.35 GB vs ~1.06 GB on main), but that page has a complex vector attention-diagram figure and
the spike lines up with `[Analyzing...]` (background ONNX layout analysis of the final pages) rather than
the render-texture path the neighbour-DPI reduction touches — the two effects aren't well separated by
this test. Re-measure at the full 300% with a wider window (or with analysis pre-warmed) if a tighter
before/after on the render-texture memory specifically is wanted later.

### Phase 2 — cheap structural wins

#### 2a. Per-frame upload budget with stale-texture fallback

The key enabling insight: the draw always maps the **whole** source image to the **whole** page rect
(`srcRect = SKRect.Create(drawImage.Width, drawImage.Height)` → `destRect = SKRect.Create(0, 0, PageW, PageH)`,
lines 245/240). A texture from a *different DPI tier* therefore draws to exactly the same place — just
softer. **Continuing to draw a stale texture for a frame or two is geometrically correct**, which is what
makes deferral safe.

Change `_gpuTextures` from `Dictionary<int, (SKImage Texture, SKImage Source)>` so a resident texture can
outlive its source, and in `OnRender`:

- Count uploads performed this frame; budget **one new upload per frame**.
- The **anchor** page (`page.IsAnchor`) always gets its upload — it is what the user is reading.
- A non-anchor page that needs an upload and has no budget left: draw its **resident** texture if it has
  one; if it has none, `continue` (skip the page — `ViewportPanel`'s own grey `Background` is the gap
  colour, the same treatment an in-flight render already gets at line 226).
- If any page was left with a stale or missing texture, call `Invalidate()` before returning from
  `OnRender` so the compositor schedules another frame and the deferred uploads drain one per frame.
  (`Invalidate()` is already used from `OnMessage` at line 147; `RegisterForNextAnimationFrameUpdate()`
  is the alternative if `Invalidate()` from inside `OnRender` misbehaves.) Termination is guaranteed
  because every such frame uploads at least one texture.
- Retire the superseded texture only once its replacement is resident. Watch the disposal ordering
  carefully — the existing code disposes `old.Texture` immediately before the new `ToTextureImage`;
  with deferral you must not dispose a texture that is still being drawn this frame.

Expected effect: turns an N-page multi-hundred-ms stall under continuous scroll into N single-page
stalls spread over N frames. **It does not remove the anchor's own hitch** — that is Phase 3's job. Be
honest about this in the commit message.

#### 2b. Raise `SkiaOptions.MaxGpuResourceSizeBytes`

Avalonia 12.0.4's default is 28 MiB — smaller than a single page texture. Textures owned by a live
`SKImage` are not purged against this budget, but Skia's scratch allocations (blur passes, layers,
mip scratch) are, so a too-small budget causes churn exactly during the frames that also blur.

Verified against Avalonia 12.0.4 by reflection: the type is **`Avalonia.SkiaOptions`** (namespace
`Avalonia`, not `Avalonia.Skia` — `Program.cs` already has `using Avalonia;`), with members
`long? MaxGpuResourceSizeBytes` and `bool UseOpacitySaveLayer`. Leave `UseOpacitySaveLayer` alone.

In `Program.BuildAvaloniaApp()` (`src/RailReader2/Program.cs`), alongside the existing
`X11PlatformOptions` block:

```csharp
// Avalonia's default Skia GPU resource budget is 28 MiB — smaller than one page texture at our
// reading DPI tiers, so Skia's scratch allocations (blur passes, mip scratch) thrash against it
// during exactly the frames that also blur. Live page textures are owned by their SKImage and are
// not purged against this budget; raising it only buys headroom for scratch. (#222)
builder = builder.With(new SkiaOptions { MaxGpuResourceSizeBytes = 256L * 1024 * 1024 });
```

Do this **after** 2a's measurement so its effect is separable. On a low-VRAM machine 256 MiB may be too
generous; 128 MiB is the conservative choice. This is a budget, not an allocation.

#### 2c. Consume the Core 0.61.3 neighbour-DPI reduction

Free with §0 — no shell change. Re-run the Phase 1 continuous-scroll measurement after the bump to
quantify it, and if the aggregate is still too heavy, try
`"continuous_render_window_max_megapixels": 64` in `config.json` and re-measure. Only propose a default
change (which would be a Core change, not a shell one) if the data supports it.

### Phase 3 — only if Phase 1 says the anchor upload is the hitch

**Upload only the visible sub-rect of the anchor page.** This is the real structural fix and the only
one that removes the zoom-settle stall.

At rail-reading zoom the anchor texture is ~25 MP but only ~2–8 MP is ever on screen. After
`canvas.Concat(page.Camera)`, `canvas.LocalClipBounds` gives the visible rect directly in **page**
coordinates; map it to source-image pixels, expand by a generous margin (e.g. one viewport height above
and below, clamped to the image), and upload only that region. Cache the covered source rect alongside
the texture and re-upload only when the required rect escapes it (hysteresis — otherwise every pan
re-uploads). Draw with `srcRect` = the covered rect in image coords and `destRect` = the corresponding
sub-rect in page coords.

- SkiaSharp 3 exposes the underlying `sk_image_make_subset` / `sk_pixmap_extract_subset` /
  `SKBitmap.ExtractSubset`; **verify the exact managed API surface before designing around it**, and note
  that Core 0.61.1 marks rendered-page bitmaps immutable so pixel-sharing works — any derived bitmap you
  create must be `SetImmutable()`'d too or `SKImage.FromBitmap` will copy.
- Mipmaps on a subset: the `magnified` heuristic still applies, but edge sampling at the subset boundary
  needs thought (the margin should exceed the mip footprint).
- Trade-off: more uploads overall, each far smaller — during line-by-line rail advance a margin of one
  viewport height covers many lines, so re-uploads are rare and cheap. Never a visible stall.

**Also investigated and expected to be dead ends** — record the outcome rather than silently dropping:

- *Async upload on a shared GL context.* Would need Avalonia to hand out a second context
  (`IOpenGlTextureSharingRenderInterfaceContextFeature` or equivalent). Check whether Avalonia 12.0.4
  exposes it at all; if not, close this option in the issue with that finding.
- *Split the upload across frames (no mips first, mips later).* SkiaSharp has no "add mips to an existing
  texture" call, so this means uploading twice — it only helps if the base upload is much cheaper than
  the mip pass, which Phase 1's `mipmapped` split will tell you.

---

## 3. Issue #224 — `DispatcherTimer`s are ~16–19 ms of UI-thread time per tick on Avalonia 12 / X11

### Background already established in the repo

`src/RailReader2/Program.cs` carries the full diagnosis in a comment: Avalonia's `X11PlatformThreading`
does **not** implement `IDispatcherImplWithExplicitBackgroundProcessing`, so a low-priority dispatcher
job arms a `now + 1 ms` OS timer and `RunLoop` polls it in a tight `epoll_wait`/`continue` cycle until
the job gets through. The measured cost (from #224): 100 ms timer → 19 % of a core, 50 ms → 33 %, 20 ms →
80 %, all with an empty `Tick` body. **Priority does not help** — a `Normal`-priority timer measured the
same 16 %. The GLib main loop remains an opt-in fallback (`RR_X11_GLIB=1`) but is not the default because
the native dispatcher paces animation noticeably better.

### 3a. `_pollTimer` — the headline fix

`ViewModels/MainWindowViewModel.cs:655-676` (setup) and `:827-828` (start).

**The bug**: `RequestAnimationFrame()` starts `_pollTimer` unconditionally whenever it is not already
enabled, and `RunAnimationFrame` calls `RequestAnimationFrame()` at the end of every animating frame.
Meanwhile the tick body's first line is `if (_animationRequested) return;` — and `_animationRequested`
is true for almost the whole inter-frame window. So during **all** rail reading, auto-scroll and zoom
animation, a 100 ms `DispatcherTimer` runs continuously, does nothing on every tick (it returns before it
can even reach its own `_pollTimer.Stop()`), and costs ~19 % of a core on X11.

**The fix** — start it only when the analysis worker is actually busy (`AnalysisWorker.IsIdle` is a
UI-thread `HashSet.Count` check, free to call):

```csharp
// In RequestAnimationFrame() — replace the unconditional start:
if (_pollTimer is { IsEnabled: false } && _controller.Worker is { IsIdle: false })
    _pollTimer.Start();
```

```csharp
// At the end of RunAnimationFrame(), replacing `if (anyAnimating) RequestAnimationFrame();`:
if (anyAnimating) RequestAnimationFrame();
else if (_pollTimer is { IsEnabled: false } && _controller.Worker is { IsIdle: false })
    _pollTimer.Start();          // analysis still in flight after the last animating frame
```

The second half closes the only hole: work submitted *during* the final animating frame would otherwise
leave no running timer to drain its result.

**Behaviour is preserved exactly**, because the tick already no-ops during animation. Do not "restore"
`SubmitPendingLookahead` into the animating path — it never ran there (the early return precedes it);
`DocumentController.PumpAnalysis(quiescent: true)` handles lookahead on quiescent frames and that is
unchanged.

**Expected win: removes a perpetual 100 ms dispatcher timer from the entire steady-state reading path.**
This is the single biggest item in this document; do it first within #224 and measure it on its own.

### 3b. `_backgroundTimer` (500 ms)

`MainWindowViewModel.cs:681-701`. Runs whenever background read-ahead has work — which on a long document
is most of a session — and its tick already refuses to submit while rail is active
(`if (!railActive && Worker.IsIdle && hasWork)`), so during rail reading it is pure overhead on top of 3a.

**Recommended**: make the interval adaptive rather than stopping/restarting the timer (a stop needs a
rail-deactivation hook to restart, and the obvious `RunAnimationFrame` restart point loops):

```csharp
// In the tick, after the submit decision:
var wanted = railActive ? TimeSpan.FromMilliseconds(2000) : TimeSpan.FromMilliseconds(500);
if (_backgroundTimer!.Interval != wanted) _backgroundTimer.Interval = wanted;
```

Full read-ahead speed when the reader is idle (where it belongs), a quarter of the cost while rail
reading (where it cannot submit anyway). Results still drain on the slower cadence, and 3a's poll timer
covers the in-flight case.

Simpler fallback if the adaptive interval proves fiddly: raise the fixed interval 500 → 1000 ms
(6.5 % vs ~19 % measured).

### 3c. `_scanAllTimer` (50 ms → 200 ms)

`MainWindowViewModel.cs:981-983`, tick at `:989`. 50 ms is 33 % of a core for a poll whose work — one
ONNX layout inference — takes 100 ms to 1 s per page. A 20 Hz poll buys nothing but does steal UI-thread
time from the sweep it is driving, and it runs behind a modal progress overlay where the user is watching.

Change the interval to 200 ms **and keep the stall watchdog's wall-clock window identical**:
`ScanAllStallTickLimit` (`:201`) is `600` ticks × 50 ms = 30 s, so it becomes `150` at 200 ms. Update the
constant and its comment together — leaving it at 600 would silently stretch the watchdog to 2 minutes.

Progress text updates 5×/s instead of 20×/s, which is still smooth for a per-page counter.

### 3d. `_startupRailTimer` (150 ms, ≤ 80 attempts) — low priority

`ViewModels/MainWindowViewModel.Navigation.cs:216-227`. Short-lived (≤ 12 s) and only on a
startup-with-rail launch. Raise to 250 ms and drop the attempt cap to 48 to keep the same ~12 s window.
Optional; include it only if it costs nothing to review.

### 3e. Debounce timers — leave them alone

`Views/SearchView.axaml.cs:124`, `Views/SearchBarView.axaml.cs:40` (200 ms), `Views/IndexView.axaml.cs:169`
(1 s). All three stop themselves in their own `Tick` (the index one when `_peekDirty` is clear), so they
run for one interval per burst. Not worth churn. Mention them in the commit message as "checked, already
self-stopping".

### 3f. Durable guidance — update `CLAUDE.md`

Add to the **Thread Safety** or **Debugging** section (this class of bug has now bitten three times —
#221, #224, and the X11 spin in `project_x11_glib_spin`):

> **`DispatcherTimer` is expensive on Avalonia 12 / X11** — roughly 16–19 ms of UI-thread time per tick
> regardless of the tick body or the priority, because `X11PlatformThreading` does not implement
> `IDispatcherImplWithExplicitBackgroundProcessing` (see the comment in `Program.cs`). A 100 ms timer
> costs ~19 % of a core; 20 ms costs ~80 %. Never leave one running in a steady-state path: prefer a push
> callback or the existing animation-frame loop, gate the `Start()` on there actually being work, and use
> ≥ 500 ms when you genuinely must poll. When triaging "the app feels warm at rest", start with
> `top -H -p <pid>` — a busy main thread at rest is this bug class.

### 3g. Optional empirical check

Re-run the #224 micro-benchmark with `RR_X11_GLIB=1` to see whether the GLib main loop makes timers
cheaper. If it does, record it in the issue as a documented workaround — but **keep the native
dispatcher as the default**, per the reasoning already written into `Program.cs` (animation smoothness).

### 3h. Follow-up for RailReaderCore — file, do not implement here

The proper fix for `_pollTimer` is to delete it. Core's `AnalysisWorker` (`Services/AnalysisWorker.cs`)
exposes only `Submit` / `Poll` / `IsInFlight` / `IsIdle`; results land in `_resultChannel` from a
background stage thread with no notification, so the host has to poll. Propose:

> **`AnalysisWorker`: push a "result available" signal to the host.** Take an optional
> `Action? onResultAvailable` in the constructor (or expose an event) and invoke it via the existing
> `IThreadMarshaller.Post` right after a result is written to `_resultChannel`. The host can then call
> `DocumentController.PollAnalysisResults()` on arrival and request an animation frame, deleting its
> 100 ms poll timer entirely — and drive background read-ahead submission off result arrival rather than
> a 500 ms timer (§3b). Must fire at most once per enqueue and be safe to invoke after `Dispose`.

Open it against `sjvrensburg/RailReaderCore`, link railreader2#224, and note in #224 that §3a is the
shell-side mitigation until it lands.

---

## 4. Suggested commit sequence on `perf/render-hitches-222-224`

1. `docs: plan for the #222/#223/#224 rendering + dispatcher perf pass` (this file).
2. `chore: bump RailReaderCore packages 0.61.1 -> 0.61.3` + `chore: bump version to 3.63.0.0`.
3. `perf: skip the motion-blur filter below half a device pixel (#223)` — plus the sigma rescale, if the
   user takes the recommendation.
4. `diag: time GPU texture uploads behind RR_GPU_UPLOAD_TIMING (#222)` — land this, gather numbers, then
   decide 2a/2b/3 and append the measurements to this document.
5. `perf: budget GPU texture uploads to one page per frame (#222)`.
6. `perf: raise the Skia GPU resource budget above one page texture (#222)`.
7. `perf: stop the 100 ms analysis poll timer running through every animation (#224)`.
8. `perf: back off the background/scan-all dispatcher timers (#224)`.
9. `docs: record the DispatcherTimer cost in CLAUDE.md (#224)`.

Keep 7 in its own commit — it is the one with the clearest measurable claim.

---

## 5. Verification

```bash
dotnet build RailReader2.slnx -c Release
dotnet test tests/RailReader.Export.Tests
dotnet run --project src/Tools/RenderHarness.Headless -c Release --only rail_mode
```

**Screenshot-harness protocol** (from memory `feedback_rendering_performance`): the harness is
deterministic for the *same* binary but its committed `docs/img/*.png` baselines drift across Core
upgrades — so diff your **edited vs un-edited build of the current Core**, not against the committed PNG.
Revert any `docs/img/` churn the harness writes; screenshots are only regenerated for deliberate UI
changes. And remember it only renders static frames, so it cannot cover motion blur or the multi-frame
upload budget.

**Live tests — ask the user to run these; do not launch the GUI yourself**
(memory `feedback_no_gui_spawning`):

1. `top -H -p <pid>` with a document open and idle → main thread should be at the 3.5–6 % floor.
2. `top -H -p <pid>` while holding rail scroll → confirm the ~19 % poll-timer overhead from §3a is gone.
3. Zoom in past the DPI cap and let it settle on a dense page → is the hitch gone/smaller? (#222)
4. Continuous scroll (Settings ▸ Rendering ▸ "Continuous scrolling") through a long document at 300 %
   zoom → no blank pages beyond the existing in-flight-render gap; check RSS against Core #115's
   ~1.25 GB baseline.
5. Rail scroll hold and a zoom animation → is the motion blur now perceptible/acceptable? (#223)
6. Full-document scan (scan-all) → progress still updates smoothly, completes, watchdog untouched.
7. Rail reading with line-focus blur on, in continuous mode → neighbour pages still blurred correctly.

## 6. Process constraints

From memory, and they are firm:

- Already on branch `perf/render-hitches-222-224` off `main`. **Never edit `main` directly.**
- **Test before opening a PR**, and get **separate explicit authorisation for each of** push / PR /
  merge / tag. Do not chain them.
- **Do not programmatically launch the GUI binary** — hand the live-test list above to the user.
- If a referenced Core symbol stops resolving after the package bump, check whether it was made
  non-internal or moved; the `InternalsVisibleTo` grant covers `internal`, not `private`.
