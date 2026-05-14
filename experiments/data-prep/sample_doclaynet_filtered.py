"""Sample DocLayNet pages where rare classes dominate the layout.

Companion to `sample_doclaynet.py` (uniform random). This script biases the
sample toward pages where Table, Formula, or List-item (reference lists)
dominate — the genres TinyLayoutYOLO v4/v5b underdetect on user-visible
failure cases (dense bibliographies, multi-column financial tables,
math-heavy lecture pages).

Filter rules (page kept if ANY match):
  * Table area > 30% of page area              (financial tables, spreadsheets)
  * Formula area > 25% of page area            (math-heavy pages)
  * >= 8 List-items AND list-item area > 40%   (reference-list back-pages)

DocLayNet category IDs (v1.1 schema):
  1 Caption  2 Footnote  3 Formula  4 List-item  5 Page-footer
  6 Page-header  7 Picture  8 Section-header  9 Table  10 Text  11 Title

We only save IMAGES — labels are produced downstream by `teacher_label.py`
(re-labels with PP-DocLayoutV3 in runtime schema).

Usage:
    python sample_doclaynet_filtered.py --output ~/Downloads/dln_focus --n 2000
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from tqdm import tqdm


CAT_FORMULA = 3
CAT_LIST_ITEM = 4
CAT_TABLE = 9


def page_filter(bboxes: list[list[float]], cats: list[int],
                page_w: float, page_h: float,
                table_thresh: float, formula_thresh: float,
                list_count_thresh: int, list_area_thresh: float
                ) -> tuple[bool, str]:
    """Return (keep, reason). Reason is empty if not kept."""
    page_area = page_w * page_h
    if page_area <= 0:
        return False, ""

    table_area = 0.0
    formula_area = 0.0
    list_area = 0.0
    list_count = 0
    for (x, y, w, h), c in zip(bboxes, cats):
        a = max(0.0, w) * max(0.0, h)
        if c == CAT_TABLE:
            table_area += a
        elif c == CAT_FORMULA:
            formula_area += a
        elif c == CAT_LIST_ITEM:
            list_area += a
            list_count += 1

    table_frac = table_area / page_area
    formula_frac = formula_area / page_area
    list_frac = list_area / page_area

    if table_frac > table_thresh:
        return True, f"table_{table_frac:.2f}"
    if formula_frac > formula_thresh:
        return True, f"formula_{formula_frac:.2f}"
    if list_count >= list_count_thresh and list_frac > list_area_thresh:
        return True, f"list_n{list_count}_{list_frac:.2f}"
    return False, ""


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--output", type=Path, required=True)
    p.add_argument("--n", type=int, default=2000,
                   help="Number of filtered pages to keep")
    p.add_argument("--split", type=str, default="train",
                   choices=["train", "val", "test"])
    p.add_argument("--seed", type=int, default=4242,
                   help="Different default from sample_doclaynet.py so the "
                        "random sample and filtered sample don't overlap")
    p.add_argument("--dataset", type=str, default="docling-project/DocLayNet-v1.1")
    p.add_argument("--scan-limit", type=int, default=60000,
                   help="Stop scanning after this many pages even if n not "
                        "reached (DocLayNet train has ~69k pages)")
    p.add_argument("--prefix", type=str, default="dln_focus",
                   help="Output filename prefix (default 'dln_focus' — distinct "
                        "from the existing 'doclaynet_' random sample)")
    p.add_argument("--table-thresh", type=float, default=0.30)
    p.add_argument("--formula-thresh", type=float, default=0.25)
    p.add_argument("--list-count-thresh", type=int, default=8)
    p.add_argument("--list-area-thresh", type=float, default=0.40)
    args = p.parse_args()

    args.output.mkdir(parents=True, exist_ok=True)

    print(f"loading {args.dataset} ({args.split} split, streaming)...", flush=True)
    from datasets import load_dataset
    ds = load_dataset(args.dataset, split=args.split, streaming=True)
    ds = ds.shuffle(seed=args.seed, buffer_size=10_000)

    saved = 0
    scanned = 0
    skipped = 0
    reason_counts: dict[str, int] = {"table": 0, "formula": 0, "list": 0}
    manifest = []

    pbar = tqdm(total=args.n, desc="filtering")
    for i, ex in enumerate(ds):
        if scanned >= args.scan_limit or saved >= args.n:
            break
        scanned += 1
        try:
            bboxes = ex.get("bboxes") or []
            cats = ex.get("category_id") or []
            meta = ex.get("metadata") or {}
            page_w = float(meta.get("coco_width", 0))
            page_h = float(meta.get("coco_height", 0))
            keep, reason = page_filter(
                bboxes, cats, page_w, page_h,
                args.table_thresh, args.formula_thresh,
                args.list_count_thresh, args.list_area_thresh,
            )
            if not keep:
                continue

            img = ex["image"]
            if img.mode != "RGB":
                img = img.convert("RGB")
            out_path = args.output / f"{args.prefix}_{saved:06d}.jpg"
            img.save(out_path, "JPEG", quality=88)
            manifest.append({
                "file": out_path.name,
                "reason": reason,
                "doc_category": meta.get("doc_category", ""),
                "collection": meta.get("collection", ""),
                "stream_index": i,
            })
            if reason.startswith("table"):
                reason_counts["table"] += 1
            elif reason.startswith("formula"):
                reason_counts["formula"] += 1
            elif reason.startswith("list"):
                reason_counts["list"] += 1
            saved += 1
            pbar.update(1)
        except Exception as e:
            skipped += 1
            tqdm.write(f"skip {i}: {e}")
    pbar.close()

    (args.output / "manifest.json").write_text(json.dumps(manifest, indent=2))

    print(f"\nscanned {scanned} pages, saved {saved}, skipped {skipped}")
    print(f"by reason: table={reason_counts['table']}  "
          f"formula={reason_counts['formula']}  list={reason_counts['list']}")
    print(f"output: {args.output}")
    print(f"manifest: {args.output / 'manifest.json'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
