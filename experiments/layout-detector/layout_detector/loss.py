"""Multi-level loss with CIoU regression + multi-positive assignment.

v2/v4 baseline: multi-level (P3/P4 size-routed), CIoU regression, tight
assignment (centre_radius=0.5 → ~1 positive cell per GT), high reg weight
(15.0).

v5 (pared back): adds multi-positive top-k=3 assignment. For each GT, the
top-K eligible cells closest to the GT centre are all marked positive
(instead of just the single nearest). centre_radius is widened to 1.5
because top-k=3 only multi-positions if the eligibility window is wider
than ~1 cell — at 0.5, top-k=3 reduces to top-1 and the change is inert.
Conflict resolution (two GTs claiming the same cell) still favours the
smallest-area GT.
"""

from __future__ import annotations

import math

import torch
import torch.nn as nn
import torch.nn.functional as F


# v5 (pared back): only RCM + multi-positive top-k=3 are kept from the
# original four-change design. Soft-objectness and the centre-radius
# schedule were dropped to keep changes attributable.
# Note: top-k=3 only multi-positions if the eligibility window is wider
# than ~1 cell. v4's 0.5 effectively reduces top-k to top-1. v5 uses 1.5
# (matches what the schedule's max would have been) so top-k=3 actually
# engages — 4-7 cells eligible per GT, top-3 chosen.
CENTRE_RADIUS = 1.5
TOP_K_POSITIVES = 3
SIZE_ROUTE_THRESHOLD_PX = 64.0
CLS_WEIGHT = 1.0
OBJ_WEIGHT = 1.0
REG_WEIGHT = 15.0
FOCAL_ALPHA = 0.25
FOCAL_GAMMA = 2.0


def _grid_centres(H: int, W: int, stride: int, device, dtype):
    yy, xx = torch.meshgrid(
        torch.arange(H, device=device, dtype=dtype),
        torch.arange(W, device=device, dtype=dtype),
        indexing="ij",
    )
    cx = (xx + 0.5) * stride
    cy = (yy + 0.5) * stride
    return cx, cy   # each (H, W)


def _iou_xyxy(boxes_a: torch.Tensor, boxes_b: torch.Tensor) -> torch.Tensor:
    """Plain IoU between matched xyxy pairs. Both (N, 4) in pixel space.

    Used as the soft-obj target — the obj head learns to predict *how
    well-aligned* the box turned out, not just whether the cell is a
    positive. Caller should `.detach()` the result so obj loss doesn't
    backprop into reg.
    """
    ax1, ay1, ax2, ay2 = boxes_a.unbind(-1)
    bx1, by1, bx2, by2 = boxes_b.unbind(-1)
    inter_x1 = torch.maximum(ax1, bx1)
    inter_y1 = torch.maximum(ay1, by1)
    inter_x2 = torch.minimum(ax2, bx2)
    inter_y2 = torch.minimum(ay2, by2)
    inter = (inter_x2 - inter_x1).clamp(min=0) * (inter_y2 - inter_y1).clamp(min=0)
    area_a = (ax2 - ax1).clamp(min=0) * (ay2 - ay1).clamp(min=0)
    area_b = (bx2 - bx1).clamp(min=0) * (by2 - by1).clamp(min=0)
    return inter / (area_a + area_b - inter + 1e-7)


def _ciou(boxes_a: torch.Tensor, boxes_b: torch.Tensor) -> torch.Tensor:
    """CIoU between matched xyxy pairs. Both (N, 4) in pixel space.

    CIoU = IoU − ρ²(centre_a, centre_b) / c² − α·v
      where c² is the squared enclosing-box diagonal,
            v penalises aspect-ratio mismatch,
            α = v / (1 − IoU + v)
    """
    # Box edges
    ax1, ay1, ax2, ay2 = boxes_a.unbind(-1)
    bx1, by1, bx2, by2 = boxes_b.unbind(-1)

    # IoU
    inter_x1 = torch.maximum(ax1, bx1)
    inter_y1 = torch.maximum(ay1, by1)
    inter_x2 = torch.minimum(ax2, bx2)
    inter_y2 = torch.minimum(ay2, by2)
    inter = (inter_x2 - inter_x1).clamp(min=0) * (inter_y2 - inter_y1).clamp(min=0)

    w_a = (ax2 - ax1).clamp(min=0); h_a = (ay2 - ay1).clamp(min=0)
    w_b = (bx2 - bx1).clamp(min=0); h_b = (by2 - by1).clamp(min=0)
    area_a = w_a * h_a
    area_b = w_b * h_b
    union = area_a + area_b - inter + 1e-7
    iou = inter / union

    # Enclosing-box diagonal squared
    enc_x1 = torch.minimum(ax1, bx1)
    enc_y1 = torch.minimum(ay1, by1)
    enc_x2 = torch.maximum(ax2, bx2)
    enc_y2 = torch.maximum(ay2, by2)
    c2 = (enc_x2 - enc_x1).pow(2) + (enc_y2 - enc_y1).pow(2) + 1e-7

    # Centre distance squared
    cx_a = (ax1 + ax2) / 2
    cy_a = (ay1 + ay2) / 2
    cx_b = (bx1 + bx2) / 2
    cy_b = (by1 + by2) / 2
    rho2 = (cx_a - cx_b).pow(2) + (cy_a - cy_b).pow(2)

    # Aspect-ratio penalty
    pi_sq = math.pi ** 2
    v = (4.0 / pi_sq) * (torch.atan(w_b / (h_b + 1e-7))
                        - torch.atan(w_a / (h_a + 1e-7))).pow(2)
    # alpha treats v as constant (no gradient through alpha) — standard CIoU trick
    with torch.no_grad():
        alpha = v / (1.0 - iou + v + 1e-7)

    return iou - rho2 / c2 - alpha * v


class TinyYoloLoss(nn.Module):
    def __init__(self, num_classes: int, input_size: int = 480,
                 strides: tuple[int, int] = (8, 16),
                 cls_weight: float = CLS_WEIGHT,
                 obj_weight: float = OBJ_WEIGHT,
                 reg_weight: float = REG_WEIGHT,
                 focal_alpha: float = FOCAL_ALPHA,
                 focal_gamma: float = FOCAL_GAMMA,
                 centre_radius: float = CENTRE_RADIUS,
                 size_route_threshold_px: float = SIZE_ROUTE_THRESHOLD_PX,
                 class_weights: list[float] | None = None,
                 top_k_positives: int = TOP_K_POSITIVES,
                 soft_obj: bool = False):
        super().__init__()
        self.num_classes = num_classes
        self.input_size = input_size
        self.strides = strides
        self.cls_weight = cls_weight
        self.obj_weight = obj_weight
        self.reg_weight = reg_weight
        self.alpha = focal_alpha
        self.gamma = focal_gamma
        self.centre_radius = centre_radius
        self.size_route_threshold_px = size_route_threshold_px
        self.top_k_positives = top_k_positives
        self.soft_obj = soft_obj
        # Per-class focal-loss weights (inverse-frequency-sqrt by default).
        # None → uniform weights of 1.0. Length must equal num_classes.
        if class_weights is None:
            cw = torch.ones(num_classes, dtype=torch.float32)
        else:
            assert len(class_weights) == num_classes, \
                f"class_weights length {len(class_weights)} != num_classes {num_classes}"
            cw = torch.tensor(class_weights, dtype=torch.float32)
        self.register_buffer("class_weights", cw)

    def forward(self, out: dict, gt_labels: list[torch.Tensor]) -> dict[str, torch.Tensor]:
        """
        out: {'p3': {'cls', 'reg', 'obj'}, 'p4': {'cls', 'reg', 'obj'}}
              cls (B, C, H, W) / reg (B, 4, H, W) / obj (B, 1, H, W)
        gt_labels: list of B tensors, each (N_i, 5) = [cls, cx, cy, w, h] in [0,1]
        """
        S = self.input_size

        # Per-image GT routing by size (in pixel space)
        gt_p3: list[torch.Tensor] = []
        gt_p4: list[torch.Tensor] = []
        for gts in gt_labels:
            if gts.numel() == 0:
                gt_p3.append(gts)
                gt_p4.append(gts)
                continue
            max_side = torch.maximum(gts[:, 3] * S, gts[:, 4] * S)
            small = max_side < self.size_route_threshold_px
            gt_p3.append(gts[small])
            gt_p4.append(gts[~small])

        # Per-level loss
        l3 = self._level_loss(out["p3"], gt_p3, stride=self.strides[0])
        l4 = self._level_loss(out["p4"], gt_p4, stride=self.strides[1])

        # Aggregate — normalise per-component by total positives so a level
        # with no positives doesn't get a free pass on obj.
        cls_total = (l3["cls_sum"] + l4["cls_sum"])
        obj_total = (l3["obj_sum"] + l4["obj_sum"])
        reg_total = (l3["reg_sum"] + l4["reg_sum"])
        num_pos = max(l3["num_pos"] + l4["num_pos"], 1)

        cls_loss = cls_total / num_pos
        obj_loss = obj_total / num_pos
        reg_loss = reg_total / num_pos
        total = (self.cls_weight * cls_loss
                 + self.obj_weight * obj_loss
                 + self.reg_weight * reg_loss)
        return {
            "total": total,
            "cls": cls_loss.detach(),
            "obj": obj_loss.detach(),
            "reg": reg_loss.detach(),
            "num_pos": torch.tensor(num_pos, dtype=torch.float32),
            "num_pos_p3": torch.tensor(l3["num_pos"], dtype=torch.float32),
            "num_pos_p4": torch.tensor(l4["num_pos"], dtype=torch.float32),
        }

    def _level_loss(self, level_out: dict[str, torch.Tensor],
                    gt_per_image: list[torch.Tensor], stride: int) -> dict:
        """Compute SUMS (not means) of cls / obj / reg loss + positive count
        for one level. The caller divides by the total positives across levels.

        All maths runs in fp32 (predictions are upcast) — pixel-area math at
        480² overflows fp16.
        """
        centre_radius = self.centre_radius
        cls_logits = level_out["cls"].float()
        reg = level_out["reg"].float()
        obj_logits = level_out["obj"].float()
        B, C, H, W = cls_logits.shape
        device = cls_logits.device
        S = self.input_size

        # Cell-centre coordinates in pixel space
        gcx, gcy = _grid_centres(H, W, stride, device, cls_logits.dtype)
        gcx_flat = gcx.reshape(-1)
        gcy_flat = gcy.reshape(-1)
        cell_x = torch.arange(W, device=device, dtype=cls_logits.dtype).repeat(H)
        cell_y = torch.arange(H, device=device, dtype=cls_logits.dtype).repeat_interleave(W)

        # Flatten predictions: (B, HW, ...)
        cls_p = cls_logits.permute(0, 2, 3, 1).reshape(B, -1, C)
        reg_p = reg.permute(0, 2, 3, 1).reshape(B, -1, 4)
        obj_p = obj_logits.permute(0, 2, 3, 1).reshape(B, -1, 1).squeeze(-1)

        # Decoded predicted boxes (xyxy in pixel space) — needed for CIoU
        tx = reg_p[..., 0]; ty = reg_p[..., 1]
        lw = reg_p[..., 2]; lh = reg_p[..., 3]
        pcx = (cell_x.unsqueeze(0) + torch.sigmoid(tx)) * stride
        pcy = (cell_y.unsqueeze(0) + torch.sigmoid(ty)) * stride
        pw = torch.exp(lw) * stride
        ph = torch.exp(lh) * stride
        pred_xyxy = torch.stack([pcx - pw / 2, pcy - ph / 2,
                                 pcx + pw / 2, pcy + ph / 2], dim=-1)  # (B, HW, 4)

        obj_target = torch.zeros(B, H * W, device=device, dtype=cls_logits.dtype)
        cls_target = torch.zeros(B, H * W, C, device=device, dtype=cls_logits.dtype)
        reg_gt_xyxy = torch.zeros(B, H * W, 4, device=device, dtype=cls_logits.dtype)
        pos_mask = torch.zeros(B, H * W, device=device, dtype=torch.bool)

        num_pos = 0
        for b in range(B):
            gts = gt_per_image[b]
            if gts.numel() == 0:
                continue
            gts = gts.to(device=device, dtype=cls_logits.dtype)
            cls_idx = gts[:, 0].long()
            cx = gts[:, 1] * S
            cy = gts[:, 2] * S
            gw = gts[:, 3] * S
            gh = gts[:, 4] * S
            gt_x1 = cx - gw / 2; gt_y1 = cy - gh / 2
            gt_x2 = cx + gw / 2; gt_y2 = cy + gh / 2

            inside_box = (
                (gcx_flat.unsqueeze(1) >= gt_x1.unsqueeze(0))
                & (gcx_flat.unsqueeze(1) <= gt_x2.unsqueeze(0))
                & (gcy_flat.unsqueeze(1) >= gt_y1.unsqueeze(0))
                & (gcy_flat.unsqueeze(1) <= gt_y2.unsqueeze(0))
            )
            r = centre_radius * stride
            close_to_centre = (
                (gcx_flat.unsqueeze(1) >= (cx - r).unsqueeze(0))
                & (gcx_flat.unsqueeze(1) <= (cx + r).unsqueeze(0))
                & (gcy_flat.unsqueeze(1) >= (cy - r).unsqueeze(0))
                & (gcy_flat.unsqueeze(1) <= (cy + r).unsqueeze(0))
            )
            candidate = inside_box & close_to_centre   # (HW, N_gt)

            # Fallback for GTs that have NO eligible cell (rare GTs at tight
            # radius straddling cell boundaries): widen to anything inside.
            empty_gts = ~candidate.any(dim=0)          # (N_gt,)
            if empty_gts.any():
                candidate[:, empty_gts] = inside_box[:, empty_gts]

            if not candidate.any():
                continue

            # ===== Multi-positive top-k assignment (v5) =====
            # For each GT, pick the top-K eligible cells closest to GT centre.
            # Then resolve cell conflicts (multiple GTs claiming same cell) by
            # smallest-area GT.
            #
            # Distance metric: squared L2 in pixel space.
            dx = gcx_flat.unsqueeze(1) - cx.unsqueeze(0)   # (HW, N_gt)
            dy = gcy_flat.unsqueeze(1) - cy.unsqueeze(0)
            sq_dist = dx * dx + dy * dy
            # For non-candidate cells, set distance to inf so they never win top-k
            sq_dist = torch.where(candidate, sq_dist,
                                   torch.full_like(sq_dist, float("inf")))

            # selected: (HW, N_gt) bool — True if cell is in top-K for that GT
            num_gts = candidate.shape[1]
            k = min(self.top_k_positives, candidate.shape[0])
            # Per-GT top-k: torch.topk along dim=0 (over cells), smallest=k
            # Use float32 to be AMP-safe
            sq_dist_f32 = sq_dist.float()
            topk_vals, topk_idx = torch.topk(sq_dist_f32, k=k, dim=0,
                                              largest=False)
            # Build selected mask. A topk entry is real iff its distance < inf
            selected = torch.zeros_like(candidate)
            for gt_i in range(num_gts):
                valid = topk_vals[:, gt_i] < float("inf")
                if valid.any():
                    selected[topk_idx[:, gt_i][valid], gt_i] = True

            if not selected.any():
                continue

            # Resolve cell conflicts: when a cell is selected for multiple GTs,
            # pick the smallest-area GT (matches v2/v4 behaviour).
            areas = (gw * gh).float()
            scored = torch.where(
                selected,
                areas.unsqueeze(0).expand_as(selected).float(),
                torch.full_like(selected, float("inf"), dtype=torch.float32),
            )
            best_gt = scored.argmin(dim=1)             # (HW,) which GT each cell is assigned to
            has_any = selected.any(dim=1)              # (HW,) is this cell a positive?
            pos_idx = has_any.nonzero(as_tuple=True)[0]
            if pos_idx.numel() == 0:
                continue
            pos_gt = best_gt[pos_idx]

            # Targets for positives
            cls_target[b, pos_idx, cls_idx[pos_gt]] = 1.0
            gt_pos_x1 = gt_x1[pos_gt]
            gt_pos_y1 = gt_y1[pos_gt]
            gt_pos_x2 = gt_x2[pos_gt]
            gt_pos_y2 = gt_y2[pos_gt]
            reg_gt_xyxy[b, pos_idx, 0] = gt_pos_x1
            reg_gt_xyxy[b, pos_idx, 1] = gt_pos_y1
            reg_gt_xyxy[b, pos_idx, 2] = gt_pos_x2
            reg_gt_xyxy[b, pos_idx, 3] = gt_pos_y2
            pos_mask[b, pos_idx] = True

            if self.soft_obj:
                # Soft-obj target: detached IoU between predicted box at this
                # positive cell and its assigned GT. Model learns to report
                # how well-aligned the box turned out, not just whether the
                # cell is a positive. Detached so obj loss doesn't push reg.
                pred_pos = pred_xyxy[b, pos_idx].detach()
                gt_pos_xyxy = torch.stack(
                    [gt_pos_x1, gt_pos_y1, gt_pos_x2, gt_pos_y2], dim=-1
                )
                iou = _iou_xyxy(pred_pos, gt_pos_xyxy).clamp(0.0, 1.0)
                obj_target[b, pos_idx] = iou
            else:
                obj_target[b, pos_idx] = 1.0

            num_pos += pos_idx.numel()

        # --- Losses ---
        # Objectness: BCE everywhere, SUM (caller divides)
        obj_sum = F.binary_cross_entropy_with_logits(
            obj_p, obj_target, reduction="sum"
        )

        # Classification: focal BCE on positive cells only, SUM
        # Per-class weights applied element-wise: column c of focal is scaled
        # by class_weights[c]. This boosts both "fire-when-class-c" learning
        # for rare classes (col c on positives where cls_t==1) and
        # "don't-fire-when-not-c" learning (col c on cells where cls_t==0).
        if pos_mask.any():
            cls_p_pos = cls_p[pos_mask]
            cls_t_pos = cls_target[pos_mask]
            p = torch.sigmoid(cls_p_pos)
            ce = F.binary_cross_entropy_with_logits(cls_p_pos, cls_t_pos, reduction="none")
            p_t = p * cls_t_pos + (1 - p) * (1 - cls_t_pos)
            alpha_t = self.alpha * cls_t_pos + (1 - self.alpha) * (1 - cls_t_pos)
            focal = alpha_t * (1 - p_t).pow(self.gamma) * ce
            focal = focal * self.class_weights.unsqueeze(0).to(focal.device)
            cls_sum = focal.sum()
        else:
            cls_sum = cls_p.sum() * 0.0  # zero w/ graph

        # Regression: 1 − CIoU on positives, SUM
        if pos_mask.any():
            pred_pos = pred_xyxy[pos_mask]
            gt_pos = reg_gt_xyxy[pos_mask]
            ciou = _ciou(pred_pos, gt_pos)
            reg_sum = (1.0 - ciou).sum()
        else:
            reg_sum = reg.sum() * 0.0

        return {
            "cls_sum": cls_sum,
            "obj_sum": obj_sum,
            "reg_sum": reg_sum,
            "num_pos": num_pos,
        }
