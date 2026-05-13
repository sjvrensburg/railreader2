"""Tiny anchor-free YOLO-style detector for document layout.

Components:
- model: TinyLayoutYOLO (MobileNetV3-Small backbone + single-level head)
- dataset: YoloDataset (reads YOLO-format labels directly, no conversion)
- loss: center-prior label assignment + focal cls + GIoU reg + BCE obj
- decode: convert raw head output to bounding boxes (+ optional NMS)
"""

from .model import TinyLayoutYOLO, build_model
from .dataset import YoloDataset, letterbox, yolo_collate
from .loss import TinyYoloLoss
from .decode import decode_predictions, batched_nms

__all__ = [
    "TinyLayoutYOLO", "build_model",
    "YoloDataset", "letterbox", "yolo_collate",
    "TinyYoloLoss",
    "decode_predictions", "batched_nms",
]
