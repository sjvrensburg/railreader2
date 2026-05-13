# v5 — launch instructions

Self-contained guide to launching v5. v5 adds **two** focused enhancements
on top of v4 (the original design had four; the schedule and soft-obj
target were dropped to keep attribution clean):

  1. **RCM** (Rectangular Self-Calibration Module, depthwise-axial variant).
     Architectural — adds row/column axial structure capture between FPN
     and each detection head. ~20K params per block, two blocks total.
  2. **Multi-positive top-k=3 assignment**. Loss-level — for each GT,
     the top-3 closest eligible cells are all marked positive (instead of
     just the single nearest at v4). Densifies positive supervision so the
     objectness head sees more positive examples per GT, improving
     confidence calibration on rare/large classes.

**Important caveat for top-k=3 to actually engage**: v4's
`centre_radius=0.5` only admits ~1 cell per GT, so top-k=3 silently
reduces to top-1 — the multi-positive change becomes inert. v5 widens
`centre_radius` to `1.5` so 4–7 cells are eligible per GT and top-k=3
picks 3 of them. This radius widening is a side-effect of the design,
not a third independent change.

The code is already implemented and smoke-tested; this file just tells
you how to fire it.

---

## TL;DR launch command

```bash
cd /home/stefan/railreader2/experiments/layout-detector
.venv/bin/python train.py \
    --images /home/stefan/Downloads/v4_corpus/images \
    --labels /home/stefan/Downloads/v4_corpus/labels \
    --output runs/v5 \
    --warmstart runs/v4/best.pt \
    --epochs 20 \
    --batch-size 12 \
    --workers 8 \
    --lr 1.5e-4 \
    --warmup-epochs 1 \
    --input-size 480
```

Expected wall: **~4 hours** on the A2000 (20 epochs × ~12 min, 23 237 train
images, batch 12). Cosine LR with 1-epoch warmup, peak 1.5e-4, anneal to
~1e-6 by epoch 20.

---

## Prerequisites — verify these before launching

```bash
# v4 checkpoint exists
ls -la runs/v4/best.pt
# Expected: ~19 MB
# If missing: v4 didn't complete; rerun v4 first.

# Merged corpus exists (23,237 image/label pairs)
ls /home/stefan/Downloads/v4_corpus/images | wc -l
ls /home/stefan/Downloads/v4_corpus/labels | wc -l
# Both should print 23237. If missing, re-run the merge command from V4 work.

# venv is functional
.venv/bin/python -c "import torch; print(torch.__version__, torch.cuda.is_available())"
# Expect: 2.5.1+cu121 True
```

If everything checks out, launch the command at the top.

---

## Warm-start expectation

When v5 starts, you should see in its log:

```
warm-starting from checkpoint: runs/v4/best.pt
  direct match: 250  v1-head→p3: 0  v1-head→p4: 0  fresh (random init): 38  skipped (no fit): 0
```

- **250 direct**: backbone + FPN + both heads transfer cleanly from v4
- **38 fresh**: the two RCM blocks initialise randomly (~19 state-dict
  entries per block × 2 blocks)
- **0 skipped**: nothing mismatched

If you see anything else (especially if "direct" < 250), STOP and diagnose
— architecture mismatch between v4 and v5.

---

## First-batch sanity (epoch 1 batch ~10)

- `cls` ≈ 0.01–0.04 (warm-started head is already calibrated)
- `obj` ≈ 0.3–1.0 (will rise initially as multi-positive assignment kicks
  in — more positive cells = larger obj loss sum per positive in the early
  phase, then settles as the head adapts)
- `reg` ≈ 0.08–0.15 (warm-started boxes are already tight; RCM
  modulation may briefly perturb this)
- `loss` (total) ≈ 2.5–5.0
- Throughput ≈ 2.5–3 it/s on the A2000

If `reg` > 0.3 or `obj` > 5 after a few hundred batches, **something
broke** — likely a label-schema mismatch or warm-start failure.

---

## Mid-training watch points

- **Epochs 1–3**: multi-positive supervision dominates the early signal.
  Expect obj loss to spike up vs v4 baseline, then come back down as the
  head learns to fire on multiple cells per GT.
- **Epochs 4–10**: RCM blocks finish adapting. reg loss should fall back
  to v4 levels or below.
- **Epochs 11–20**: cosine LR is annealing. Validation should creep down;
  reg should converge.

Best epoch is typically 15–18 for our setup.

---

## After training — deployment pipeline

Same as v2 / v4. Sequence:

```bash
# 1. Export to ONNX
.venv/bin/python export_onnx.py \
    --checkpoint runs/v5/best.pt \
    --output runs/v5/tiny_layout.onnx

# 2. Per-class AP on 1161-image val split (~5 min GPU)
.venv/bin/python eval_ap.py \
    --checkpoint runs/v5/best.pt \
    --output runs/v5/eval.json

# 3. Visual side-by-side comparisons vs v4 on the diverse test set
cp runs/v5/tiny_layout.onnx /tmp/v5_partial.onnx
cd ../order-validation
# Adapt render_v2_vs_v4.py to compare v4 vs v5.
```

Acceptance criteria for "ship v5":

- mIoU on TPs ≥ 0.92 (matches v4 box-tightness)
- Per-class recall on paragraph/table/note/formula meaningfully higher
  than v4 — especially on `doclaynet_000500` (financial-table page,
  where v4 missed everything below conf 0.05) and `doclaynet_000100`
  (missing-paragraphs page)
- No regression on user's lecture-note documents (STAT321 set)

---

## What was added (for the curious reader)

### `layout_detector/model.py`
- New `RCM` class — depthwise-axial variant of the Rectangular Self-Calibration
  Module from Ni et al. 2024. ~20K params per block. Captures the row/column
  axial structure documents have.
- `TinyLayoutYOLO.__init__` accepts `use_rcm` flag (default True). Adds
  `rcm_p3` and `rcm_p4` blocks between the FPN and each head.
- Forward routes p3/p4 through RCM before going to the head.

### `layout_detector/loss.py`
- `CENTRE_RADIUS` constant: 0.5 → 1.5 (necessary for top-k=3 to engage).
- `TinyYoloLoss.__init__` accepts `top_k_positives` (default 3).
- `_level_loss` does **top-k multi-positive assignment**: for each GT,
  finds top-k closest eligible cells (instead of just the single nearest).
  Conflicts resolved by smallest-area GT (same as v2/v4).
- Objectness target stays binary (`1.0` for positives, `0.0` for
  negatives) — soft-IoU target was dropped from v5 scope.

### `layout_detector/train.py`
- No changes vs v4. (`current_epoch` plumbing was added then removed
  along with the centre-radius schedule.)

### `layout_detector/dataset.py`
- `YoloDataset.__init__` default `class_remap` changed: **None means
  no remap** (labels assumed runtime-schema). Pass `YOLO_DLA_CLASS_REMAP`
  explicitly when loading raw YOLO-DLA dataset-schema labels.
- `compute_class_weights` same change.

This is the schema-unification fix that was needed to make the
merged-corpus training work correctly. **If you ever rebuild a corpus
that mixes YOLO-DLA-format labels with runtime-schema labels, you must
either pre-convert the YOLO-DLA labels offline or pass per-source remaps
— there is no auto-detect.**

---

## Rollback path

If v5 turns out to be a regression vs v4 after eval:

```bash
# v4 ONNX is preserved at runs/v4 — already integrated into RailReader2.
# To roll back the C# side to v4's tiny_layout.onnx:
cp runs/v4/tiny_layout.onnx ../../models/tiny_layout.onnx
# Then rebuild RailReader2 — no source changes needed.
```

Training-side rollback isn't needed; just don't export v5 to
`models/tiny_layout.onnx`.

---

## Why these two changes and not the original four?

The original v5 design layered four changes (RCM, top-k=3, soft-obj
target, centre-radius schedule) on top of v4. Stacking that many at
once would make attribution impossible — if v5 underperforms v4, you
can't tell which change is the culprit. Paring back to two focused
changes (RCM for architecture, top-k=3 for assignment) keeps the
ablation cheap if needed.

Soft-obj target (IoU(pred, GT) instead of 1.0) and the centre-radius
schedule (0.5 → 1.5 ramped over epochs 3–10) were deferred. They can
be re-added as v6 or v7 if v5 underperforms expectations and we want
to push obj calibration further.
