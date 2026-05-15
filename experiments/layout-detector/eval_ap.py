"""Per-class AP evaluation on the held-out val split.

Runs the trained model over the same val files used during training (same
seed + val_fraction), computes AP@0.5 per class plus overall mAP, and prints
a diagnostic table that tells us which classes need help.

Output columns:
  id, name, AP, precision, recall, #GT, #pred, mean_IoU
where mean_IoU is computed over matched TP pairs and serves as a tightness
proxy — for rail-mode UX, this is the metric we actually care about.

Usage:
    python eval_ap.py                          # defaults to runs/v2/best.pt
    python eval_ap.py --checkpoint runs/v1/best.pt   # eval an older run
    python eval_ap.py --output report.json     # also write JSON report
    python eval_ap.py --limit 50               # smoke-test on small subset
"""

from __future__ import annotations

import argparse
import json
from collections import defaultdict
from pathlib import Path

import numpy as np
import torch
import torchvision
from PIL import Image
from tqdm import tqdm

from layout_detector import build_model
from layout_detector.model import TinyLayoutYOLO
from layout_detector.dataset import (
    make_train_val_split,
    letterbox,
    _load_labels,
    YOLO_DLA_CLASS_REMAP,
)
from layout_detector.decode import decode_predictions


# Mirror of LayoutConstants.LayoutClasses (corrected runtime mapping).
CLS_NAMES = [
    "t", "t1", "t2", "t3", "paragraph", "author", "keyword", "abstract",
    "reference", "graph", "note", "other", "formula", "table", "footnote",
    "class17",
]


def _iou_one_vs_many(box: np.ndarray, others: np.ndarray) -> np.ndarray:
    """IoU between one xyxy box and (N, 4) array of xyxy boxes."""
    if len(others) == 0:
        return np.zeros(0)
    ix1 = np.maximum(box[0], others[:, 0])
    iy1 = np.maximum(box[1], others[:, 1])
    ix2 = np.minimum(box[2], others[:, 2])
    iy2 = np.minimum(box[3], others[:, 3])
    inter = np.clip(ix2 - ix1, 0, None) * np.clip(iy2 - iy1, 0, None)
    area_a = (box[2] - box[0]) * (box[3] - box[1])
    area_b = (others[:, 2] - others[:, 0]) * (others[:, 3] - others[:, 1])
    union = area_a + area_b - inter + 1e-7
    return inter / union


def collect_predictions(model, val_files, image_dir, label_dir, input_size,
                        device, conf_threshold=0.05, nms_iou=0.5,
                        class_remap=None):
    """Returns:
        detections: list per image of (cls, score, x1, y1, x2, y2) normalised
        gts:        list per image of (cls, x1, y1, x2, y2) normalised
    """
    detections: list[list[tuple]] = []
    gts: list[list[tuple]] = []

    model.eval()
    for fname in tqdm(val_files, desc="inferencing"):
        img_path = image_dir / fname
        with Image.open(img_path) as img:
            img = img.convert("RGB")
            orig_w, orig_h = img.size
            canvas, scale, pad_x, pad_y = letterbox(img, input_size)

        arr = np.asarray(canvas, dtype=np.float32) / 255.0
        chw = torch.from_numpy(arr.transpose(2, 0, 1))[None].to(device)

        with torch.no_grad():
            out = model(chw)
            boxes, scores, labels = decode_predictions(out)
        boxes = boxes[0].cpu().numpy()    # in 480-space
        scores = scores[0].cpu().numpy()
        labels = labels[0].cpu().numpy()

        # Conf threshold + class-aware NMS
        mask = scores >= conf_threshold
        boxes = boxes[mask]; scores = scores[mask]; labels = labels[mask]
        if len(boxes):
            keep = torchvision.ops.batched_nms(
                torch.from_numpy(boxes).float(),
                torch.from_numpy(scores).float(),
                torch.from_numpy(labels).long(),
                nms_iou,
            ).numpy()
            boxes = boxes[keep]; scores = scores[keep]; labels = labels[keep]

        # Unproject letterbox → original-image pixels → normalised
        new_w = int(round(orig_w * scale))
        new_h = int(round(orig_h * scale))
        dets_i: list[tuple] = []
        for (x1, y1, x2, y2), sc, lb in zip(boxes, scores, labels):
            ux1 = (x1 - pad_x) * orig_w / max(1, new_w)
            uy1 = (y1 - pad_y) * orig_h / max(1, new_h)
            ux2 = (x2 - pad_x) * orig_w / max(1, new_w)
            uy2 = (y2 - pad_y) * orig_h / max(1, new_h)
            ux1 = max(0.0, ux1) / orig_w; uy1 = max(0.0, uy1) / orig_h
            ux2 = min(float(orig_w), ux2) / orig_w
            uy2 = min(float(orig_h), uy2) / orig_h
            if ux2 - ux1 <= 0 or uy2 - uy1 <= 0:
                continue
            dets_i.append((int(lb), float(sc), ux1, uy1, ux2, uy2))
        detections.append(dets_i)

        # GTs: YOLO format → xyxy normalised + class remap
        lbl_path = label_dir / (Path(fname).stem + ".txt")
        gt_boxes, gt_labels = _load_labels(lbl_path, class_remap)
        gts_i: list[tuple] = []
        for (cx, cy, w, h), cls in zip(gt_boxes, gt_labels):
            gts_i.append((int(cls), cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2))
        gts.append(gts_i)

    return detections, gts


def compute_per_class_ap(detections, gts, num_classes, iou_thr=0.5):
    """COCO 101-point AP per class.

    Returns dict: class_id → {AP, precision, recall, num_gt, num_pred, mean_iou_tp}.
    `mean_iou_tp` is the mean IoU over true-positive matches — our tightness proxy.
    """
    per_class = {c: {"matches": [], "num_gt": 0, "ious_tp": []}
                 for c in range(num_classes)}

    for det_image, gt_image in zip(detections, gts):
        # Group GTs by class
        gts_by_cls: dict[int, list] = defaultdict(list)
        for cls, x1, y1, x2, y2 in gt_image:
            gts_by_cls[cls].append([x1, y1, x2, y2])
            per_class[cls]["num_gt"] += 1

        # Group detections by class
        dets_by_cls: dict[int, list] = defaultdict(list)
        for cls, score, x1, y1, x2, y2 in det_image:
            dets_by_cls[cls].append((score, np.array([x1, y1, x2, y2])))

        for cls, dets in dets_by_cls.items():
            gt_boxes = (np.array(gts_by_cls[cls], dtype=np.float64)
                        if gts_by_cls.get(cls) else np.zeros((0, 4)))
            gt_matched = np.zeros(len(gt_boxes), dtype=bool)
            # Greedy match in descending score order
            for score, box in sorted(dets, key=lambda x: -x[0]):
                if len(gt_boxes) == 0:
                    per_class[cls]["matches"].append((score, 0))
                    continue
                ious = _iou_one_vs_many(box, gt_boxes)
                best_iou, best_idx = iou_thr, -1
                for j, iou in enumerate(ious):
                    if iou >= best_iou and not gt_matched[j]:
                        best_iou, best_idx = iou, j
                if best_idx >= 0:
                    per_class[cls]["matches"].append((score, 1))
                    per_class[cls]["ious_tp"].append(float(best_iou))
                    gt_matched[best_idx] = True
                else:
                    per_class[cls]["matches"].append((score, 0))

    # AP per class
    out: dict[int, dict] = {}
    recall_thr = np.linspace(0, 1, 101)
    for cls in range(num_classes):
        matches = sorted(per_class[cls]["matches"], key=lambda x: -x[0])
        num_gt = per_class[cls]["num_gt"]
        ious_tp = per_class[cls]["ious_tp"]

        if num_gt == 0:
            out[cls] = {"AP": float("nan"), "precision": float("nan"),
                        "recall": float("nan"), "num_gt": 0,
                        "num_pred": len(matches),
                        "mean_iou_tp": float("nan")}
            continue
        if not matches:
            out[cls] = {"AP": 0.0, "precision": 0.0, "recall": 0.0,
                        "num_gt": num_gt, "num_pred": 0,
                        "mean_iou_tp": float("nan")}
            continue

        tp = np.cumsum([m[1] for m in matches])
        fp = np.cumsum([1 - m[1] for m in matches])
        recall = tp / num_gt
        precision = tp / np.maximum(tp + fp, 1e-7)

        # COCO 101-point interpolated AP
        ap_pts = np.zeros_like(recall_thr)
        for i, r in enumerate(recall_thr):
            mask = recall >= r
            if mask.any():
                ap_pts[i] = precision[mask].max()
        ap = float(ap_pts.mean())

        out[cls] = {
            "AP": ap,
            "precision": float(precision[-1]),
            "recall": float(recall[-1]),
            "num_gt": int(num_gt),
            "num_pred": int(len(matches)),
            "mean_iou_tp": float(np.mean(ious_tp)) if ious_tp else float("nan"),
            "ious_tp": ious_tp,    # raw per-TP IoUs for downstream aggregation
        }
    return out


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--checkpoint", type=Path, default=Path("runs/v2/best.pt"))
    p.add_argument("--images", type=Path,
                   default=Path("/home/stefan/Downloads/18000/images"))
    p.add_argument("--labels", type=Path,
                   default=Path("/home/stefan/Downloads/18000/labels"))
    p.add_argument("--input-size", type=int, default=480)
    p.add_argument("--num-classes", type=int, default=16)
    p.add_argument("--val-fraction", type=float, default=0.05,
                   help="Must match the value used during training")
    p.add_argument("--seed", type=int, default=42,
                   help="Must match the value used during training")
    p.add_argument("--conf-threshold", type=float, default=0.05,
                   help="Low threshold to capture full PR curve")
    p.add_argument("--nms-iou", type=float, default=0.5)
    p.add_argument("--ap-iou", type=float, default=0.5)
    p.add_argument("--output", type=Path, default=None,
                   help="Optional JSON report path")
    p.add_argument("--device", type=str,
                   default="cuda" if torch.cuda.is_available() else "cpu")
    p.add_argument("--limit", type=int, default=None,
                   help="Limit val images for smoke-test")
    p.add_argument("--class-remap", type=str, default="auto",
                   help="'auto' (default): detect from corpus. 'yolo-dla': apply "
                        "YOLO_DLA_CLASS_REMAP (raw YOLO-DLA dataset schema). "
                        "'none': no remap (labels already runtime schema, e.g. "
                        "merged v4_corpus).")
    args = p.parse_args()

    device = torch.device(args.device)
    print(f"device: {device}")

    # Load checkpoint
    print(f"loading {args.checkpoint}")
    ck = torch.load(args.checkpoint, map_location=device, weights_only=False)
    state = ck["model"] if isinstance(ck, dict) and "model" in ck else ck
    has_rcm = any(k.startswith("rcm_p3.") or k.startswith("rcm_p4.") for k in state)
    has_mnv4 = any(k.startswith("backbone.backbone.") for k in state)
    backbone = "mnv4_small" if has_mnv4 else "mnv3_small"
    print(f"checkpoint has RCM: {has_rcm}  backbone: {backbone}")
    model = TinyLayoutYOLO(num_classes=args.num_classes,
                           pretrained=False, use_rcm=has_rcm,
                           backbone=backbone).to(device)
    model.load_state_dict(state)

    # Val split: same files as training
    _, val_files = make_train_val_split(
        args.images, args.labels,
        val_fraction=args.val_fraction, seed=args.seed,
    )
    if args.limit:
        val_files = val_files[: args.limit]
    print(f"val images: {len(val_files)}  "
          f"(seed={args.seed}, val_fraction={args.val_fraction})")

    if args.class_remap == "yolo-dla":
        class_remap = YOLO_DLA_CLASS_REMAP
    elif args.class_remap == "none":
        class_remap = None
    else:  # auto — detect by presence of YOLO-DLA ID 16 in any label
        sample_labels = list(args.labels.glob("*.txt"))[:50]
        has_dla_ids = any(
            any(int(line.split()[0]) == 16 for line in lf.read_text().splitlines() if line.split())
            for lf in sample_labels if lf.exists()
        )
        class_remap = YOLO_DLA_CLASS_REMAP if has_dla_ids else None
    print(f"class_remap: "
          f"{'YOLO_DLA_CLASS_REMAP' if class_remap is YOLO_DLA_CLASS_REMAP else 'None (runtime schema)'}")

    detections, gts = collect_predictions(
        model, val_files, args.images, args.labels,
        input_size=args.input_size, device=device,
        conf_threshold=args.conf_threshold, nms_iou=args.nms_iou,
        class_remap=class_remap,
    )

    # Per-class AP
    results = compute_per_class_ap(detections, gts, args.num_classes,
                                   iou_thr=args.ap_iou)

    # Report
    def fmt(v):
        return "  -  " if (isinstance(v, float) and np.isnan(v)) else f"{v:.3f}"

    print(f"\n=== Per-class AP @ IoU {args.ap_iou:.2f} ===")
    print(f'{"id":>3}  {"name":<12}  {"AP":>5}  {"P":>5}  {"R":>5}  '
          f'{"#GT":>6}  {"#pred":>6}  {"mIoU_TP":>7}')
    print("-" * 64)
    valid_aps = []
    for c in range(args.num_classes):
        r = results[c]
        name = CLS_NAMES[c]
        print(f'{c:>3}  {name:<12}  {fmt(r["AP"]):>5}  {fmt(r["precision"]):>5}  '
              f'{fmt(r["recall"]):>5}  {r["num_gt"]:>6}  {r["num_pred"]:>6}  '
              f'{fmt(r["mean_iou_tp"]):>7}')
        if not (isinstance(r["AP"], float) and np.isnan(r["AP"])) and r["num_gt"] > 0:
            valid_aps.append(r["AP"])

    mean_ap = float(np.mean(valid_aps)) if valid_aps else 0.0

    # Overall mean IoU over all TPs (across all classes)
    all_ious = [iou for c in range(args.num_classes)
                for iou in (results[c].get("ious_tp") or [])
                if not np.isnan(iou)]
    overall_iou = float(np.mean(all_ious)) if all_ious else float("nan")

    print("-" * 64)
    print(f"\nmAP@{args.ap_iou}: {mean_ap:.3f}  "
          f"(over {len(valid_aps)}/{args.num_classes} classes with GTs)")
    print(f"overall mean IoU on TPs: {fmt(overall_iou)}  "
          f"(tightness proxy — higher = tighter boxes)")

    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)

        def _clean(d):
            return {k: (None if isinstance(v, float) and np.isnan(v)
                        else v) for k, v in d.items() if k != "ious_tp"}
        payload = {
            "checkpoint": str(args.checkpoint),
            "val_images": len(val_files),
            "ap_iou": args.ap_iou,
            "conf_threshold": args.conf_threshold,
            "nms_iou": args.nms_iou,
            "mAP": mean_ap,
            "overall_mean_iou_tp": (None if np.isnan(overall_iou) else overall_iou),
            "per_class": {CLS_NAMES[c]: _clean(results[c])
                          for c in range(args.num_classes)},
        }
        args.output.write_text(json.dumps(payload, indent=2))
        print(f"\nwrote {args.output}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
