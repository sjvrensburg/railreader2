"""Compare YOLO+XY-cut reading order against PP-DocLayoutV3 (the oracle) over
a sample of training-set images. Reports Kendall τ + exact-match metrics and
dumps worst-failure cases for visual inspection.

Usage:
    python compare.py --images /home/stefan/Downloads/18000/images \\
        --sample 200 \\
        --yolo-model ../../models/yolo26DLA.onnx \\
        --teacher-model ../../models/PP-DocLayoutV3.onnx \\
        --output report/
"""

from __future__ import annotations

import argparse
import json
import random
import sys
from pathlib import Path
from statistics import mean, median

import numpy as np
from PIL import Image
from scipy.stats import kendalltau
from tqdm import tqdm

import teacher
import yolo
import xy_cut
from visualise import annotate, _font

import colorsys
from PIL import ImageDraw


def iou(a, b) -> float:
    """IoU of two boxes with (x, y, w, h) attributes."""
    ax1, ay1 = a.x, a.y
    ax2, ay2 = a.x + a.w, a.y + a.h
    bx1, by1 = b.x, b.y
    bx2, by2 = b.x + b.w, b.y + b.h
    ix1, iy1 = max(ax1, bx1), max(ay1, by1)
    ix2, iy2 = min(ax2, bx2), min(ay2, by2)
    inter = max(0.0, ix2 - ix1) * max(0.0, iy2 - iy1)
    union = a.w * a.h + b.w * b.h - inter
    return inter / union if union > 0 else 0.0


def match_by_iou(teacher_boxes: list, yolo_boxes: list,
                 iou_threshold: float = 0.5) -> list[tuple]:
    """Greedy IoU matching between teacher and YOLO boxes. Returns list of
    (teacher_box, yolo_box) pairs."""
    pairs = []
    used_t = set()
    used_y = set()
    candidates = []
    for i, t in enumerate(teacher_boxes):
        for j, y in enumerate(yolo_boxes):
            score = iou(t, y)
            if score >= iou_threshold:
                candidates.append((score, i, j))
    candidates.sort(reverse=True)
    for score, i, j in candidates:
        if i in used_t or j in used_y:
            continue
        pairs.append((teacher_boxes[i], yolo_boxes[j]))
        used_t.add(i); used_y.add(j)
    return pairs


def evaluate_one(image: Image.Image,
                 teacher_session, yolo_session,
                 iou_threshold: float = 0.5) -> dict:
    teacher_boxes = teacher.run(teacher_session, image)
    yolo_raw = yolo.run(yolo_session, image)
    yolo_ordered = xy_cut.sort(yolo_raw)

    matches = match_by_iou(teacher_boxes, yolo_ordered, iou_threshold)
    if len(matches) < 2:
        return {
            "matched": len(matches),
            "n_teacher": len(teacher_boxes),
            "n_yolo": len(yolo_ordered),
            "tau": None,
            "exact_match": None,
        }

    t_orders = [t.order for t, _ in matches]
    y_orders = [y.order for _, y in matches]
    tau, _ = kendalltau(t_orders, y_orders)
    exact = sorted(zip(t_orders, y_orders)) == sorted(
        zip(t_orders, sorted(y_orders, key=lambda o: t_orders[y_orders.index(o)]))
    )
    # exact-match = the two rankings induce the same permutation
    t_perm = [r for r, _ in sorted(enumerate(t_orders), key=lambda p: p[1])]
    y_perm = [r for r, _ in sorted(enumerate(y_orders), key=lambda p: p[1])]
    exact = t_perm == y_perm

    return {
        "matched": len(matches),
        "n_teacher": len(teacher_boxes),
        "n_yolo": len(yolo_ordered),
        "tau": float(tau) if tau is not None and not np.isnan(tau) else None,
        "exact_match": bool(exact),
    }


def render_side_by_side(image: Image.Image,
                        teacher_boxes: list, yolo_boxes: list) -> Image.Image:
    """Render image twice with overlays from each algorithm, stacked horizontally
    with column titles."""
    annotated_t = annotate(image, teacher_boxes)
    annotated_y = annotate(image, yolo_boxes)
    W, H = image.size
    title_h = 36
    out = Image.new("RGB", (W * 2, H + title_h), (255, 255, 255))
    out.paste(annotated_t, (0, title_h))
    out.paste(annotated_y, (W, title_h))
    draw = ImageDraw.Draw(out)
    f = _font(24)
    draw.text((10, 6), "PP-DocLayoutV3 (oracle)", fill=(0, 0, 0), font=f)
    draw.text((W + 10, 6), "YOLO + XY-cut", fill=(0, 0, 0), font=f)
    return out


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--images", type=Path, required=True)
    p.add_argument("--sample", type=int, default=200)
    p.add_argument("--seed", type=int, default=42)
    p.add_argument("--yolo-model", type=Path,
                   default=Path(__file__).parent.parent.parent / "models" / "yolo26DLA.onnx")
    p.add_argument("--teacher-model", type=Path,
                   default=Path(__file__).parent.parent.parent / "models" / "PP-DocLayoutV3.onnx")
    p.add_argument("--output", type=Path, default=Path("report"))
    p.add_argument("--iou-threshold", type=float, default=0.5)
    p.add_argument("--worst-n", type=int, default=10,
                   help="Render this many worst-tau images side-by-side")
    p.add_argument("--providers", nargs="+", default=None)
    args = p.parse_args()

    args.output.mkdir(parents=True, exist_ok=True)

    random.seed(args.seed)
    images = sorted(list(args.images.glob("*.jpg")) + list(args.images.glob("*.png")))
    if not images:
        print(f"No images found in {args.images}", file=sys.stderr)
        return 1
    sample = random.sample(images, min(args.sample, len(images)))

    print(f"Loading models...", flush=True)
    teacher_sess = teacher.load_teacher(args.teacher_model, args.providers)
    yolo_sess = yolo.load_yolo(args.yolo_model, args.providers)
    print(f"  teacher: {teacher_sess.get_providers()}")
    print(f"  yolo:    {yolo_sess.get_providers()}")

    rows = []
    failures = []  # (tau, image_path)
    for img_path in tqdm(sample, desc="Comparing"):
        try:
            with Image.open(img_path) as image:
                image = image.convert("RGB")
                stats = evaluate_one(image, teacher_sess, yolo_sess,
                                     args.iou_threshold)
            row = {"image": img_path.name, **stats}
            rows.append(row)
            if stats["tau"] is not None and stats["tau"] < 0.9:
                failures.append((stats["tau"], img_path))
        except Exception as e:
            rows.append({"image": img_path.name, "error": str(e)})

    # Aggregate
    valid_taus = [r["tau"] for r in rows if r.get("tau") is not None]
    exact_count = sum(1 for r in rows if r.get("exact_match"))
    tau_ge_09 = sum(1 for t in valid_taus if t >= 0.9)
    tau_ge_095 = sum(1 for t in valid_taus if t >= 0.95)
    tau_eq_1 = sum(1 for t in valid_taus if t >= 0.999)
    insufficient = sum(1 for r in rows if r.get("tau") is None)
    errored = sum(1 for r in rows if "error" in r)

    report_lines = [
        f"# XY-cut vs PP-DocLayoutV3 — n={len(rows)} images",
        "",
        f"## Coverage",
        f"- Sampled: **{len(rows)}**",
        f"- Insufficient matches (<2 paired boxes): {insufficient}",
        f"- Errored: {errored}",
        f"- Scored: {len(valid_taus)}",
        "",
        f"## Reading-order agreement (Kendall τ)",
        f"- Mean τ:    **{mean(valid_taus):.3f}**" if valid_taus else "- (no data)",
        f"- Median τ:  **{median(valid_taus):.3f}**" if valid_taus else "",
        f"- τ = 1.0  (exact match by τ):  {tau_eq_1} ({100*tau_eq_1/len(valid_taus):.1f}%)" if valid_taus else "",
        f"- τ ≥ 0.95:  {tau_ge_095} ({100*tau_ge_095/len(valid_taus):.1f}%)" if valid_taus else "",
        f"- τ ≥ 0.90:  {tau_ge_09} ({100*tau_ge_09/len(valid_taus):.1f}%)" if valid_taus else "",
        f"- Exact permutation match: {exact_count}/{len(valid_taus)} ({100*exact_count/max(1,len(valid_taus)):.1f}%)",
        "",
        f"## τ distribution",
    ]
    if valid_taus:
        bins = [(-1.0, 0.5), (0.5, 0.7), (0.7, 0.8), (0.8, 0.9), (0.9, 0.95), (0.95, 1.01)]
        for lo, hi in bins:
            count = sum(1 for t in valid_taus if lo <= t < hi)
            pct = 100 * count / len(valid_taus)
            bar = "█" * int(pct * 0.5)
            report_lines.append(f"- [{lo:.2f}, {hi:.2f}):  {count:>4}  {pct:>5.1f}%  {bar}")

    failures.sort()
    report_lines.append("")
    report_lines.append(f"## Worst {args.worst_n} cases")
    for tau, img_path in failures[: args.worst_n]:
        report_lines.append(f"- τ={tau:.3f}  {img_path.name}")

    # Per-row dump
    (args.output / "results.json").write_text(json.dumps(rows, indent=2))
    (args.output / "report.md").write_text("\n".join(report_lines))

    # Render side-by-side for worst N
    if failures:
        print(f"\nRendering {min(args.worst_n, len(failures))} worst cases...", flush=True)
        worst_dir = args.output / "worst"
        worst_dir.mkdir(exist_ok=True)
        for tau, img_path in failures[: args.worst_n]:
            with Image.open(img_path) as image:
                image = image.convert("RGB")
                teacher_boxes = teacher.run(teacher_sess, image)
                yolo_raw = yolo.run(yolo_sess, image)
                yolo_ordered = xy_cut.sort(yolo_raw)
            side_by_side = render_side_by_side(image, teacher_boxes, yolo_ordered)
            tau_str = f"{tau:.3f}".replace(".", "_")
            side_by_side.save(worst_dir / f"tau{tau_str}_{img_path.stem}.png")

    print(f"\nReport: {args.output / 'report.md'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
