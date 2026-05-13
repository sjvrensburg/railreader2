"""TinyLayoutYOLO inference wrapper for the order-validation harness.

Analogue of yolo.py for the YOLO26 model, but adapted to our custom
TinyLayoutYOLO exported by experiments/layout-detector/export_onnx.py.

Output format from our ONNX is identical in shape semantics to YOLO26's
end-to-end output (`[1, N, 6] = [x1, y1, x2, y2, conf, cls]`) but NMS is
not embedded — we apply class-agnostic NMS here in Python.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

import numpy as np
import onnxruntime as ort
import torch
import torchvision
from PIL import Image


INPUT_SIZE = 480
CONF_THRESHOLD = 0.25
NMS_IOU_THRESHOLD = 0.5
LETTERBOX_PAD = 114

# Mirror of LayoutConstants.LayoutClasses — the corrected runtime mapping
# (training-id → runtime-idx remap is already baked into the trained model
# because we applied it in YoloDataset.__getitem__).
TINY_CLASSES = [
    "t", "t1", "t2", "t3", "paragraph", "author", "keyword", "abstract",
    "reference", "graph", "note", "other", "formula", "table", "footnote",
    "class17",
]


@dataclass
class YoloBox:
    """Detection in normalised page coordinates [0, 1]. Compatible with
    xy_cut.sort and the rest of the validation harness."""
    cls: int
    cls_name: str
    conf: float
    x: float       # left
    y: float       # top
    w: float
    h: float
    order: int = -1


def load_model(model_path: Path,
               providers: list[str] | None = None) -> ort.InferenceSession:
    so = ort.SessionOptions()
    so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    so.log_severity_level = 3
    # Default to CPU — validation should not fight the GPU if training is
    # still running.
    providers = providers or ["CPUExecutionProvider"]
    return ort.InferenceSession(str(model_path), sess_options=so, providers=providers)


def _preprocess(image: Image.Image) -> tuple[np.ndarray, int, int, int, int]:
    """Letterbox to 480×480 with grey 114 padding, RGB, /255.
    Returns CHW tensor + (resized_w, resized_h, pad_x, pad_y) for coord unprojection."""
    w, h = image.size
    scale = INPUT_SIZE / max(w, h)
    new_w, new_h = int(round(w * scale)), int(round(h * scale))
    resized = image.resize((new_w, new_h), Image.BILINEAR)
    pad_x = (INPUT_SIZE - new_w) // 2
    pad_y = (INPUT_SIZE - new_h) // 2
    canvas = Image.new("RGB", (INPUT_SIZE, INPUT_SIZE),
                       (LETTERBOX_PAD, LETTERBOX_PAD, LETTERBOX_PAD))
    canvas.paste(resized, (pad_x, pad_y))
    arr = np.asarray(canvas, dtype=np.float32) / 255.0
    chw = np.transpose(arr, (2, 0, 1))[None]
    return chw, new_w, new_h, pad_x, pad_y


def run(session: ort.InferenceSession, image: Image.Image,
        conf_threshold: float = CONF_THRESHOLD,
        nms_iou: float = NMS_IOU_THRESHOLD) -> list[YoloBox]:
    """Run TinyLayoutYOLO + apply conf threshold + class-agnostic NMS.
    Returns unordered detections in [0, 1] page coordinates."""
    chw, new_w, new_h, pad_x, pad_y = _preprocess(image)
    out = session.run(None, {"images": chw})[0][0]   # (N, 6)

    # Filter by confidence
    mask = out[:, 4] >= conf_threshold
    det = out[mask]
    if det.shape[0] == 0:
        return []

    # Class-agnostic NMS in 480-space
    bx = torch.from_numpy(det[:, :4].astype(np.float32))
    sc = torch.from_numpy(det[:, 4].astype(np.float32))
    keep = torchvision.ops.nms(bx, sc, nms_iou).numpy()
    det = det[keep]

    # Unproject from letterbox 480-space → original image pixels → normalised
    img_w, img_h = image.size
    out_boxes: list[YoloBox] = []
    for row in det:
        x1, y1, x2, y2, conf, cls = row
        # Unproject
        ux1 = (x1 - pad_x) * img_w / max(1, new_w)
        uy1 = (y1 - pad_y) * img_h / max(1, new_h)
        ux2 = (x2 - pad_x) * img_w / max(1, new_w)
        uy2 = (y2 - pad_y) * img_h / max(1, new_h)
        ux1 = max(0.0, ux1); uy1 = max(0.0, uy1)
        ux2 = min(float(img_w), ux2); uy2 = min(float(img_h), uy2)
        w = ux2 - ux1
        h = uy2 - uy1
        if w < 5 or h < 5:
            continue
        cls_idx = int(cls)
        cls_name = (TINY_CLASSES[cls_idx]
                    if 0 <= cls_idx < len(TINY_CLASSES) else f"cls_{cls_idx}")
        out_boxes.append(YoloBox(
            cls=cls_idx, cls_name=cls_name, conf=float(conf),
            x=ux1 / img_w, y=uy1 / img_h,
            w=w / img_w, h=h / img_h,
        ))
    return out_boxes
