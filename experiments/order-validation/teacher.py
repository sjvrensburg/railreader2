"""PP-DocLayoutV3 inference wrapper. Returns detections in reading order."""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

import numpy as np
import onnxruntime as ort
from PIL import Image


PP_INPUT_SIZE = 800
PP_CONF_THRESHOLD = 0.4

# PP-DocLayoutV3 class labels (25 classes, alphabetical) — mirror of the
# pre-YOLO LayoutConstants table in RailReader.Core for human-readable output.
PP_CLASSES = [
    "abstract", "algorithm", "aside_text", "chart", "content",
    "display_formula", "doc_title", "figure_title", "footer", "footer_image",
    "footnote", "formula_number", "header", "header_image", "image",
    "inline_formula", "number", "paragraph_title", "reference",
    "reference_content", "seal", "table", "text", "vertical_text",
    "vision_footnote",
]


@dataclass(frozen=True)
class TeacherBox:
    """Detection from PP-DocLayoutV3, in normalised page coordinates [0, 1]."""
    cls: int
    cls_name: str
    conf: float
    x: float       # left
    y: float       # top
    w: float
    h: float
    order: int     # reading order from Global Pointer (0 = first)


def load_teacher(model_path: Path,
                 providers: list[str] | None = None) -> ort.InferenceSession:
    so = ort.SessionOptions()
    so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    so.log_severity_level = 3
    providers = providers or ort.get_available_providers()
    return ort.InferenceSession(str(model_path), sess_options=so, providers=providers)


def _preprocess(image: Image.Image) -> tuple[dict, float]:
    """Scale-preserving fit into 800x800 with black padding at top-left.
    Returns ORT input dict + the scale factor used."""
    w, h = image.size
    scale = PP_INPUT_SIZE / max(w, h)
    new_w, new_h = int(round(w * scale)), int(round(h * scale))
    resized = image.resize((new_w, new_h), Image.BILINEAR)

    canvas = Image.new("RGB", (PP_INPUT_SIZE, PP_INPUT_SIZE), (0, 0, 0))
    canvas.paste(resized, (0, 0))
    arr = np.asarray(canvas, dtype=np.float32) / 255.0  # HWC
    chw = np.transpose(arr, (2, 0, 1))                  # CHW
    return {
        "im_shape": np.array([[PP_INPUT_SIZE, PP_INPUT_SIZE]], dtype=np.float32),
        "image": chw[None],
        "scale_factor": np.array([[1.0, 1.0]], dtype=np.float32),
    }, scale


def run(session: ort.InferenceSession, image: Image.Image,
        conf_threshold: float = PP_CONF_THRESHOLD) -> list[TeacherBox]:
    """Run PP-DocLayoutV3, return detections sorted by reading order."""
    inputs, scale = _preprocess(image)
    outputs = session.run(None, inputs)

    # Find the [N, 7] detection tensor: [cls, conf, x1, y1, x2, y2, order]
    det = None
    for o in outputs:
        if o.ndim == 2 and o.shape[1] >= 7:
            det = o
            break
    if det is None or det.shape[0] == 0:
        return []

    img_w, img_h = image.size
    boxes: list[TeacherBox] = []
    for row in det:
        cls, conf, x1, y1, x2, y2, order = (
            int(row[0]), float(row[1]),
            float(row[2]), float(row[3]),
            float(row[4]), float(row[5]),
            int(row[6]),
        )
        if conf < conf_threshold:
            continue
        # Unproject letterbox → original pixel space → normalised
        nx = (x1 / scale) / img_w
        ny = (y1 / scale) / img_h
        nw = ((x2 - x1) / scale) / img_w
        nh = ((y2 - y1) / scale) / img_h
        cls_name = PP_CLASSES[cls] if 0 <= cls < len(PP_CLASSES) else f"cls_{cls}"
        boxes.append(TeacherBox(
            cls=cls, cls_name=cls_name, conf=conf,
            x=nx, y=ny, w=nw, h=nh, order=order,
        ))

    boxes.sort(key=lambda b: b.order)
    # Renumber 0..N-1 contiguous in case the model emitted gaps
    return [TeacherBox(b.cls, b.cls_name, b.conf, b.x, b.y, b.w, b.h, i)
            for i, b in enumerate(boxes)]
