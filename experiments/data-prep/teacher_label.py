"""Teacher-label a directory of document images using PP-DocLayoutV3.

Produces YOLO-format labels (one .txt per image, lines `class cx cy w h`
normalised) in the TinyLayoutYOLO runtime schema. Use these alongside the
existing YOLO-DLA labels to expand training coverage to other document
genres (lecture notes, textbooks, financial docs, etc.).

Usage:
    python teacher_label.py \\
        --images /path/to/document/images \\
        --output /path/to/output/labels \\
        --conf 0.4

Then point the training-data dir at a merged image+label set:
    /path/to/training/images/        # YOLO-DLA + new
    /path/to/training/labels/        # YOLO-DLA + new (using same schema)

Notes:
  * Confidence threshold (--conf, default 0.4) filters PP-DocLayoutV3
    detections — lower than YOLO26's 0.4 means more recall + more noise.
  * Skips classes that have no analog in our schema (formula_number,
    inline_formula, seal). See class_mapping.py for rationale.
  * Skips images whose teacher output has zero retained labels (would
    contribute nothing to training).
"""

from __future__ import annotations

import argparse
import sys
from dataclasses import dataclass
from pathlib import Path

import numpy as np
import onnxruntime as ort
from PIL import Image
from tqdm import tqdm

# Reuse PP-DocLayoutV3 inference from the validation harness.
sys.path.insert(0, str(Path(__file__).parent.parent / "order-validation"))
import teacher as pp  # noqa: E402

from class_mapping import remap, TINY_RUNTIME_CLASSES  # noqa: E402


@dataclass
class Stats:
    images_total: int = 0
    images_with_labels: int = 0
    images_skipped_empty: int = 0
    detections_total: int = 0
    detections_kept: int = 0
    detections_dropped: int = 0
    per_class_kept: dict[int, int] = None

    def __post_init__(self):
        if self.per_class_kept is None:
            self.per_class_kept = {i: 0 for i in range(len(TINY_RUNTIME_CLASSES))}


def process_image(img_path: Path, label_path: Path, session: ort.InferenceSession,
                  conf_threshold: float, stats: Stats) -> bool:
    """Returns True if at least one label was written for this image."""
    stats.images_total += 1
    try:
        with Image.open(img_path) as img:
            img = img.convert("RGB")
            img_w, img_h = img.size
            pp_dets = pp.run(session, img, conf_threshold=conf_threshold)
    except Exception as e:
        print(f"  skip {img_path.name}: {e}", file=sys.stderr)
        return False

    lines: list[str] = []
    for d in pp_dets:
        stats.detections_total += 1
        tiny_cls = remap(d.cls)
        if tiny_cls is None:
            stats.detections_dropped += 1
            continue

        # PP boxes are in normalised [0, 1]. Convert (x, y, w, h) top-left form
        # into YOLO (cx, cy, w, h) form.
        cx = d.x + d.w / 2
        cy = d.y + d.h / 2

        # Clip — PP can occasionally emit boxes that slip past 1.0 by ε
        if cx <= 0 or cy <= 0 or d.w <= 0 or d.h <= 0:
            stats.detections_dropped += 1
            continue
        cx = min(1.0, cx); cy = min(1.0, cy)
        w = min(d.w, 1.0); h = min(d.h, 1.0)

        lines.append(f"{tiny_cls} {cx:.6f} {cy:.6f} {w:.6f} {h:.6f}")
        stats.detections_kept += 1
        stats.per_class_kept[tiny_cls] = stats.per_class_kept.get(tiny_cls, 0) + 1

    if not lines:
        stats.images_skipped_empty += 1
        return False

    label_path.write_text("\n".join(lines) + "\n")
    stats.images_with_labels += 1
    return True


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--images", type=Path, required=True,
                   help="Directory of input images (.jpg / .png)")
    p.add_argument("--output", type=Path, required=True,
                   help="Output directory for YOLO-format .txt labels")
    p.add_argument("--teacher-model", type=Path,
                   default=Path(__file__).parent.parent.parent / "models" / "PP-DocLayoutV3.onnx")
    p.add_argument("--conf", type=float, default=0.4,
                   help="PP-DocLayoutV3 detection confidence threshold (default 0.4)")
    p.add_argument("--providers", nargs="+", default=None,
                   help="ORT providers (default: try CUDA first, fallback CPU)")
    p.add_argument("--limit", type=int, default=None,
                   help="Process at most this many images (smoke testing)")
    args = p.parse_args()

    if not args.images.is_dir():
        print(f"ERROR: --images must be a directory: {args.images}", file=sys.stderr)
        return 1
    if not args.teacher_model.exists():
        print(f"ERROR: teacher model not found: {args.teacher_model}", file=sys.stderr)
        return 1
    args.output.mkdir(parents=True, exist_ok=True)

    images = sorted(
        list(args.images.glob("*.jpg"))
        + list(args.images.glob("*.png"))
        + list(args.images.glob("*.jpeg"))
    )
    if args.limit:
        images = images[: args.limit]
    if not images:
        print(f"no images found in {args.images}", file=sys.stderr)
        return 1

    print(f"loading PP-DocLayoutV3 ({args.teacher_model})...", flush=True)
    session = pp.load_teacher(args.teacher_model, providers=args.providers)
    print(f"  providers: {session.get_providers()}")
    print(f"  conf threshold: {args.conf}")
    print(f"  images: {len(images)}")
    print(f"  output: {args.output}")

    stats = Stats()
    for img_path in tqdm(images, desc="labelling"):
        label_path = args.output / (img_path.stem + ".txt")
        process_image(img_path, label_path, session, args.conf, stats)

    print()
    print("=" * 60)
    print(f"images:              {stats.images_total}")
    print(f"  with labels:       {stats.images_with_labels}")
    print(f"  skipped (empty):   {stats.images_skipped_empty}")
    print(f"detections:          {stats.detections_total}")
    print(f"  kept:              {stats.detections_kept}")
    print(f"  dropped (no map):  {stats.detections_dropped}")
    print()
    print(f"{'cls':>3}  {'name':<12}  {'count':>8}")
    print("-" * 28)
    for cls_id, count in sorted(stats.per_class_kept.items()):
        if count == 0:
            continue
        print(f"{cls_id:>3}  {TINY_RUNTIME_CLASSES[cls_id]:<12}  {count:>8}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
