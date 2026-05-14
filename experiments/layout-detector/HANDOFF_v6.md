# HANDOFF — TinyLayoutYOLO v6 (data-distribution pivot)

**Branch**: `experiment/yolox-layout`
**Last work**: 2026-05-14 (paused mid-data-collection at user's request)
**Resume from**: this file. Read top-to-bottom; pick up at "Resume here".

---

## TL;DR — what we decided

After v5 (regression) and v5b (marginal RCM-only gain), per-class AP +
visual diagnostic showed the user-visible failures (dense bibliography
pages, multi-column financial tables) are **out-of-distribution pages**,
not broken classes.

User chose: **DocLayNet filtered subset, ~2000 pages, retrain as v6**.

We had launched the filtered DocLayNet sampler. User cancelled before
any pages were saved. The `sample_doclaynet_filtered.py` script and
filter logic are tested and working — just relaunch.

---

## State of the world (verified 2026-05-14)

### Model checkpoints (all in `experiments/layout-detector/runs/`)

| Run | best_val | best_reg | mAP@0.5 | mIoU_TP | Status |
|---|---|---|---|---|---|
| v4 | 2.352 | 0.0906 | **0.662** | 0.920 | **Production** (shipped at `models/tiny_layout.onnx` actually still v2; v4 not yet integrated) |
| v5 | 2.644 | 0.130 | not eval'd | not eval'd | Regression. Don't ship. |
| v5b | 2.322 | 0.087 | 0.655 | **0.923** | RCM-only. Slightly tighter boxes, slightly lower mAP. Marginal. |

ONNX exports present for v4, v5b. `eval.json` per-class AP present
for both. **The currently-shipped `models/tiny_layout.onnx` is still v2**
(SHA matches `runs/v2/tiny_layout.onnx`) — production integration of
v4 was never done.

### Disk state

- `/home/stefan/Downloads/v4_corpus/{images,labels}/` — 23 237 pairs,
  runtime schema. **Source of truth for training.**
- `/home/stefan/Downloads/dln_focus/` — **empty directory** (cancelled
  before any saves).
- `runs/v5b/best.pt` — 19 MB, the natural warm-start for v6.

### Uncommitted code changes on this branch

Modified (small, focused changes):
- `experiments/layout-detector/train.py` — added `--centre-radius` and
  `--top-k-positives` CLI flags (defaults preserve v5 behaviour). Lets
  v6 reproduce v4-style assignment without code edits.
- `experiments/layout-detector/export_onnx.py` — auto-detect RCM
  presence from state_dict (so it can export both v4 weights and v5b
  weights from the same script).
- `experiments/layout-detector/eval_ap.py` — auto-detect RCM + new
  `--class-remap` flag (`auto|yolo-dla|none`) so it works on the
  merged runtime-schema corpus.

New files:
- `experiments/data-prep/sample_doclaynet_filtered.py` — filtered
  DocLayNet sampler. Smoke-tested at n=20, hit-rate ~13%, distribution
  85% table / 10% list / 5% formula.
- `experiments/order-validation/render_v4_vs_v5b.py` — side-by-side
  visual diagnostic with `--conf-threshold` flag.

**Before launching v6**: commit these. They're stable.
Suggested commit message: `tooling: v5 design + v5b ablation + data-pivot helpers`
(then: do NOT PR; push to origin per standing rule).

---

## Diagnosis recap (so future-you understands the WHY)

### Why v5 failed
- v5 design = RCM + multi-pos top-k=3 + radius widening 0.5 → 1.5
- Ablation (v5b = RCM-only at radius 0.5, top-k=1) recovered v4-level
  box tightness and slightly beat it
- Cause: radius widening (needed for top-k=3 to engage) directly
  violated v2/v4's tight-assignment principle that gives box-edge
  precision. Multi-positive at radius=1.5 pulls boxes loose.
- **Verdict: never re-introduce radius widening or multi-positive
  without explicit ablation.** Keep `centre_radius=0.5`, `top_k=1`.

### Why the visible failures don't go away with v5b
- mAP eval on val split shows v5b has NO broken class (reference
  AP=0.62, table AP=0.77 — both reasonable)
- Visual: failure pages (`danam_p5_references`, `doclaynet_000500`)
  detect very few boxes at production conf=0.25
- Low-conf render (conf=0.05) on same pages: **2-3× more detections
  appear**, including correct reference items at 25-35% confidence
- **Two concurrent root causes**:
  1. **Calibration**: model proposes correct regions but low confidence
     on hard pages. Soft-obj IoU target (paired back from original v5
     design) was specifically meant to fix this; defer to v7 if v6
     doesn't close the gap.
  2. **Distribution**: failure pages are out-of-distribution — pages
     where reference/table/formula DOMINATE the layout are under-
     represented in the training corpus, even though aggregate label
     counts are decent. **v6 addresses this.**

---

## Resume here — concrete next steps

### Step 1: Commit the pending code changes (5 min)

```bash
cd /home/stefan/railreader2
git status --short experiments/
# Should show: M train.py, M export_onnx.py, M eval_ap.py,
# ?? sample_doclaynet_filtered.py, ?? render_v4_vs_v5b.py
git add experiments/layout-detector/train.py \
        experiments/layout-detector/export_onnx.py \
        experiments/layout-detector/eval_ap.py \
        experiments/data-prep/sample_doclaynet_filtered.py \
        experiments/order-validation/render_v4_vs_v5b.py
git commit -m "tooling: v5/v5b ablation + data-pivot helpers"
git push origin experiment/yolox-layout    # NO PR
```

Also worth committing (separately): `runs/v5b/eval.json`, `runs/v4/eval.json`,
`HANDOFF_v6.md` (this file). The `.gitignore` excludes `runs/*` so these
need explicit `git add -f` if you want them tracked.

### Step 2: Run the filtered DocLayNet sampler (~30 min, mostly HF streaming)

```bash
cd /home/stefan/railreader2/experiments/data-prep
/home/stefan/railreader2/experiments/layout-detector/.venv/bin/python \
  sample_doclaynet_filtered.py \
    --output /home/stefan/Downloads/dln_focus \
    --n 2000 --scan-limit 60000
```

Expected output:
- `/home/stefan/Downloads/dln_focus/dln_focus_000000.jpg` ... `_001999.jpg`
- `/home/stefan/Downloads/dln_focus/manifest.json` (per-image reason + genre)
- Console: `by reason: table=~1700  formula=~100  list=~200`

**If list count is < 100**, the bibliography failure mode won't get
enough training signal. Mitigation: rerun with looser list filter:
`--list-count-thresh 5 --list-area-thresh 0.30` — but use a different
`--output` and `--prefix` to avoid collision.

### Step 3: Teacher-label the new images (~30 min on GPU)

```bash
cd /home/stefan/railreader2/experiments/data-prep
/home/stefan/railreader2/experiments/layout-detector/.venv/bin/python \
  teacher_label.py \
    --images /home/stefan/Downloads/dln_focus \
    --output /home/stefan/Downloads/dln_focus_labels
```

Requires `models/PP-DocLayoutV3.onnx` (already present per `models/`
dir). Produces YOLO-format `.txt` labels in runtime schema. Will skip
images with zero retained labels — expect 1900-1950 labels for 2000
images.

### Step 4: Build v6 corpus (1 min, just file ops)

```bash
mkdir -p /home/stefan/Downloads/v6_corpus/{images,labels}
# Use ln -s to save ~5 GB of disk (file dir is read-only during training)
cd /home/stefan/Downloads/v6_corpus/images
ln -s /home/stefan/Downloads/v4_corpus/images/* .
ln -s /home/stefan/Downloads/dln_focus/*.jpg .
cd /home/stefan/Downloads/v6_corpus/labels
ln -s /home/stefan/Downloads/v4_corpus/labels/* .
ln -s /home/stefan/Downloads/dln_focus_labels/*.txt .
# Verify counts
ls /home/stefan/Downloads/v6_corpus/images | wc -l   # expect ~25,200
ls /home/stefan/Downloads/v6_corpus/labels | wc -l   # expect ~25,150
```

**Watch out**: symlinks don't work if the loader resolves real paths
weirdly. If `train.py` fails with "image not found", switch to
`cp` instead of `ln -s`.

### Step 5: Train v6 (~5h)

```bash
cd /home/stefan/railreader2/experiments/layout-detector
.venv/bin/python train.py \
    --images /home/stefan/Downloads/v6_corpus/images \
    --labels /home/stefan/Downloads/v6_corpus/labels \
    --output runs/v6 \
    --warmstart runs/v5b/best.pt \
    --epochs 20 \
    --batch-size 12 \
    --workers 8 \
    --lr 1.5e-4 \
    --warmup-epochs 1 \
    --input-size 480 \
    --centre-radius 0.5 \
    --top-k-positives 1 \
    2>&1 | tee runs/v6/train.log
```

**Why warm-start from v5b not v4**: v5b has RCM; v4 doesn't. If we
warm-start v6 from v4, the RCM blocks initialise fresh (38 keys) and
we waste epochs re-learning them. Starting from v5b inherits everything
including trained RCM weights → `direct match: 288  fresh: 0  skipped: 0`.
**The `--centre-radius 0.5 --top-k-positives 1` flags are critical** —
without them, v6 uses the v5 defaults (1.5 / 3) which we proved
regress box tightness.

### Step 6: Evaluate v6 (~10 min)

```bash
cd /home/stefan/railreader2/experiments/layout-detector
# Export ONNX
.venv/bin/python export_onnx.py \
    --checkpoint runs/v6/best.pt \
    --output runs/v6/tiny_layout.onnx

# Per-class AP on val split (uses same seed=42, val_fraction=0.05)
.venv/bin/python eval_ap.py \
    --checkpoint runs/v6/best.pt \
    --images /home/stefan/Downloads/v6_corpus/images \
    --labels /home/stefan/Downloads/v6_corpus/labels \
    --output runs/v6/eval.json
```

Compare per-class AP to `runs/v5b/eval.json` and `runs/v4/eval.json`.

### Step 7: Visual diagnostic on the problem pages

```bash
cd /home/stefan/railreader2/experiments/order-validation
# Edit render_v4_vs_v5b.py to compare v5b-vs-v6 (or write render_v5b_vs_v6.py)
# Same 8 test pages: DANAM p3/p4/p5, STAT321 p1/p4/p6, doclaynet 000100/000500

# Run at production threshold
/home/stefan/railreader2/experiments/layout-detector/.venv/bin/python \
  render_v5b_vs_v6.py --output /tmp/v5b_vs_v6

# Also at low threshold to see proposal density
/home/stefan/railreader2/experiments/layout-detector/.venv/bin/python \
  render_v5b_vs_v6.py --output /tmp/v5b_vs_v6_conf005 --conf-threshold 0.05
```

**Acceptance criteria for "ship v6"**:
- `doclaynet_000500` (multi-column financial table): detect count
  improves from 1 → at least 4 at production threshold
- `danam_p5_references`: detect count improves from 3 → at least 8
- Per-class AP on `reference` and `table` improves vs v5b
- mAP doesn't regress more than -0.005 vs v5b
- mIoU_TP stays ≥ 0.92

If v6 hits acceptance: integrate into RailReader2 (copy ONNX to
`models/tiny_layout.onnx`, commit, push). Don't PR without user
testing per standing rule.

If v6 doesn't hit acceptance: investigate v7 with **soft-obj IoU
target** for confidence calibration (the change from original v5
design we paired back).

---

## Open decisions deferred to future-you

1. **Production integration of v4 → which version actually ships?**
   `models/tiny_layout.onnx` is still v2 per SHA. Need to: pick the
   target version (v4 vs v5b vs v6), `cp` it, ensure
   `LayoutConstants.InputSize=480`, rebuild RailReader2, user-test,
   commit. **Defer until v6 results decide which to ship.**

2. **List filter too strict?** Smoke test showed only 10% list-
   reason hits. If v6 doesn't fix the bibliography failure
   (`danam_p5`), do a follow-up filtered sample with looser thresholds
   AND/OR add a "dense small-text-column" detector to the filter.

3. **Soft-obj IoU target (v7)?** Defer until v6 results indicate
   whether calibration is still a bottleneck after distribution fix.

4. **Cleanup v5/`runs/v5/`?** v5 was a regression — checkpoints are
   ~38 MB total. Keep for ablation reference; delete only when
   confidence is high we won't need them.
