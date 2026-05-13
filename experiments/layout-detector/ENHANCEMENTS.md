# Tiny-detector enhancements — things to try if quality is insufficient

Catalogue of architectural / training enhancements distilled from
**Qi et al. 2026, "YOLO-DLA: A YOLO-based unified framework for multi-scale
document layout analysis"** (Expert Systems With Applications 299, 129981).
Same paper that produced the AcadLayout dataset we train on.

This is a **to-be-tried-if-needed** list, not a backlog. Implement only when the
current baseline shows the specific weakness each item addresses.

---

## Trigger → enhancement map

| Symptom on baseline | Enhancement | Tier | Where |
|---|---|---|---|
| Low recall on minority classes (`t3`, `t`, `keyword`, `abstract`, `author`) | Class-weighted focal loss / scale-aware curriculum | A | §1 |
| Boxes wrong on column-/row-aligned content; mixes adjacent columns | RCM (Rectangular Self-Calibration Module) | A | §2 |
| NMS keeps wrong candidate / loses good box on dense pages | Retention head (learned NMS) | A/B | §7 |
| Missed small objects (footnotes, captions, tiny headings) | Multi-level head (add stride-8) | B | §3 |
| XY-cut quality bottoms out on complex layouts; want to retire it | Learned ordering head with pairwise BCE | B | §8 |
| Cross-scale fusion needed (only if multi-level head is added) | Mini-FPN with sum or concat | B | §5 |
| Full multi-scale neck like the paper's PRDM | PCE + DIF + MSFF | C — skip | §6 |
| All of the above + budget for big rewrite | KWConv backbone | C — skip | §4 |

---

## §1. Class-weighted focal loss / scale-aware curriculum — **Tier A**

**Problem.** YOLO-DLA dataset class distribution:
- `paragraph` (class 4): ~51% of labels
- `formula` (12), `other` (11), `note` (10): 6–12% each
- `t3`, `t`, `keyword`, `abstract`, `author`: <1% each

Without rebalancing, training overfits `paragraph` and ignores the long tail.

### Cheapest version: inverse-frequency weighting (~5 LOC)
In `layout_detector/loss.py:TinyYoloLoss.forward`, multiply the per-class focal
BCE by a constant class-weight tensor `w = (median_count / class_count) ** 0.5`
or similar. Pass weights at construction.

### Paper's version: scale-aware curriculum (~100 LOC)
1. For each GT object compute `C(e) = -log((w·h) / (W·H))` (normalised area).
2. K-means cluster all GTs into N stages (paper uses N=3 or N=4 → K=4 gave best
   results on AcadLayout, +0.2% mAP).
3. Stage 1 keeps only large objects (macro-scale: t1, paragraph, figure, table);
   stage 2 adds medium; stage N adds all.
4. During training, switch the dataset's `_load_labels` to mask GTs from later
   stages until the current stage budget allows them.
5. Per-stage loss weight: `λ_j = 2j / (N(N+1))` so later stages emphasise harder
   classes.

### When to choose
If after the baseline training the minority-class AP is much lower than
majority AP, start with the cheap version. Only escalate to full curriculum if
that doesn't close the gap.

---

## §2. RCM — Rectangular Self-Calibration Module — **Tier A**

**Problem.** Documents are uniquely axial — columns, rows, baselines, heading
hierarchy all line up. Standard square-kernel convolution has no inductive bias
for this; standard self-attention is too heavy.

**RCM** (from Ni et al. 2024, used in YOLO-DLA's PRDM-neck):

```
F_out = MLP(BN( ψ_{3x3}(x) ⊙ Sigmoid(y) )) + x       # eq. 6 in paper

where:
  y = ψ_{k×1}( ReLU( BN( ψ_{1×k}( H_P(x) ⊕ V_P(x) ) ) ) )   # eq. 5
  H_P, V_P = horizontal and vertical average pooling (broadcast back)
  ψ_{1×k} and ψ_{k×1} = large-kernel banded convolutions (rows then cols)
  ⊕ = broadcast add
  ⊙ = Hadamard product
  ψ_{3×3} = depthwise 3×3 conv
```

In plain prose: pool rows + pool cols → mix them with a banded conv → use that
as an attention map → multiply with a depthwise-conv of the input → MLP → add
residual.

### Where to slot in
One RCM block between backbone output and the head. After the lateral 1×1 conv
in `DecoupledHead.stem`, before `cls_trunk` / `reg_trunk`.

### Cost
~80 LOC. Adds ~50–100K params. Inference overhead modest.

### Why it's the highest-priority architectural add
The paper's most "document-specific" idea, and the only one that fits into a
single-feature-level model without dragging the rest of PRDM-neck along.

---

## §3. Multi-level head (stride-8 + stride-16) — **Tier B**

**Problem.** Single stride-16 grid at 480 input = 30×30 = 900 cells.
Small objects (footnotes, captions, sub-headings) may not get a positive cell
assigned at the centre-prior step.

### Approach
1. In `model.MobileNetBackbone`, expose features[3] (stride 8) **and**
   features[8] (stride 16). Keep features[3] separately as `c3`.
2. Add a second `DecoupledHead` for stride 8 with shared weights or duplicate.
3. Route each GT to the level whose stride matches its scale:
   - Box max-side ≤ 64px → stride-8 head only
   - Box max-side > 64px → stride-16 head only
   - (or use both with same target — simpler)
4. In the loss, iterate over both head outputs and sum.
5. In `decode.decode_predictions`, concat boxes from both levels before NMS.

### Cost
~120 LOC across model, loss, and decode. Adds ~50K params. ~2× head compute.

### When to use
If the baseline shows AP drop > 10 points on `footnote`, `keyword`, `abstract`
vs the dominant `paragraph` AP — small minority classes that are *also*
geometrically small.

---

## §4. KWConv — **Tier C, skip for our scale**

**What it is.** Kernel-Wise Convolution: a dynamic-weight convolution where
each Conv2D's kernel is computed at runtime as a weighted combination of basis
kernels drawn from a "cross-layer repository". Weights come from a
Contrast-Driven Attention Function with annealed temperature.

**Paper's gain.** +0.4–0.5% mAP at their 20M-param scale. FLOPs *drop*
(67.7G → 43.0G) because the dynamic kernels share a smaller bank than full
per-layer Conv2D would.

**Why we skip.**
- ~hundreds of LOC, including orthogonal kernel decomposition and
  attention-driven kernel weighting.
- At 0.79M params we are backbone-bottlenecked, not conv-design bottlenecked.
  Our MobileNetV3 backbone is pretrained and small — most architectural
  decisions are baked in.
- Decimal-point mAP gains aren't worth the implementation surface.
- ONNX export path for dynamic conv is messy.

**When to reconsider.** Only if we ever swap to a from-scratch 5M+ param
backbone where KWConv's kernel-reuse-saves-FLOPs trade actually helps.

---

## §5. Mini-FPN — **Tier B (gated on §3)**

If §3 (multi-level head) is implemented, the two feature levels need to be
fused. Simplest: lateral 1×1 convs to a common channel count, then top-down
addition (FPN-lite):

```
P4 = lateral_4(C4)                       # stride 16
P3 = lateral_3(C3) + upsample(P4)        # stride 8 + top-down
```

That's it. ~30 LOC. Don't add the full PRDM machinery.

---

## §6. Full PRDM-neck (PCE + DIF + MSFF) — **Tier C, skip**

The paper's "Polymorphic ROI-aware Dense Multi-scale" neck assembles features
from four backbone levels via:
- **PCE** — adaptive pooling all levels to a common resolution, concat, RCM.
- **DIF** — interpolation-aligned blending of low- and high-level features.
- **MSFF** — Hadamard-product fusion with sigmoid gating.

**Why we skip.** All three are *cross-scale fusion* modules. They need
multi-level inputs and lots of channels to amortise. At our size:
- §2 (RCM alone) captures the document-specific inductive bias.
- §5 (mini-FPN) handles cross-scale fusion adequately at our channel count.
- PCE/DIF/MSFF combined would add ~300K params and ~200 LOC for sub-1% mAP at
  our scale.

**When to reconsider.** Only if we scale up the model significantly (say
backbone → MobileNetV3-Large or EfficientNet-B0) AND find a measurable
cross-scale fusion weakness.

---

## Decision protocol

1. Train baseline to convergence (the current `runs/v1`).
2. Compute per-class AP on a held-out test split.
3. Identify weakness — minority classes, small objects, column-confusion, or
   "everything's fine".
4. Pick the matching enhancement from §1–§3 above. Try one at a time so we know
   what helped.
5. **Warm-start from the baseline checkpoint** (see "Transfer learning" below)
   rather than retraining from scratch.
6. Re-run `experiments/order-validation/compare.py` on the new model. The
   XY-cut downstream is detector-agnostic — quality goes up automatically if
   the detector improves.

Skip §4 and §6 unless we're at a very different model scale.

---

## §7. Retention head — learned NMS replacement — **Tier A/B**

**Source.** Liu et al. 2026, "Parser-Oriented Structural Refinement for a
Stable Layout Interface in Document Parsing" (arXiv 2604.02692). Their
strongest ablation: removing the retention loss drops F1 by 0.82 and worsens
reading-order Edit by 3.5× on OmniDocBench.

**Problem.** Our `decode.batched_nms` uses fixed IoU=0.5 to dedupe overlapping
detections. On dense pages — adjacent paragraphs, overlapping equation +
caption, columns with tight gutters — IoU NMS sometimes:
- Keeps the wrong candidate (lower-IoU box that suppresses the right answer).
- Loses valid boxes that happen to overlap with a stronger neighbour.

**Idea.** Add a **retention scalar** to the head output. At inference, threshold
on retention score instead of running IoU NMS. The head learns *which boxes
should survive* from data rather than from a hand-tuned threshold.

### Implementation (~30–40 LOC)

1. In `model.DecoupledHead`, add `self.ret_pred = nn.Conv2d(head_ch, 1, kernel_size=1)`
   (initialise bias with the same `-log((1-π)/π)` trick as cls/obj for π=0.5,
   not 0.01, since we want a balanced prior at start).
2. Model `forward` returns `cls`, `reg`, `obj`, `ret` — adds 1 channel.
3. In `loss.TinyYoloLoss`, add a retention BCE term. The label is **free**:
   `ret_target = pos_mask.float()` — exactly the same mask we already compute
   for cls/reg supervision.
4. In `decode.decode_predictions`, multiply `obj × cls_top × ret` for the final
   score, or use `ret` alone as the survival score.
5. `decode.batched_nms` either: (a) keep as a safety net at high IoU threshold,
   or (b) replace with score-threshold + top-K selection.

### Why this is potentially Tier-A

- **Implementation cost**: ~30 LOC, near-zero param cost (~128 weights).
- **No new labels**: positives come for free from the existing center-prior
  assignment. We get retention supervision *as a byproduct of training the rest*.
- **Their ablation says it's the most impactful single component** of their
  pipeline — bigger than ordering, bigger than difficulty weighting.

### Why Tier-A/B and not pure Tier-A

The paper's gains are largest where their *DETR detector* emits redundant
queries that need set-level reasoning. Our anchor-free, single-level head
emits cleaner output already — fewer near-duplicates. The relative gain may
be smaller for us. But the cost is so low (~30 LOC, no new labels) that it's
worth a try if the baseline shows any NMS-related failure.

### Trigger to implement

After the baseline run, look for these symptoms in the per-page debug:
- Multiple high-conf detections of the same content kept post-NMS
- Valid detections suppressed by overlapping-but-wrong neighbours
- Cases where lowering the NMS IoU helps recall but hurts duplicates, or vice
  versa

---

## §8. Learned ordering head — **Tier B**

**Source.** Same paper (Liu et al. 2026). They report Reading Order Edit of
**0.024** on OmniDocBench — beating PP-DocLayoutV3's 0.042 — i.e., better than
the oracle we currently validate against.

**Problem.** XY-cut is geometric and stateless. It struggles with:
- Floating figures that interrupt column flow
- Footnotes split across multiple regions
- Complex pages where reading order doesn't follow a strict top-to-bottom /
  left-to-right partition
- Anything where reading order encodes semantic info not visible from geometry

If validation reveals XY-cut quality is the bottleneck (e.g., many pages with
τ < 0.9 that aren't degenerate cases), a learned ordering head can replace it.

**Idea.** Add **per-detection ordering scalar** `ô`. At inference, sort retained
detections by `ô` ascending = reading order. Train with pairwise BCE over GT
order pairs: `BCE(σ(ô_i − ô_j), 1[i precedes j])`.

### Implementation (~80–120 LOC)

1. Add `self.ord_pred = nn.Conv2d(head_ch, 1, kernel_size=1)` to
   `DecoupledHead`. Output channel: `ord ∈ (B, 1, H, W)`.
2. Pairwise ordering loss inside `TinyYoloLoss`:
   - For each image, gather predicted `ô_i` at every positive cell and the
     GT order index of the matched GT.
   - Form all pairs of positive cells that match *different* GTs (skip
     same-GT pairs).
   - BCE on whether predicted score difference matches GT order.
3. **Difficulty-aware pair weighting** (also from the paper, +2.4× ordering
   improvement in their ablation):
   ```
   w_ij = 1 + γ log(1 + n_mid_ij)
   ```
   where `n_mid_ij` is the number of *other GT centres* inside the axis-aligned
   bounding rectangle spanned by the centres of GT i and GT j. γ ≈ 1.0 per paper.

   **Optional refinements from FocalOrder (Liu et al. 2026, arXiv 2601.07483):**
   - **EMA-tracked spatial difficulty**: in addition to the geometric
     `n_mid_ij`, maintain a running per-spatial-bin error tensor (e.g. 3×3
     grid over the page). Update via EMA each batch:
     `L_bin = γ·L_bin + (1-γ)·L_batch`. Weight pairs by the bin difficulty
     they fall into. Captures *where the model actually struggles*, not just
     where geometry says it should. ~30 LOC.
   - **Adaptive hinge margin instead of BCE**:
     `loss_ij = max(0, S(j) - S(i) + m_ij)` with `m_ij = α·max(w_i, w_j)`.
     Hard pairs need bigger score separation. ~10 LOC change to the loss.

   The FocalOrder paper's main contribution targets sequence models
   (LayoutReader, PaddleOCR-VL); it doesn't apply to our non-sequence setup
   wholesale, but these two pairwise-loss refinements transfer cleanly.
4. At inference (`decode`), sort retained detections by `ô` and discard the
   XY-cut runtime call.

### Catch: needs reading-order labels at training time

The YOLO-DLA dataset has bounding boxes only — no ordering. To supervise the
ordering head we need to **teacher-label with PP-DocLayoutV3** (Apache-2.0):

1. Run PP-DocLayoutV3 over all 18000 training images (one-off, ~24 GPU-hours
   based on the rate we saw in `compare.py`).
2. For each image, match teacher detections to YOLO ground-truth boxes by
   IoU > 0.5 (greedy or Hungarian).
3. Save matched GT-index → teacher-order-index mapping per image, alongside
   the YOLO label file.
4. The dataset reader returns labels as `(class, cx, cy, w, h, order)` instead
   of `(class, cx, cy, w, h)`.

Infrastructure already exists in `experiments/order-validation/teacher.py` —
we'd run that over the training set rather than a 200-page sample.

### Why Tier-B not Tier-A

- More elaborate than retention head: pairwise loss bookkeeping, vectorisation
  for batched pair sampling, difficulty weighting.
- **Requires the 24-GPU-hour teacher-labelling pass** on the training set.
- We don't *know* it'll beat XY-cut at our 0.79M-param scale. The paper uses
  61.57M params. Ordering with only ~100 params (1 output channel) may not
  learn the right structure.
- Eliminates XY-cut as a runtime dependency — clean simplification — but only
  worth it if XY-cut is empirically broken.

### Trigger to implement

After baseline + retention head (if applied), look at validation:
- Mean Kendall τ < 0.93 on the held-out test set (currently 0.944).
- Many real failures (matched ≥ 5, τ < 0.9) on the worst-15 inspection.
- Specific layout patterns (textbooks with sidebars, journals with floating
  figures) where XY-cut consistently misorders.

If XY-cut keeps tracking PP-DocLayoutV3's ordering closely on real failures,
the learned head is unlikely to do much better — defer.

---

## Transfer learning from the baseline

All Tier-A and Tier-B enhancements can warm-start from `runs/v1/best.pt`
instead of training from scratch. This saves ~half the wall time per iteration.

| Enhancement | What loads from v1 | What initialises fresh |
|---|---|---|
| §1 weighted loss / curriculum | **everything** — architecture unchanged | nothing |
| §2 RCM block | backbone + head stem + cls/reg trunks + prediction heads | RCM block only |
| §3 multi-level head | backbone + stride-16 head | stride-8 projection + (optionally copy stride-16 trunk weights as init) |
| §5 mini-FPN | backbone + stride-16 head | lateral 1×1 convs |
| §7 retention head | backbone + head | retention pred conv (1×1) only |
| §8 ordering head | backbone + head | ordering pred conv (1×1) only |

### Implementation pattern

```python
ckpt = torch.load("runs/v1/best.pt", map_location=device)
model_state = model.state_dict()
loaded = 0
for k, v in ckpt["model"].items():
    if k in model_state and v.shape == model_state[k].shape:
        model_state[k] = v
        loaded += 1
model.load_state_dict(model_state)
print(f"warm-start: loaded {loaded}/{len(model_state)} params")
# Fresh optimizer below — do NOT load ckpt["optimizer"]
```

### Hyper-param adjustments for warm-start runs

- **Lower base LR**: 3e-4 instead of 1e-3 (~0.3× the original). Pretrained
  parts are near a local minimum; large updates hurt.
- **Shorter warmup**: 1 epoch instead of 3.
- **Fewer epochs**: 10–15 instead of 30. Warm-started runs converge faster.
- **Optional parameter-group LR**: new parts (RCM, new head) at full LR,
  pretrained parts at 0.1× LR. Two extra `param_group` entries in the
  optimizer constructor.
- **Fresh optimizer state**: don't load `ckpt["optimizer"]` — buffers are
  stale or shape-mismatched.

### Suggested CLI addition to `train.py`

A `--warmstart <path>` flag that:
- Partial-loads model weights (logs loaded vs initialised counts)
- Skips optimizer state load
- Defaults to `--lr 3e-4 --warmup-epochs 1 --epochs 15`
- Otherwise identical to a normal training run

Implementation: ~30 LOC. Not yet built — add when we first hit an enhancement.
