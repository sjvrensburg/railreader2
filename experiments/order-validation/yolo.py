"""YOLO26-DLA inference wrapper.

Mirrors the C# LayoutAnalyzer's preprocessing + decoding so the Python
validation harness sees exactly what RailReader2 sees at runtime.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

import numpy as np
import onnxruntime as ort
from PIL import Image


INPUT_SIZE = 640
CONF_THRESHOLD = 0.25
LETTERBOX_PAD = 114

# Mirror of LayoutConstants.LayoutClasses (corrected runtime mapping after
# the ONNX class-shift discovery — see LayoutConstants.cs for the full story).
YOLO_CLASSES = [
    "t", "t1", "t2", "t3", "paragraph", "author", "keyword", "abstract",
    "reference", "graph", "note", "other", "formula", "table", "footnote",
    "class17",
]


@dataclass
class YoloBox:
    """YOLO detection in normalised page coordinates [0, 1]."""
    cls: int
    cls_name: str
    conf: float
    x: float       # left
    y: float       # top
    w: float
    h: float
    order: int = -1  # filled in by XY-cut


def load_yolo(model_path: Path,
              providers: list[str] | None = None) -> ort.InferenceSession:
    so = ort.SessionOptions()
    so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    so.log_severity_level = 3
    providers = providers or ort.get_available_providers()
    return ort.InferenceSession(str(model_path), sess_options=so, providers=providers)


def _preprocess(image: Image.Image) -> tuple[np.ndarray, int, int, int, int]:
    """Ultralytics letterbox: scale-preserving fit into 640x640 with grey (114)
    padding, RGB, divide by 255. Returns CHW tensor + (pxW, pxH, padX, padY)
    for coordinate unprojection."""
    w, h = image.size
    scale = INPUT_SIZE / max(w, h)
    new_w, new_h = int(round(w * scale)), int(round(h * scale))
    resized = image.resize((new_w, new_h), Image.BILINEAR)

    pad_x = (INPUT_SIZE - new_w) // 2
    pad_y = (INPUT_SIZE - new_h) // 2

    canvas = Image.new("RGB", (INPUT_SIZE, INPUT_SIZE),
                       (LETTERBOX_PAD, LETTERBOX_PAD, LETTERBOX_PAD))
    canvas.paste(resized, (pad_x, pad_y))
    arr = np.asarray(canvas, dtype=np.float32) / 255.0  # HWC
    chw = np.transpose(arr, (2, 0, 1))                  # CHW
    return chw[None], new_w, new_h, pad_x, pad_y


def run(session: ort.InferenceSession, image: Image.Image,
        conf_threshold: float = CONF_THRESHOLD) -> list[YoloBox]:
    """Run YOLO inference. Returns unordered detections (no reading order)."""
    chw, new_w, new_h, pad_x, pad_y = _preprocess(image)
    outputs = session.run(None, {"images": chw})

    # YOLO26 end-to-end: [1, max_det, 6] = [x1, y1, x2, y2, conf, cls]
    det = None
    for o in outputs:
        if o.ndim == 3 and o.shape[0] == 1 and o.shape[2] >= 6:
            det = o[0]
            break
    if det is None:
        return []

    img_w, img_h = image.size
    boxes: list[YoloBox] = []
    for row in det:
        x1, y1, x2, y2, conf, cls = row[:6]
        if conf < conf_threshold:
            continue
        # Unproject from 640-space (with padding offset) → original pixels →
        # normalised [0, 1].
        ux1 = (x1 - pad_x) * img_w / new_w
        uy1 = (y1 - pad_y) * img_h / new_h
        ux2 = (x2 - pad_x) * img_w / new_w
        uy2 = (y2 - pad_y) * img_h / new_h
        # Clamp to page
        ux1 = max(0.0, ux1); uy1 = max(0.0, uy1)
        ux2 = min(float(img_w), ux2); uy2 = min(float(img_h), uy2)
        w = ux2 - ux1
        h = uy2 - uy1
        if w < 5 or h < 5:
            continue
        cls_idx = int(cls)
        cls_name = YOLO_CLASSES[cls_idx] if 0 <= cls_idx < len(YOLO_CLASSES) else f"cls_{cls_idx}"
        boxes.append(YoloBox(
            cls=cls_idx, cls_name=cls_name, conf=float(conf),
            x=ux1 / img_w, y=uy1 / img_h,
            w=w / img_w, h=h / img_h,
        ))

    return boxes
