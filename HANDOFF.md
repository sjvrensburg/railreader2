# HANDOFF: YOLO26 → YOLOX-nano migration

You are picking this up on a different machine from a fresh checkout. Read this whole file before touching code. The previous instance left infrastructure that already works — your job is a targeted detector swap, not a rewrite.

---

## TL;DR

1. **Goal:** swap the AGPL-3.0 YOLO26-DLA detector for an Apache-2.0 YOLOX-nano trained on the same dataset, to keep RailReader2 MIT.
2. **Branch:** you are on `experiment/yolox-layout` (this branch). It was forked from `experiment/yolo26-layout` (commit `b04161f`) which has the working YOLO26 implementation as a comparison baseline. **Do not delete or merge that branch** until validation confirms YOLOX matches its quality.
3. **What carries over unchanged:** XY-cut reading-order sorter, 16-class label table, validation harness, line-detection pipeline, all C# integration points. The detector is the only moving part.
4. **What's pending:** train YOLOX-nano (user does this), drop the resulting `yolox_dla.onnx` into `models/`, update the inference adapter (~30–80 LOC depending on ONNX export options), re-run the validation harness.

If the user has already trained the model and given you the path, jump to **§5. Integration steps**. Otherwise, read **§4. Training recipe** to brief them on what to produce.

---

## 1. Why we're doing this

Ultralytics (publishers of YOLO26) asserts AGPL-3.0 on models trained with their framework. We do not want to:
- Relicense RailReader2 from MIT to AGPL
- Burden every web-app user with AGPL source-availability
- Block the planned commercial iPad app (see memory `project_mobile_commercial.md`)

The minimum-disruption fix is to retrain on a permissively-licensed architecture. YOLOX (Megvii, Apache-2.0) is the chosen architecture — it has tiny variants (~0.9M params), mature ONNX export, and the same YOLO-format dataset labels we already have.

Other options were evaluated and rejected:
- **Embrace AGPL**: closes mobile-app plan, deters contributors, irreversible.
- **Buy Ultralytics commercial license**: ~$500/mo+, not viable for free OSS.
- **Revert to PP-DocLayoutV3** (Apache-2.0, has reading order natively): 130 MB, ~1800 ms/page — kills the web-app target.
- **Separate-model distribution**: legally gray; doesn't help web-app at all.

YOLOX-nano is the recommendation. PP-PicoDet-XS or RT-DETR-r18 are acceptable alternatives if YOLOX training hits an unexpected wall.

---

## 2. State of the codebase

Branch lineage:

```
main (b3f5223)
 └── experiment/yolo26-layout (b04161f)  ← working YOLO26 + XY-cut, 261/261 Core tests, mean τ=0.944
      └── experiment/yolox-layout (this branch)  ← HANDOFF.md + your work goes here
```

The yolo26 branch already contains:
- `src/RailReader.Core/Services/LayoutAnalyzer.cs` — YOLO end-to-end inference, Ultralytics letterbox preprocessing, `[1, 300, 6]` output decode with pad-offset unprojection
- `src/RailReader.Core/Services/LayoutConstants.cs` — 16-class YOLO-DLA schema with documented runtime/dataset index-shift caveat
- `src/RailReader.Core/Services/XYCutSorter.cs` — port of XYCutPlusPlusSorter, calibrated for academic papers
- `experiments/order-validation/` — Python harness using PP-DocLayoutV3 as oracle
- `tests/RailReader.Core.Tests/XYCutSorterTests.cs` — 12 sorter tests including the ICASSP regression

Read `src/RailReader.Core/Services/LayoutConstants.cs` first — its top-of-file comment explains the class-index shift the previous instance discovered the hard way.

---

## 3. The critical gotchas — READ THESE before touching anything

### G1. The ONNX class-index shift

YOLO26's ONNX `names` metadata revealed that the export **dropped training IDs with too few examples** (id 4 = `t4` had zero instances; id 16 was also missing). So runtime output index does NOT equal dataset YAML index from index 4 upward:

```
runtime idx 4 → training id 5 → "paragraph"  (not t4)
runtime idx 5 → training id 6 → "author"
...
runtime idx 15 → training id 17 → unknown (5 stray instances)
```

If you train YOLOX from scratch with the *same* dataset, the same drop-out may or may not happen depending on your training config. **After training, inspect the ONNX `names` metadata** and verify against `LayoutConstants.LayoutClasses` (which currently encodes the YOLO26 runtime mapping). Adjust the C# table if YOLOX's class ordering differs.

To check: `python3 -c "import onnx; m=onnx.load('models/yolox_dla.onnx'); [print(p.key,':',p.value) for p in m.metadata_props if 'name' in p.key.lower()]"`

### G2. Coordinate system: screen Y-down

Everything in `XYCutSorter.cs` assumes Y increases downward (top of page = smaller Y). The Java reference (XYCutPlusPlusSorter) uses PDF convention (top of page = larger Y). Don't accidentally re-introduce the Java convention when reading the reference or comparing implementations.

### G3. `MinGapThreshold = 2.0pt` is calibrated for ICASSP

ICASSP/IEEE conference templates have column gaps as tight as 4.3pt. The Java reference's 5.0pt threshold misses them. **Do not raise this threshold without re-running `experiments/order-validation/compare.py`**. The current value is the result of empirical validation against PP-DocLayoutV3 over 200 random training-set pages.

### G4. Input-size constant

`LayoutConstants.InputSize = 640` is YOLO26-specific. If YOLOX is trained at a different image size, update this constant. **All three CLI commands** (`StructureCommand`, `AnnotationsCommand`, `VlmCommand`) reference `LayoutConstants.InputSize` for `RenderPagePixmap`. They used to hardcode `800` (PP-DocLayoutV3's input size) and crashed — the bug fix is committed but the lesson stands: never hardcode the size.

### G5. Tall-narrow margin-text detection

The XY-cut pre-mask catches IEEE/ICASSP rotated info-bar text via `H ≥ 0.7·maxH ∧ W ≤ 0.1·maxW`. YOLOX may or may not detect these strips depending on how well it learns from the training data. If it doesn't, the pre-mask has nothing to catch and the layout still works. If it does, the pre-mask handles it. **Don't loosen the thresholds** — false positives there would catch column body blocks.

### G6. Don't bundle pre-trained Ultralytics weights

When training YOLOX-nano, use **Megvii's** COCO-pretrained backbone (Apache-2.0 from their releases). Do NOT initialise from any Ultralytics weights — that would taint the resulting model with AGPL.

---

## 4. Training recipe (the user will do this)

### Dataset

`AIBox-IMU/DLA` YOLO-DLA dataset, 18000 images. On the original machine this was at `/home/stefan/Downloads/18000/` (Linux). The label format is standard YOLO: one `.txt` per image with `class_id x_centre y_centre w h` (normalised). 16 classes per `dataset/doc_data.yaml`.

### Architecture: YOLOX-nano

Use the official Megvii repo: https://github.com/Megvii-BaseDetection/YOLOX (Apache-2.0).

Config tweaks vs. defaults:

| Setting | Value | Why |
|---|---|---|
| `num_classes` | 16 | Match YOLO-DLA schema |
| `input_size` | (640, 640) | Matches existing C# preprocessing — keeps the integration trivial |
| `epochs` | 300 | YOLOX-nano on 18000 images: ~2 GPU-hours |
| `mosaic` | **disabled** | Documents have canonical orientation |
| `mixup` | **disabled** | Same |
| `hsv_jitter` | **disabled** | Documents have canonical colour |
| `flip_prob` | 0.0 | No mirror — text orientation matters |
| Pretrained weights | YOLOX-nano COCO (Apache-2.0) | NOT any Ultralytics weights |

### ONNX export — CRITICAL

Export with **end-to-end NMS** so output is `[1, max_det, 6] = [x1, y1, x2, y2, conf, cls]` — identical shape and column order to what `LayoutAnalyzer.cs` currently expects from YOLO26. Zero inference-decoder changes.

YOLOX's `tools/export_onnx.py`:

```bash
python tools/export_onnx.py \
    --output-name yolox_dla.onnx \
    -f exps/example/custom/yolox_nano_dla.py \
    -c YOLOX_outputs/yolox_nano_dla/best_ckpt.pth \
    --decode_in_inference \
    --batch-size 1
```

If your YOLOX version doesn't support end-to-end NMS export, you'll get `[1, num_anchors, 21]` (5 bbox coords + 16 class scores). Tell me when you get there — adapter is ~50 LOC of NMS in C# (we already have `LayoutAnalyzer.Nms`, just need to convert from anchor decoding first).

### Validation before handing it over

Run a sanity check on one image before shipping the model. The output tensor should have shape `[1, 300, 6]` (or `[1, max_det, 6]` for whatever `max_det` you configured) with `conf ∈ [0, 1]`, `cls ∈ [0, 15]`, and reasonable box coords.

Output: `models/yolox_dla.onnx`. Aim for ≤ 20 MB after FP16 or INT8 quantisation.

---

## 5. Integration steps (you do this once the model exists)

### Step 1: Drop the model

Place the trained model at `models/yolox_dla.onnx`. (The directory's `.gitignore` already excludes `*.onnx` from version control.)

### Step 2: Verify the ONNX I/O

```bash
python3 -c "
import onnx
m = onnx.load('models/yolox_dla.onnx')
print('inputs:', [(i.name, [d.dim_value for d in i.type.tensor_type.shape.dim]) for i in m.graph.input])
print('outputs:', [(o.name, [d.dim_value for d in o.type.tensor_type.shape.dim]) for o in m.graph.output])
print('names metadata:')
for p in m.metadata_props:
    if 'name' in p.key.lower() or 'class' in p.key.lower():
        print(f'  {p.key}: {p.value[:300]}')
"
```

Verify:
- Single input named `images` (or whatever YOLOX uses by default — may be `input`), shape `[1, 3, 640, 640]`
- Output shape compatible with the current decoder: `[1, max_det, 6]` ideally, else `[1, num_anchors, 21]`
- The `names` metadata matches `LayoutConstants.LayoutClasses` — see G1 above

### Step 3: Update file references

Three files reference the YOLO26 model filename. Update them to point at `yolox_dla.onnx`:

- `src/RailReader.Core/DocumentController.cs` — `FindModelPath()`: change the `filename` constant
- `src/RailReader2/Views/AboutDialog.axaml` — attribution string
- `README.md` — if it mentions YOLO26 anywhere

Also update `LayoutConstants.cs` top-of-file comment to mention YOLOX in addition to / instead of YOLO26.

### Step 4: Update the inference path (only if needed)

If YOLOX was exported with end-to-end NMS and output shape is `[1, max_det, 6]`:
- **Zero code changes** beyond the filename. The existing `ExtractDetections` in `LayoutAnalyzer.cs` works.
- Verify the input tensor name. If it's not `images`, change the `NamedOnnxValue.CreateFromTensor("images", ...)` line.

If YOLOX output shape is `[1, num_anchors, 21]`:
- The model emits raw predictions: per-anchor `[x, y, w, h, obj_conf, cls0, cls1, ..., cls15]` in 640-space.
- For each anchor, compute `final_conf = obj_conf × max(cls_scores)`, pick `cls_id = argmax(cls_scores)`.
- Apply class-agnostic NMS — `LayoutAnalyzer.Nms` already exists.
- Then unproject through letterbox pad offsets the same way the current code does.
- ~50 LOC in `ExtractDetections`. Keep the NMS pass after (already there for cross-class dedup).

### Step 5: Run the validation harness

```bash
cd experiments/order-validation
source .venv/bin/activate  # or recreate with: uv venv && uv pip install -e .
python compare.py --images <path-to-18000-images> --sample 200 --output /tmp/yolox-validation/
```

**Acceptance criteria** (compare against the YOLO26 baseline numbers below):

| Metric | YOLO26 baseline | YOLOX target |
|---|---|---|
| Mean Kendall τ | 0.944 | ≥ 0.93 |
| Exact permutation match | 82.4% | ≥ 78% |
| τ ≥ 0.90 | 86.0% | ≥ 80% |

If significantly worse, investigate — likely a class-mapping issue (G1) or a detection-quality regression (training under-converged). If significantly better, great — log it and move on.

Render the worst 15 failures (`--worst-n 15`) and visually inspect them. Common false-failure pattern: granularity mismatch where PP-DocLayoutV3 fragments paragraphs more than YOLOX does. Those are not real failures.

### Step 6: Update the GUI smoke test

Open a real PDF in the GUI:

```bash
dotnet run -c Release --project src/RailReader2 -- path/to/some/academic-paper.pdf
```

Press `Shift+D` to enable the debug overlay. Verify:
- Boxes appear and class labels make sense
- Rail mode activates above 3× zoom
- Reading order in rail mode goes left-column-then-right on two-column pages

A known-good test case: the ICASSP-template paper that drove the XY-cut calibration. If the user can't share that PDF, any IEEE/ICASSP conference paper with a rotated margin info-bar exercises the same code paths.

### Step 7: Run the Core test suite

```bash
dotnet test tests/RailReader.Core.Tests -c Release
```

Expected: **261/261 passing** (or 261+N if you added regression tests for any YOLOX-specific issues you encountered). All XYCutSorter tests must pass — they're detector-agnostic so any failure indicates an accidental regression in the sorter.

The Export tests (`tests/RailReader.Export.Tests`) have 15 known failures because `PageMarkdownBuilder` keys on PP-DocLayoutV3 class names. That's a separate piece of deferred work — out of scope for this branch.

### Step 8: Commit

Two commits suggested:

1. **The model swap itself.** `experiment/yolox-layout` → "Switch detector to YOLOX-nano (Apache-2.0)" — model filename, attribution updates, any C# decoder changes, and the validation results in the commit message body.
2. **Any class-table corrections** if you discovered shifts vs. YOLO26's runtime mapping.

Don't push, merge, or open a PR without the user's explicit go-ahead. (Per durable instruction: "Each action — push/PR/merge/tag — needs separate explicit authorisation.")

---

## 6. What you do NOT need to do

These are detector-agnostic and were validated on YOLO26. Don't reopen them unless something demonstrably broke:

- **XYCutSorter calibration** — β=0.7, tall-narrow detection, 2pt MinGapThreshold, narrow-element retry. All settled.
- **Class-table semantics** — the 16-class table in `LayoutConstants.cs` was verified by rendering training annotations. Only revisit if the YOLOX export's `names` field disagrees (G1).
- **Validation harness** — `experiments/order-validation/` works. Don't refactor it.
- **CLI commands** — already use `LayoutConstants.InputSize`. Don't reintroduce the hardcoded 800.
- **Test fixtures** — `TestFixtures.cs` and `RailNavTests.cs` use `ClassParagraph` / `ClassImage` / etc. These constants get re-pointed automatically if you update them in `LayoutConstants.cs`.

---

## 7. Files you'll touch (probably)

```
src/RailReader.Core/DocumentController.cs            FindModelPath filename
src/RailReader.Core/Services/LayoutAnalyzer.cs       Maybe NMS pass if non-e2e export
src/RailReader.Core/Services/LayoutConstants.cs      Maybe class table if names shifted
src/RailReader2/Views/AboutDialog.axaml              Attribution string
README.md                                            Any YOLO26 mentions
HANDOFF.md                                           Delete or mark "completed" after merge
```

## Files you should NOT touch

```
src/RailReader.Core/Services/XYCutSorter.cs          Detector-agnostic
experiments/order-validation/*.py                    Detector-agnostic
tests/RailReader.Core.Tests/XYCutSorterTests.cs      Regression tests, leave alone
tests/RailReader.Core.Tests/TestFixtures.cs          Uses ClassParagraph etc.
tests/RailReader.Core.Tests/RailNavTests.cs          Uses class constants
```

---

## 8. Useful context the user may not repeat

- **The user is a statistics lecturer.** Mathematically complex PDFs, multi-column technical papers. RailReader2 is for the broader academic community, not just the user — design decisions should accommodate humanities single-column, statistics/tables-heavy, ICASSP-tight 2-column, all of it. (Memory: `feedback_target_audience.md`.)
- **The user has poor vision but can read at normal magnification with strain.** High magnification is for sustained reading comfort, not necessity. Don't frame as an accessibility tool.
- **Never push, PR, merge, or tag without explicit authorisation per action.** (Memory: `feedback_pr_workflow.md`, `feedback_no_merge_without_approval.md`.)
- **Don't launch the GUI binary programmatically.** Ask the user to test. (Memory: `feedback_no_gui_spawning.md`.)
- **AOT compatibility constraint.** Core, Renderer.Skia, and Export are `IsAotCompatible`; CLI uses `PublishAot=true`. No reflection-based serialisation — all `System.Text.Json` goes through `RailReaderJsonContext`. Adding a new persisted type means adding a `[JsonSerializable(typeof(T))]` entry there. (See `CLAUDE.md` "AOT compatibility" section.)

---

## 9. Quick-reference: previous baseline numbers

From `experiment/yolo26-layout` at commit `b04161f`, validation run on 2026-05-11:

```
n = 200 random pages from /home/stefan/Downloads/18000/images/
mean Kendall τ      = 0.944
median Kendall τ    = 1.000
exact match (τ=1.0) = 82.4% (159/193)
τ ≥ 0.95            = 83.9%
τ ≥ 0.90            = 86.0%
sub-0.5 τ           = 3.1% (mostly degenerate matched<5 cases)
```

YOLOX-nano should hit similar numbers. Investigate if mean τ drops below 0.93 or exact-match below 78%.

---

## 10. If you get stuck

The previous instance recorded these as the hard problems already solved — if you find yourself re-litigating them, stop and re-read the relevant memory entry:

- "XY-cut puts blocks in y-order" → MinGapThreshold was 5pt, calibrated to 2pt for ICASSP. See G3.
- "Class names look wrong on the debug overlay" → ONNX class-shift. See G1.
- "CLI structure command crashes with IndexOutOfRange" → CLI hardcoded 800pt input. Already fixed; don't reintroduce.
- "How does Surya solve reading order?" → It doesn't have an algorithm — autoregressive transformer emits boxes in reading order. Out of scope for our size budget.
- "Should we train a tiny ordering model?" → Considered and rejected in favour of the validated XY-cut approach. Don't re-propose unless the user asks.

Good luck. Most of the work is done — keep the scope tight.
