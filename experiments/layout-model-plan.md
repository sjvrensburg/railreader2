# Layout Model Retrain Plan: YOLO26n + XY-cut via PP-DocLayoutV3 Distillation

**Status**: planning
**Last updated**: 2026-04-15
**Author**: sjvrensburg + Claude Code

---

## 1. Motivation

### What prompted this

RailReader2 currently ships Docling Heron-INT8 (~66 MB). Historical context: we previously shipped PP-DocLayoutV3 (RT-DETR-v2–based). This plan documents exploration of alternative models.

1. **Quantization failures.** Attempts to INT8-quantize the Heron (RT-DETR-v2) variant of DocLayoutV3 hit both numerical instability (`add_2355` producing `+inf` on all calibration pages, requiring sentinel patching) and memory blowup on the ORT Percentile calibrator beyond 8 pages. MinMax+EMA quantized cleanly but produced a broken model: **3.4% detection recall at IoU 0.5**, because DETR-style attention + LayerNorm-heavy decoders squash query scores below the 0.4 threshold under PTQ. The architecture fundamentally doesn't PTQ well on CPU EPs.
2. **No meaningful CPU speedup even with successful INT8.** The MinMax+EMA model gave only a 1.13× speedup (1794 → 1585 ms/page) despite a 27% size reduction. ORT CPU EP is falling back to fp32 on large chunks of the DETR graph. The theoretical ceiling for this architecture on CPU is modest.

Replacing with a student model that **quantizes cleanly by construction** (conv backbone, NMS-free head, no layer-norm heavy decoders) is the path forward.

### Why now

- **YOLO26** (Ultralytics, Jan 2026) shipped with NMS-free end-to-end detection and DFL removal. Both changes remove exactly the ops that tend to dodge INT8 kernels. CPU-n variant clocks 38.9 ms/page on COCO 640×640; at 800×800 on documents we estimate ~60–100 ms/page pre-quantization, ~30–50 ms post-INT8.
- **Layout models under TensorRT** are cheap enough to run at scale as teachers on custom corpora. This flips the economics: auto-labelling is no longer the bottleneck, filter quality is.
- **Public layout datasets** (DocLayNet, DocBank, M6Doc) give us a manually-labelled or high-quality weakly-labelled anchor that doesn't depend on teacher quality.

### What success looks like

- **Student model**: YOLO26n (or -s) INT8-quantized to ≤20 MB, running at ≤100 ms/page on the same CPU that takes ~1800 ms/page today.
- **Accuracy**: ≥95% detection recall and ≥0.85 mean IoU on a hand-labelled golden set drawn from the user corpus, vs PP-DocLayoutV3 fp32 as reference.
- **Reading order**: Kendall τ ≥ 0.95 via XY-cut heuristic on the same golden set, measured against DocLayoutV3's Global Pointer output.
- **Integration**: drop-in replacement for `LayoutAnalyzer` with the same 8-class output contract. Existing rail-mode behaviour unchanged.

---

## 2. Architecture

### Student detector: YOLO26n

Rationale over alternatives:

- **YOLO26n vs YOLO26s**: n is 2.4M params / 40.9 mAP, s is 9.5M / 48.6 mAP. Document layout is a narrower task than COCO — 8 classes, mostly large/medium objects. The 4× param difference likely doesn't translate to 7.7 mAP on our task. Start with -n; escalate to -s only if the held-out eval demands it.
- **YOLO26 vs YOLO11**: YOLO26's NMS-free end-to-end architecture is meaningfully better for quantization. YOLO11 keeps NMS + DFL which either run in fp32 (breaking the latency promise) or need custom INT8 kernels (breaking portability). YOLO26 is a clean win.
- **YOLO26 vs RT-DETR / DETR variants**: already ruled out by the Heron failure.
- **YOLO26 vs custom lightweight architecture**: Ultralytics' pipeline (train → validate → export to ONNX/OpenVINO/CoreML/TFLite with INT8 PTQ) saves weeks of engineering. Not worth rolling our own.

### Reading-order subsystem: XY-cut

Separate from detection. Given N detected boxes on a page, produce an ordering.

Chosen approach: **recursive XY-cut** (Nagy & Seth, 1984), which recursively partitions the page by the widest whitespace gap in each direction until each region contains one box, then reads top-to-bottom and left-to-right within each split.

Alternatives considered:

- **Pure y-sort** (sort by box top): fails on multi-column, which is most academic/textbook content. Rejected.
- **Column clustering** (k-means or DBSCAN on x-centres, then y-sort within cluster): works on 2-column papers, fails on 3+ columns, mixed-width layouts, and pages with full-width figures that break column structure. Reasonable fallback but worse than XY-cut for complex pages.
- **LayoutReader** (Microsoft, ACL 2021, BERT-based learnable ordering): best accuracy on complex layouts but adds a second neural model to quantize, deploy, and maintain. Overkill until we measure that XY-cut actually fails on the user corpus.
- **Learned GNN over box features**: similar concerns to LayoutReader, plus no pre-trained weights exist so we'd need to train from scratch. Defer.
- **PP-DocLayoutV3's Global Pointer output**: what we're trying to replace. Not an option.

XY-cut is good enough for the `opendataloader-pdf` project, which targets similar content, so we start there. The upgrade path (column clustering → LayoutReader) is left open, gated on measured failure on the golden set.

### Class schema (narrowed from DocLayoutV3's 26 to 8)

Target schema:

| # | Class | Purpose in RailReader | Source in DocLayoutV3 |
|---|---|---|---|
| 0 | `text` | Rail-mode navigable, rail navigation core | `text` |
| 1 | `paragraph_title` | Section heading, navigable, breadcrumb source | `paragraph_title` |
| 2 | `doc_title` | Top-level heading, breadcrumb root | `doc_title` |
| 3 | `equation` | Rail-mode navigable, VLM Copy-as-LaTeX target | `equation` |
| 4 | `table` | Peek index, VLM transcription target | `table` |
| 5 | `figure` | Peek index, VLM description target | `figure` / `image` |
| 6 | `table_caption` | Peek index label, not navigated | `table_title` |
| 7 | `figure_caption` | Peek index label, not navigated | `figure_title` |

Classes intentionally dropped from DocLayoutV3's schema: `header`, `footer`, `page_number`, `seal`, `reference`, `algorithm`, `code_snippet`, `chart`, `formula_caption`, `chart_title`, various sub-structures. The rail reader doesn't navigate to any of these; their visual regions are currently filtered by `navigable_classes`/`centering_classes` anyway. Cutting them simplifies the head, improves per-class recall, and makes the label remapping from public datasets tractable.

Note on `code_snippet`: dropping is defensible because stats lecturers' PDFs rarely contain code-as-figure, and when they do the user can free-pan. Revisit if user feedback disagrees.

Note on `list_item`: DocLayNet has it, DocLayoutV3 doesn't treat it specially. Deliberately rolled into `text` — rail mode navigates list items as text regions already.

---

## 3. Dataset strategy

### Design principles

1. **Public manual labels are the floor.** Teacher noise compounds; manual labels don't. Anchor with DocLayNet + DocBank.
2. **CMCV (Cross-Model Consistency Verification, from MinerU 2.5-Pro) for teacher labels.** Run two heterogeneous teacher models, keep only pages where they agree at IoU ≥ 0.7 on ≥ 80% of detections.
3. **Domain adaptation via the user corpus.** The statistics/ML paper aesthetic (heavy equations, specific table styles, textbook-style pages) is under-represented in DocLayNet/DocBank. Teacher-labelled user PDFs close that gap.
4. **Label quality > label quantity.** MinerU used 65.5M pages; we won't. At YOLO26n's parameter budget, ~200k well-filtered pages should saturate learning.

### Primary datasets

| Dataset | Pages | Native schema | License | Role | Remap effort |
|---|---|---|---|---|---|
| **DocLayNet** | ~80k | 11 classes, manual | CDLA-Permissive-1.0 | Anchor — manual labels, schema overlap with ours is 7/8 classes | Low (explicit class-to-class mapping) |
| **DocBank** | ~500k | 13 classes, LaTeX-derived weak labels | Apache-2.0 | Scale for text + equation; rich equation coverage | Medium (weak labels are noisier; needs confidence filtering) |
| **M6Doc** | ~9k | 74 fine-grained classes | CC-BY-4.0 | Textbook/exam domain — matches stats-lecturer content | Medium (many-to-few collapse) |
| **User corpus + arXiv (stats/ML)** | target 1–2k | — | — | Domain adaptation; teacher-labelled via CMCV | N/A (teacher output is already in our schema) |

### Rejected / deferred

- **PubLayNet (IBM)**: 360k pages but only 5 classes (text/title/list/table/figure) — no equation class, no section-level granularity. Redundant with DocLayNet on text/table/figure. Skip unless we hit a data-starved phase.
- **arXiv + raw LaTeX source (Nougat-style)**: highest-quality equation labels achievable, since LaTeX `\begin{equation}` environments map directly to bounding boxes. But requires recompiling LaTeX for every paper to get reliable page-break alignment, which Nougat deliberately avoided. Defer to a second iteration if the DocBank equation labels prove insufficient.
- **CORD / SROIE / RVL-CDIP**: wrong domain (receipts, forms, document classification). Skip.
- **Synthetic PDFs** (generated from HTML/LaTeX templates): possible but low ROI until we see which classes are actually data-starved after the main mix.

### Proposed training mix

| Source | Pages | Share | Why this share |
|---|---|---|---|
| DocLayNet | ~80k | ~45% | Manual-label quality anchor; covers most classes well |
| DocBank (equation-bearing subset) | ~60k | ~35% | Equation supervision — DocLayNet is weak here |
| M6Doc (remapped) | ~9k | ~5% | Textbook domain, fine-grained boundary cases |
| User corpus + curated arXiv (teacher + CMCV filtered) | ~25k target | ~15% | Domain adaptation on actual user content aesthetic |
| **Total** | **~175k** | | |

Roughly 85% public/manual, 15% teacher-labelled. This inverts MinerU's ratio — acceptable because their end task is VLM document parsing, which benefits from massive scale, whereas we're training a narrow 8-class detector where 175k pages is already plentiful.

### CMCV pipeline for teacher labels

We can afford to run multiple layout models at scale as teachers. The key is ensemble agreement, which prevents teacher noise from compounding:

- **Primary teacher**: An established layout model (e.g., Docling Heron) deployed on a fast backend (TensorRT if available).
- **Secondary teacher for CMCV**: A different model with different training data/recipe (e.g., `PaddlePaddle/PP-DocLayout_plus-L` or `opendatalab/MinerU2.5-2509-1.2B`). Architectural diversity improves signal.

Algorithm per page:
1. Run primary teacher → detections₁.
2. Run secondary teacher → detections₂.
3. Hungarian match detections₁ ↔ detections₂ by class-aware IoU.
4. Keep the page for training if **≥ 80% of detections₁ have a match at IoU ≥ 0.7 with the same class label** in detections₂.
5. Use detections₁ as the label. (Primary is our target anyway; secondary only gates.)
6. For matched-but-class-disagreeing pairs, log for manual review — these are the genuinely ambiguous cases (e.g., `paragraph_title` vs `text` on a one-line emphasis).

This is lifted directly from MinerU 2.5-Pro's methodology. The filter is the thing that prevents teacher noise from compounding into student ceiling.

### Schema remapping tables

Codified as Python dicts under `experiments/label-prep/schemas.py` (to be created). Sketch:

**DocLayNet → ours**:
```python
DOCLAYNET_TO_OURS = {
    "Text": "text",
    "List-item": "text",              # rolled in
    "Section-header": "paragraph_title",
    "Title": "doc_title",
    "Formula": "equation",
    "Table": "table",
    "Picture": "figure",
    "Caption": None,                  # split below
    "Footnote": None,                 # dropped
    "Page-header": None,              # dropped
    "Page-footer": None,              # dropped
}
# Captions: "Caption" near a Table → table_caption; near a Picture → figure_caption.
# Resolution: nearest-box class within vertical distance threshold.
```

**DocBank → ours**: similar; DocBank has `equation`, `table`, `figure`, `section`, `title`, `paragraph`, plus `list`, `reference`, `footer`, etc. Drops cleaner than DocLayNet.

**M6Doc → ours**: collapse 74 classes; most map to `text`, special-case heading classes (`title`, `subtitle`, `header_1`…`header_6` → `paragraph_title` or `doc_title` by depth), figure/table/formula classes map naturally.

---

## 4. Training pipeline

### Stage 0 — Environment

- Fresh `uv` project under `experiments/train/` (distinct from the now-deleted quantization dir).
- Dependencies: `ultralytics`, `onnx`, `onnxruntime-gpu`, `paddlepaddle` (for teacher), `onnxruntime-openvino` (for INT8 benchmark), `datasets` (HuggingFace, for DocLayNet pull).
- Training box: the user's local GPU if available; fall back to rented A100 for 24–48 hours if not. YOLO26n training on ~175k 640×640 images for 100 epochs is ~20 GPU-hours on an A100.

### Stage 1 — Data prep

Output: YOLO-format label dir (one `.txt` per image with `cls xc yc w h` normalized).

1. Download DocLayNet (HuggingFace `ds4sd/DocLayNet`). Apply remap. Write YOLO labels. ~10 GB on disk.
2. Download DocBank. Filter to pages containing ≥ 1 `equation` or rich structure. Apply remap. Write labels. ~30 GB.
3. Download M6Doc. Apply remap. Write labels. ~2 GB.
4. Harvest user corpus: expand `experiments/PDFs/` to ~500 PDFs (user to provide + curated arXiv stats/ML papers). Rasterise at 150 DPI.
5. Teacher pass: run PP-DocLayoutV3 (TensorRT) + PP-DocLayout_plus-L on all user-corpus pages. Apply CMCV filter. Remap primary teacher output. Write labels.
6. Merge everything into a single dataset with a fixed train/val/test split (90/5/5, stratified by source so eval isn't dominated by one domain).

### Stage 2 — Training

Config highlights:

- **Image size**: 800×800. Deliberately larger than YOLO's COCO default (640) because document details (small equations, section numbers) benefit. Tradeoff: 2.4× compute per page vs 640.
- **Augmentation**: **disable mosaic, mixup, horizontal flip, rotation, HSV perturbation**. Documents have canonical orientation and colour; augmenting breaks it. Keep: scale jitter (0.5–1.5), translate (±5%), minor blur for resolution robustness.
- **Epochs**: 100, with early-stopping patience 20.
- **Batch size**: as large as fits in GPU memory (16–32 for A100).
- **Optimizer**: MuSGD (YOLO26 default). Don't override.
- **Class weighting**: inverse-frequency weighting on the classification loss. `equation` and `figure_caption` will be under-represented vs `text`; the weighting prevents the model from just predicting `text` everywhere.
- **Pretrained weights**: initialise from COCO-pretrained YOLO26n. Layout detection is close enough to object detection that the backbone transfer is worth it.

### Stage 3 — Evaluation

Golden set: **100 hand-labelled pages** drawn from the user corpus (disjoint from training). Stratified to include:

- 20 single-column text-heavy (straightforward baseline)
- 20 two-column academic papers (typical case)
- 20 equation-heavy (integrals, matrix equations, aligned environments)
- 15 table-heavy (simple + spanning-cell)
- 15 figure-heavy (with captions)
- 10 complex multi-column (3+ columns, floats, sidebars)

Metrics:

| Metric | Target | Why |
|---|---|---|
| mAP@0.5 (macro-avg over classes) | ≥ 0.75 | Detection quality vs fp32 teacher |
| mAP@0.5:0.95 | ≥ 0.55 | Localisation tightness |
| Per-class recall at confidence 0.4 | ≥ 0.90 for `text`, `equation`; ≥ 0.80 others | Matches our inference threshold |
| Kendall τ (reading order) | ≥ 0.95 (XY-cut + detected boxes vs DocLayoutV3's order) | Rail-mode requires near-perfect ordering |
| ms/page median (CPU, fp32) | ≤ 150 | Pre-quantization ceiling |

### Stage 4 — Quantization

YOLO architecture PTQ is well-trodden. Options, in order of preference:

1. **Ultralytics built-in INT8 export to OpenVINO**: one command, handles calibration, produces deployable artefact. Uses NNCF under the hood.
2. **ONNX + ORT static INT8 (Percentile) with chunked calibration**: same as the option we discussed for Heron. Works here because YOLO's graph doesn't have the DETR LayerNorm-heavy decoder that caused Heron's failure.
3. **TensorRT INT8**: best speedup but limits deployment to NVIDIA. Worth exporting as an optional fast-path for users with GPUs.

Calibration set: **500 pages from the user corpus + DocLayNet mix**, not COCO (domain mismatch). Enough for percentile convergence; small enough that memory isn't a blocker.

Quantization target gates:

- mAP degradation ≤ 3 points from fp32
- Latency: ≤ 50 ms/page median (ONNX Runtime CPU)
- Size: ≤ 20 MB

If (1) meets all three gates, we ship the OpenVINO artefact. If latency is close but mAP drops too much, escalate to QAT (Quantization-Aware Training) as a second pass.

---

## 5. Integration into RailReader2

### Drop-in points

Current code: `src/RailReader.Core/Services/LayoutAnalyzer.cs` (PP-DocLayoutV3, ONNX, `[N,7]` output with reading order as 7th column).

New contract: YOLO26n output is `[N, 6]` per class (cls, conf, x0, y0, x1, y1) — **no reading order column**. Reading order is a post-pass.

Changes:

1. **Replace the model path** and the post-processing logic in `LayoutAnalyzer` — from DocLayoutV3's confidence/NMS/order-sort pipeline to YOLO26's NMS-free output + XY-cut.
2. **Add `Services/XYCutReadingOrder.cs`** (Core, rendering-agnostic). Input: list of detected boxes. Output: boxes with `order` field populated.
3. **Update class-ID mapping** wherever DocLayoutV3's 26-class IDs are referenced. Candidate grep targets: `navigable_classes[]` config defaults, `BackgroundAnalysisQueue`, `PeekIndexBuilder`.
4. **Model loading**: `FindModelPath()` search order unchanged, but look for `yolo26n-layout.onnx` (or `-int8.onnx`) rather than `PP-DocLayoutV3.onnx`. Update `scripts/download-model.sh`.
5. **Config migration**: `navigable_classes` / `centering_classes` use string class names. Because we're keeping the 8 names we already use, no breaking change for users' existing config files — but we need to emit a deprecation warning if old config references classes we're dropping (e.g., `"header"`).

### Fallback behaviour

Current fallback when ONNX model missing: horizontal-strip detection in `LayoutAnalyzer`. Keep as-is — activates only when the user hasn't run `download-model.sh`.

### Reading-order fallback

If XY-cut returns a contradictory ordering (detected by heuristic: ≥ 2 cycle-like swaps vs simple y-sort baseline), fall back to plain y-sort + x-tiebreak. This is defensive; XY-cut is well-behaved on clean inputs.

---

## 6. Risks & open questions

### High risk

- **Class-imbalance collapse**: `figure_caption` and `table_caption` will be <5% of labels. If the inverse-frequency weighting isn't enough, we may need caption-specific oversampling or focal loss. **Mitigation**: measure per-class recall at epoch 20 and adjust.
- **Reading order on complex layouts**: XY-cut is known to fail on floating figures and text-wrapping-around-figure cases. Users who read heavily-illustrated textbooks may see this. **Mitigation**: measure on the golden set's "complex multi-column" stratum before committing. If τ < 0.9 on that stratum, escalate to LayoutReader for those pages only (could detect "complex" via column-count heuristic).
- **Inline-math detection is out of scope**. DocLayoutV3 doesn't reliably catch inline $x^2$ either, so we're not regressing, but we're also not gaining. **Mitigation**: accept as known limitation; revisit if the user's workflow actually stops on inline math.

### Medium risk

- **YOLO26 is new (Jan 2026).** Fewer deployment gotchas known than YOLO11. Ultralytics' export pipeline may have rough edges for INT8 on the OpenVINO path specifically. **Mitigation**: plan a fallback to YOLO11n if any single export path blocks us for > 2 days.
- **Teacher CMCV may be too permissive or too strict.** 80%/IoU-0.7 thresholds are first guesses from the MinerU paper's numbers; we may need to tune. **Mitigation**: hold out a 200-page hand-labelled CMCV-validation set, measure what % of filter-accepted pages have correct labels on manual spot-check.
- **DocBank's weak labels** are known to have systematic errors (missed inline equations, merged paragraphs). Contributes 35% of our mix. **Mitigation**: spot-check 100 random DocBank pages before committing the full 60k; if error rate > 10%, reduce share and lean more on DocLayNet.

### Low risk

- **Training compute cost**: ~20 GPU-hours on A100 is $20–40 rented. Not a blocker.
- **Schema remap bugs**: caught by visual validation on a sample.

### Open questions for the user

1. **GPU access for training**: do you have a local GPU you'd want to use, or do we rent A100 time?
2. **User corpus expansion**: happy to curate ~500 additional PDFs (stats/ML arXiv papers + textbooks) for domain adaptation, or keep it focused on your existing test corpus?
3. **`code_snippet` class**: confirmed drop, or want to keep because some stats textbooks have inline R/Python?
4. **TensorRT fast-path** as a shipped optional: worth the extra packaging complexity (per-platform TRT engines) for NVIDIA users, or stick to OpenVINO/CoreML/CPU ONNX only?
5. **Inline-math**: accept as out of scope, or is this a feature you want us to invest in separately?

---

## 7. Phased timeline

Rough calendar estimate assuming one engineer (probably Claude + user) working on it, non-continuous.

| Phase | Description | Est. duration | Gated on |
|---|---|---|---|
| 0 | Set up `experiments/train/` project, answer open questions above | 1 day | User decisions |
| 1 | Data pipeline: download + remap DocLayNet, DocBank, M6Doc to YOLO format | 3–5 days | Disk space |
| 2 | Teacher + CMCV pipeline on user corpus | 2–3 days | Teacher model availability (both models are on HF — should be same-day) |
| 3 | First YOLO26n training run (fp32, all sources) | 2–3 days | Phase 1 + 2 complete |
| 4 | Golden-set eval, iterate training (class weighting, mix ratios) | 3–5 days | Iteration count unknown |
| 5 | XY-cut reading-order + end-to-end eval harness | 2 days | Phase 3 |
| 6 | INT8 quantization + benchmark | 2 days | Phase 3 |
| 7 | Integration into RailReader2 | 3–5 days | Phase 5 + 6 |
| 8 | On-device testing (user) + polish | 2–3 days | Phase 7 + user availability |

**Total calendar**: 3–5 weeks elapsed, ~3 weeks of active work.

Natural checkpoints for go/no-go reviews with the user:

- **After Phase 2**: look at CMCV-filter-accept rate on the user corpus. If < 30%, we have a teacher-disagreement problem to debug before training.
- **After Phase 4**: golden-set mAP. If < 0.65 we don't ship and either expand data or upgrade to YOLO26s.
- **After Phase 6**: quantized mAP + latency. If INT8 drops > 3 mAP points or latency > 50 ms/page, consider QAT or ship fp16 instead.

---

## 8. Decisions log

Decisions made during planning, to avoid re-litigation:

- **8-class schema, dropping header/footer/page_number/code/etc.** — rail reader doesn't navigate them; keeping them adds training complexity for no UX gain.
- **XY-cut over column clustering / LayoutReader** — simple wins until we measure failure.
- **Detection and ordering decoupled** — accepted small accuracy cost on complex layouts in exchange for clean quantization and a swappable ordering subsystem.
- **DocLayNet + DocBank as primary mix, user corpus via CMCV-filtered teacher for domain adaptation** — 85% public / 15% teacher, inverts MinerU's ratio because our task is narrower.
- **YOLO26n start, -s as escalation** — size/perf tradeoff, re-evaluate after first training run.
- **Heavy augmentation disabled** — documents have canonical orientation; mosaic/flip actively hurt.
- **Dropped Heron/RT-DETR from consideration entirely** — proven to not quantize on CPU EPs.

Decisions deferred, explicitly not now:

- Which INT8 backend (OpenVINO vs ONNX-INT8 vs TensorRT) ships as default — decide after Phase 6 benchmarks.
- Whether to invest in LayoutReader for reading order — decide after golden-set τ measurement.
- Whether to extend to inline-math — gated on user feedback post-ship.
- Whether to add back specific dropped classes (code, list) — gated on user feedback post-ship.
