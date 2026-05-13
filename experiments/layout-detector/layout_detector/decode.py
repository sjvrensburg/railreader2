"""Decoding multi-level head outputs to (x, y, w, h, conf, cls) predictions.

Each level emits per-cell:
    cls_logits ∈ (B, C, H, W)
    reg        ∈ (B, 4, H, W)   [tx, ty, log_w, log_h]
    obj_logits ∈ (B, 1, H, W)

Decoding:
    x_centre = (grid_x + sigmoid(tx)) * stride
    y_centre = (grid_y + sigmoid(ty)) * stride
    w        = exp(log_w) * stride
    h        = exp(log_h) * stride
    conf     = sigmoid(obj) * sigmoid(cls_top)
    cls      = argmax(cls_logits)

Boxes from both levels are concatenated and returned together — NMS happens
post-decode in batched_nms.
"""

from __future__ import annotations

import torch
import torchvision


STRIDES = (8, 16)


def _grid(h: int, w: int, device, dtype):
    yy, xx = torch.meshgrid(
        torch.arange(h, device=device, dtype=dtype),
        torch.arange(w, device=device, dtype=dtype),
        indexing="ij",
    )
    return xx, yy


def _decode_level(out_level: dict[str, torch.Tensor], stride: int
                  ) -> tuple[torch.Tensor, torch.Tensor, torch.Tensor]:
    cls = out_level["cls"]
    reg = out_level["reg"]
    obj = out_level["obj"]

    B, C, H, W = cls.shape
    xx, yy = _grid(H, W, device=cls.device, dtype=cls.dtype)

    tx = reg[:, 0]; ty = reg[:, 1]
    lw = reg[:, 2]; lh = reg[:, 3]
    cx = (xx + torch.sigmoid(tx)) * stride
    cy = (yy + torch.sigmoid(ty)) * stride
    bw = torch.exp(lw) * stride
    bh = torch.exp(lh) * stride

    x1 = cx - bw / 2
    y1 = cy - bh / 2
    x2 = cx + bw / 2
    y2 = cy + bh / 2
    boxes = torch.stack([x1, y1, x2, y2], dim=1)        # (B, 4, H, W)
    boxes = boxes.permute(0, 2, 3, 1).reshape(B, -1, 4)  # (B, HW, 4)

    obj_p = torch.sigmoid(obj).reshape(B, -1)
    cls_p = torch.sigmoid(cls)
    cls_p = cls_p.permute(0, 2, 3, 1).reshape(B, -1, C)
    top_score, top_label = cls_p.max(dim=2)
    scores = obj_p * top_score
    return boxes, scores, top_label


def decode_predictions(out: dict[str, dict[str, torch.Tensor]],
                       strides: tuple[int, int] = STRIDES
                       ) -> tuple[torch.Tensor, torch.Tensor, torch.Tensor]:
    """Decode both levels and concatenate. Returns:
        boxes_xyxy: (B, N_total, 4) in image pixels
        scores:    (B, N_total)
        labels:    (B, N_total) int64
    where N_total = N_p3 + N_p4 (3600 + 900 = 4500 at 480 input).
    """
    b3, s3, l3 = _decode_level(out["p3"], strides[0])
    b4, s4, l4 = _decode_level(out["p4"], strides[1])
    return (
        torch.cat([b3, b4], dim=1),
        torch.cat([s3, s4], dim=1),
        torch.cat([l3, l4], dim=1),
    )


def batched_nms(boxes: torch.Tensor, scores: torch.Tensor, labels: torch.Tensor,
                score_threshold: float = 0.25, iou_threshold: float = 0.5,
                max_det: int = 300) -> list[dict[str, torch.Tensor]]:
    """Per-image class-agnostic NMS across both levels.

    Returns a list of dicts with keys 'boxes', 'scores', 'labels'.
    """
    B = boxes.shape[0]
    results = []
    for i in range(B):
        s = scores[i]
        mask = s >= score_threshold
        if not mask.any():
            results.append({
                "boxes": boxes.new_zeros((0, 4)),
                "scores": s.new_zeros((0,)),
                "labels": labels.new_zeros((0,)),
            })
            continue
        b = boxes[i][mask]
        sc = s[mask]
        lb = labels[i][mask]
        keep = torchvision.ops.batched_nms(b, sc, lb, iou_threshold)[:max_det]
        results.append({"boxes": b[keep], "scores": sc[keep], "labels": lb[keep]})
    return results
