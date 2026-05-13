"""TinyLayoutYOLO — multi-level anchor-free document layout detector.

Architecture (v2 — go-big revision):
  Backbone: MobileNetV3-Small features[0:9]
    └── intermediate at features[3]: stride  8, 24 ch → c3
    └── intermediate at features[8]: stride 16, 48 ch → c4
  Neck (mini-FPN):
    P4 = lateral_4(c4)                 # 48 → 128 ch
    P3 = lateral_3(c3) + upsample(P4)  # 24 → 128 ch + top-down add
    P3 = smooth_3(P3)                  # 3×3 BN-SiLU smoothing
  Heads (decoupled, separate weights per level):
    head_p3 : (cls, reg, obj) at stride 8   (small objects)
    head_p4 : (cls, reg, obj) at stride 16  (large objects)

Why the rewrite vs single-level v1:
  - The v1 stride-16 head has 16 px cell quantisation, which leaves up to
    ±8 px slack on box centres after assignment + NMS. That manifests as
    boxes that extend past the left edge of text — fatal for rail mode.
  - Stride-8 halves the quantisation. Combined with tighter centre-prior
    assignment (radius 0.5) and CIoU loss, target precision drops to a few
    pixels regardless of cell granularity.

State-dict compatibility with v1:
  - `MobileNetBackbone.features` is preserved as a single `nn.Sequential` so
    a v1 checkpoint's backbone weights load without renaming.
  - Heads change from `head.*` → `head_p3.*` + `head_p4.*`. The warm-start
    helper copies v1's `head.*` into both v2 heads. FPN gets fresh init.
"""

from __future__ import annotations

import torch
import torch.nn as nn
import torch.nn.functional as F
from torchvision.models import mobilenet_v3_small, MobileNet_V3_Small_Weights


# Feature stages we extract from MobileNetV3-Small features
P3_INDEX = 3      # output after features[3]: stride 8, 24 channels
P4_INDEX = 8      # output after features[8]: stride 16, 48 channels
C3_CH = 24
C4_CH = 48
FPN_CH = 128
HEAD_CH = 128

STRIDES = (8, 16)
STRIDE_P3 = 8
STRIDE_P4 = 16


def conv_bn_silu(in_ch: int, out_ch: int, k: int = 3, s: int = 1) -> nn.Sequential:
    p = (k - 1) // 2
    return nn.Sequential(
        nn.Conv2d(in_ch, out_ch, k, s, p, bias=False),
        nn.BatchNorm2d(out_ch),
        nn.SiLU(inplace=True),
    )


class MobileNetBackbone(nn.Module):
    """Wraps features[:9] but emits two intermediate feature maps.

    State-dict key `features.N.weight` is unchanged from v1, so v1 backbone
    weights load directly.
    """

    def __init__(self, pretrained: bool = True):
        super().__init__()
        weights = MobileNet_V3_Small_Weights.IMAGENET1K_V1 if pretrained else None
        base = mobilenet_v3_small(weights=weights)
        self.features = nn.Sequential(*list(base.features[:P4_INDEX + 1]))

    def forward(self, x: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
        c3 = None
        for i, layer in enumerate(self.features):
            x = layer(x)
            if i == P3_INDEX:
                c3 = x
        assert c3 is not None
        return c3, x   # c3 stride 8, c4 stride 16


class SimpleFPN(nn.Module):
    """2-level top-down FPN.

    P4 = lateral_4(c4) — channel projection, no neighbour to fuse
    P3 = lateral_3(c3) + upsample(P4) → smooth_3 → smoothed feature map
    """

    def __init__(self, in_ch_3: int = C3_CH, in_ch_4: int = C4_CH, out_ch: int = FPN_CH):
        super().__init__()
        # Lateral 1×1 convs project to common channel count
        self.lateral_3 = nn.Conv2d(in_ch_3, out_ch, kernel_size=1)
        self.lateral_4 = nn.Conv2d(in_ch_4, out_ch, kernel_size=1)
        # Smooth conv after the top-down add; reduces aliasing from upsample
        self.smooth_3 = conv_bn_silu(out_ch, out_ch, k=3)

    def forward(self, c3: torch.Tensor, c4: torch.Tensor
                ) -> tuple[torch.Tensor, torch.Tensor]:
        p4 = self.lateral_4(c4)
        # `align_corners` doesn't apply to nearest; nearest is exact and
        # ONNX-friendly for the 2× upsample needed here.
        up4 = F.interpolate(p4, scale_factor=2, mode="nearest")
        p3 = self.lateral_3(c3) + up4
        p3 = self.smooth_3(p3)
        return p3, p4


class RCM(nn.Module):
    """Rectangular Self-Calibration Module (Ni et al. 2024).

    Document-axial attention block: pool horizontally + vertically, mix the
    pooled features through banded convs, use as an attention map on a
    depthwise-3×3 content stream.

    Used in YOLO-DLA's PRDM-neck. Captures the column/row/heading axial
    structure that documents uniquely exhibit.

    This variant uses **depthwise** banded convs (1×k and k×1) rather than
    the paper's full convs, trading a small amount of axial-mixing capacity
    for a 64× parameter reduction. ~20K params per block.
    """

    def __init__(self, channels: int, kernel: int = 7):
        super().__init__()
        pad = kernel // 2
        # Axial banded depthwise convs over the pooled axial features
        self.axial_h = nn.Conv2d(channels, channels, kernel_size=(1, kernel),
                                 padding=(0, pad), groups=channels, bias=False)
        self.axial_bn = nn.BatchNorm2d(channels)
        self.axial_v = nn.Conv2d(channels, channels, kernel_size=(kernel, 1),
                                 padding=(pad, 0), groups=channels, bias=False)
        # Spatial content stream (depthwise 3×3 — preserves spatial detail)
        self.spatial = nn.Conv2d(channels, channels, kernel_size=3, padding=1,
                                 groups=channels, bias=False)
        self.spatial_bn = nn.BatchNorm2d(channels)
        # MLP after modulation: 1×1 conv mixes channels
        self.mlp = nn.Sequential(
            nn.Conv2d(channels, channels, kernel_size=1, bias=False),
            nn.BatchNorm2d(channels),
            nn.SiLU(inplace=True),
        )

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        # Pool each axis: H_P collapses height → (B, C, 1, W),
        #                 V_P collapses width  → (B, C, H, 1).
        # Broadcast-add gives an axial signature for every (h, w).
        h_pool = x.mean(dim=2, keepdim=True)   # (B, C, 1, W)
        v_pool = x.mean(dim=3, keepdim=True)   # (B, C, H, 1)
        axial = h_pool + v_pool                # (B, C, H, W) by broadcasting

        # Mix axial features through banded depthwise convs
        y = self.axial_h(axial)
        y = self.axial_bn(y)
        y = F.relu(y, inplace=True)
        y = self.axial_v(y)

        # Modulate content stream by axial-attention map
        core = self.spatial_bn(self.spatial(x))
        modulated = core * torch.sigmoid(y)

        return self.mlp(modulated) + x   # residual


class DecoupledHead(nn.Module):
    """Per-pixel decoupled head:
        cls_logits (B, num_classes, H, W)
        reg        (B, 4, H, W)        — (tx, ty, log_w, log_h)
        obj_logits (B, 1, H, W)

    The cls and reg branches each have a 2-conv trunk with BN+SiLU.
    """

    def __init__(self, in_ch: int, num_classes: int, head_ch: int = HEAD_CH):
        super().__init__()
        self.stem = conv_bn_silu(in_ch, head_ch, k=1)
        self.cls_trunk = nn.Sequential(
            conv_bn_silu(head_ch, head_ch, k=3),
            conv_bn_silu(head_ch, head_ch, k=3),
        )
        self.cls_pred = nn.Conv2d(head_ch, num_classes, kernel_size=1)
        self.reg_trunk = nn.Sequential(
            conv_bn_silu(head_ch, head_ch, k=3),
            conv_bn_silu(head_ch, head_ch, k=3),
        )
        self.reg_pred = nn.Conv2d(head_ch, 4, kernel_size=1)
        self.obj_pred = nn.Conv2d(head_ch, 1, kernel_size=1)

        # Prior bias init (RetinaNet trick): start cls/obj logits at -log(99)
        # so that early training has a low background-firing rate. Stabilises
        # the focal/BCE losses in the first few hundred steps.
        prior_prob = 0.01
        bias_value = -float(torch.log(torch.tensor((1.0 - prior_prob) / prior_prob)))
        nn.init.constant_(self.cls_pred.bias, bias_value)
        nn.init.constant_(self.obj_pred.bias, bias_value)

    def forward(self, x: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor, torch.Tensor]:
        feat = self.stem(x)
        c = self.cls_trunk(feat)
        r = self.reg_trunk(feat)
        return self.cls_pred(c), self.reg_pred(r), self.obj_pred(r)


class TinyLayoutYOLO(nn.Module):
    """Multi-level anchor-free detector.

    forward returns:
        {
            'p3': {'cls': ..., 'reg': ..., 'obj': ...},   # stride 8
            'p4': {'cls': ..., 'reg': ..., 'obj': ...},   # stride 16
        }
    """

    def __init__(self, num_classes: int, pretrained: bool = True,
                 use_rcm: bool = True):
        super().__init__()
        self.num_classes = num_classes
        self.strides = STRIDES
        self.use_rcm = use_rcm
        self.backbone = MobileNetBackbone(pretrained=pretrained)
        self.fpn = SimpleFPN(in_ch_3=C3_CH, in_ch_4=C4_CH, out_ch=FPN_CH)
        # v5: document-axial attention before each head
        if use_rcm:
            self.rcm_p3 = RCM(FPN_CH)
            self.rcm_p4 = RCM(FPN_CH)
        self.head_p3 = DecoupledHead(FPN_CH, num_classes, head_ch=HEAD_CH)
        self.head_p4 = DecoupledHead(FPN_CH, num_classes, head_ch=HEAD_CH)

    def forward(self, x: torch.Tensor) -> dict[str, dict[str, torch.Tensor]]:
        c3, c4 = self.backbone(x)
        p3, p4 = self.fpn(c3, c4)
        if self.use_rcm:
            p3 = self.rcm_p3(p3)
            p4 = self.rcm_p4(p4)
        cls_3, reg_3, obj_3 = self.head_p3(p3)
        cls_4, reg_4, obj_4 = self.head_p4(p4)
        return {
            "p3": {"cls": cls_3, "reg": reg_3, "obj": obj_3},
            "p4": {"cls": cls_4, "reg": reg_4, "obj": obj_4},
        }


def build_model(num_classes: int = 16, pretrained: bool = True) -> TinyLayoutYOLO:
    return TinyLayoutYOLO(num_classes=num_classes, pretrained=pretrained)


def warmstart_from_v1(model: TinyLayoutYOLO, prior_state: dict) -> dict[str, int]:
    """Load a prior checkpoint into the current model.

    Handles two cases:
      * **Same architecture** (e.g. v2 → v4): any key with matching name +
        shape loads directly. Backbone, FPN, both heads — everything that's
        unchanged transfers cleanly.
      * **v1 → v2 transition** (single → multi-level head): v1's `head.*`
        keys copy into BOTH `head_p3.*` and `head_p4.*`. Same backbone
        state-dict structure preserves backbone weights either way.

    Anything still uninitialised after both passes counts as "fresh"
    (typically just FPN keys when warm-starting from a v1 checkpoint).

    Returns {category: count} for logging.
    """
    new_state = model.state_dict()
    loaded = {"direct": 0, "head_p3_v1": 0, "head_p4_v1": 0,
              "fresh": 0, "skipped_shape": 0}
    matched_keys: set[str] = set()

    # Pass 1: direct name + shape match (covers same-architecture warm-start).
    for k, v in prior_state.items():
        if k in new_state and new_state[k].shape == v.shape:
            new_state[k] = v
            matched_keys.add(k)
            loaded["direct"] += 1

    # Pass 2: legacy v1 → v2 head copy.
    for k, v in prior_state.items():
        if not k.startswith("head."):
            continue
        if k in matched_keys:
            continue  # already loaded directly
        tail = k[len("head."):]
        for prefix, bucket in (("head_p3.", "head_p3_v1"),
                               ("head_p4.", "head_p4_v1")):
            nk = prefix + tail
            if nk in new_state and nk not in matched_keys \
               and new_state[nk].shape == v.shape:
                new_state[nk] = v
                matched_keys.add(nk)
                loaded[bucket] += 1

    # Count anything still un-matched as fresh.
    for k in new_state:
        if k not in matched_keys:
            loaded["fresh"] += 1

    # Count source keys that we couldn't place (shape mismatch, dropped from
    # the architecture, etc.)
    for k, v in prior_state.items():
        if k in matched_keys:
            continue
        if k.startswith("head."):
            tail = k[len("head."):]
            if any((p + tail) in matched_keys for p in ("head_p3.", "head_p4.")):
                continue
        loaded["skipped_shape"] += 1

    model.load_state_dict(new_state)
    return loaded


if __name__ == "__main__":
    m = build_model(num_classes=16, pretrained=False)
    n = sum(p.numel() for p in m.parameters() if p.requires_grad)
    print(f"TinyLayoutYOLO v2: {n / 1e6:.2f}M params")
    x = torch.zeros(2, 3, 480, 480)
    out = m(x)
    for level, d in out.items():
        for k, v in d.items():
            print(f"  {level}.{k}: {tuple(v.shape)}")
