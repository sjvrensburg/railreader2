# data-prep — teacher-label additional document corpora

This directory contains tooling to **expand TinyLayoutYOLO's training data
beyond YOLO-DLA** by teacher-labelling with PP-DocLayoutV3 (Apache-2.0).

## Why

TinyLayoutYOLO v2 was trained on 18000 academic-paper pages from the
AIBox-IMU YOLO-DLA dataset. v2 detection quality drops when fed documents
with materially different structure:

  * **Lecture notes** (e.g. STAT321 unit-root-tests) — single-column,
    dense math derivations, bullet lists, large fonts. v2 misses many
    elements on pages 1, 3–8.
  * **Some journal templates** (e.g. DANAM ICASSP page 5) — heavy figure
    + math layouts that diverge from the YOLO-DLA distribution.
  * **Anything not academic** — textbooks, financial reports, government
    documents, legal briefs.

The fix is **broader training data**, and the prerequisite is a clean class
mapping from a reliable teacher's schema to ours.

## What's here

| File | Purpose |
|---|---|
| `class_mapping.py` | PP-DocLayoutV3 (25 classes) → TinyLayoutYOLO runtime (16 classes). Three classes are intentionally *dropped*: `formula_number`, `inline_formula`, `seal`. |
| `teacher_label.py` | CLI: scan an image directory, run PP-DocLayoutV3, write YOLO-format `.txt` labels in our runtime schema. |
| `README.md` | This file. |

## Mapping decisions

Run `python class_mapping.py` for a printed mapping table. Key calls:

  * **`algorithm` → `formula`**: algorithm blocks behave like multi-line
    display math in our model. No separate `algorithm` class.
  * **`figure_title` → `note`**: PP-DocLayoutV3's `figure_title` covers
    both figure and table captions. Our `note` is the caption class.
  * **`paragraph_title` → `t2`**: PP-DocLayoutV3's section heading.
    Maps to our subsection heading. (`t1` is reserved for `doc_title`.)
  * **`reference` and `reference_content` both → `reference`**: PP splits
    bibliography heading from entries; our model lumps them.
  * **All page-furniture classes** (`header`, `footer`, `*_image`,
    `number`, `vertical_text`, `aside_text`) → `other`.
  * **Drop `formula_number`**: small inline tags adjacent to equations.
    Adding them would over-fragment our paragraph-scale detector.
  * **Drop `inline_formula`**: inline math sits *inside* our `paragraph`
    boxes — predicting it as a separate class would confuse the model.
  * **Drop `seal`**: Chinese-document stamps, irrelevant for English content.

## What we lose

PP-DocLayoutV3 has no analog for our `t`, `t3`, `keyword`, `author`, or
`class17` classes. Teacher-labelled data from PP contributes **zero
positives** for those — fine; we lean on YOLO-DLA for them. (And `t3` /
`class17` were effectively dead in YOLO-DLA anyway.)

## Workflow

### 1. Get document images

Suggested corpora, in order of estimated value for railreader2's use case:

  * **DocLayNet** (CDLA-Permissive-1.0): 80k pages, very diverse — scientific
    papers, financial reports, patents, manuals, government. **Probably the
    biggest single recall win for the kinds of failures we see today.**
  * **PubLayNet** (CDLA-1.0): 360k pages, biomedical academic. Similar to
    YOLO-DLA but adds biomedical-journal diversity.
  * **User PDFs**: render any PDF (lecture notes, textbooks) to images at
    150 DPI. This addresses the specific failure modes we saw (lecture-note
    layouts).
  * **Synthetic textbook pages**: optional, only if everything else falls
    short on specific document genres.

### 2. Teacher-label

```bash
cd experiments/data-prep
python teacher_label.py \
    --images /path/to/your/images \
    --output /path/to/your/labels \
    --conf 0.4
```

This writes one `.txt` per image in YOLO format using our 16-class runtime
schema. Skips images where the teacher found nothing mappable.

### 3. Merge with YOLO-DLA + train v4

After teacher-labelling, you have a directory of labelled images compatible
with TinyLayoutYOLO's training pipeline. Combine with the original YOLO-DLA
images+labels (point `train.py --images` and `--labels` at the merged dirs)
and run:

```bash
cd experiments/layout-detector
.venv/bin/python train.py \
    --images /path/to/merged/images \
    --labels /path/to/merged/labels \
    --output runs/v4 \
    --warmstart runs/v2/best.pt \
    --epochs 15 \
    --batch-size 12 \
    --lr 1.5e-4 \
    --warmup-epochs 1
```

The warm-start from v2 preserves the in-distribution quality while letting
the additional data fill in the out-of-distribution gaps.

## A note on label quality

Teacher labels are **noisier** than human ground truth — PP-DocLayoutV3 has
its own failure modes (over-fragmentation, occasional misclassification).
Two ways to mitigate, in order of value:

1. **Confidence filtering**: `--conf 0.4` is the default; raise to 0.5 for
   cleaner labels at the cost of fewer detections.
2. **Cross-Model Consistency Verification (CMCV)**: run a second teacher
   (e.g. PaddleOCR-VL or a fine-tuned LayoutLMv3) and keep only labels
   where both agree at IoU ≥ 0.7. The plan for this was in the deleted
   `experiments/order-model/` directory; can be revived if v4 with simple
   teacher-labelling proves insufficient.

For v4 specifically, single-teacher PP-DocLayoutV3 labels are probably
fine — we're not trying to match the teacher exactly, we're trying to
expand structural coverage.
