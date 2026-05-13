"""YOLO-format dataset reader. Images + per-image .txt label files with lines:
    class_id x_centre y_centre w h    (all normalised to [0, 1])
"""

from __future__ import annotations

import random
from dataclasses import dataclass
from pathlib import Path
from typing import Sequence

import numpy as np
import torch
from PIL import Image
from torch.utils.data import Dataset


@dataclass(frozen=True)
class Sample:
    """One image worth of labels (in normalised image coords)."""
    image_path: Path
    boxes: np.ndarray   # (N, 4)  [cx, cy, w, h] in [0, 1]
    labels: np.ndarray  # (N,)    int class ids


# Dataset-id → runtime-id mapping for AIBox-IMU/DLA YOLO-DLA dataset.
# Matches the runtime schema documented in
# src/RailReader.Core/Services/LayoutConstants.cs: training IDs 4 (t4) and
# 16 are absent / unused; ID 17 is a rare garbage class kept as runtime 15.
YOLO_DLA_CLASS_REMAP: dict[int, int] = {
    0: 0,   # t
    1: 1,   # t1
    2: 2,   # t2
    3: 3,   # t3
    # 4: t4  — 0 training instances, dropped
    5: 4,   # paragraph (body text — dominant class)
    6: 5,   # author
    7: 6,   # keyword
    8: 7,   # abstract
    9: 8,   # reference
    10: 9,  # graph
    11: 10, # note
    12: 11, # other
    13: 12, # formula
    14: 13, # table
    15: 14, # footnote
    17: 15, # rare/unknown — kept to mirror YOLO26 runtime shape
}


def letterbox(image: Image.Image, target: int = 480, pad_value: int = 114
              ) -> tuple[Image.Image, float, int, int]:
    """Scale-preserving resize to (target, target) with grey padding.
    Returns (canvas, scale, pad_x, pad_y) so we can map labels through it."""
    w, h = image.size
    scale = target / max(w, h)
    new_w, new_h = int(round(w * scale)), int(round(h * scale))
    resized = image.resize((new_w, new_h), Image.BILINEAR)
    pad_x = (target - new_w) // 2
    pad_y = (target - new_h) // 2
    canvas = Image.new("RGB", (target, target), (pad_value, pad_value, pad_value))
    canvas.paste(resized, (pad_x, pad_y))
    return canvas, scale, pad_x, pad_y


def _transform_labels(boxes: np.ndarray, orig_w: int, orig_h: int,
                      scale: float, pad_x: int, pad_y: int, target: int
                      ) -> np.ndarray:
    """Move normalised-image boxes through the letterbox to normalised-target."""
    if len(boxes) == 0:
        return boxes
    cx = boxes[:, 0] * orig_w * scale + pad_x
    cy = boxes[:, 1] * orig_h * scale + pad_y
    w = boxes[:, 2] * orig_w * scale
    h = boxes[:, 3] * orig_h * scale
    return np.stack([cx / target, cy / target, w / target, h / target], axis=1)


def _load_labels(path: Path, class_remap: dict[int, int] | None = None
                 ) -> tuple[np.ndarray, np.ndarray]:
    boxes, labels = [], []
    if not path.exists():
        return np.zeros((0, 4), dtype=np.float32), np.zeros((0,), dtype=np.int64)
    with open(path) as f:
        for line in f:
            parts = line.strip().split()
            if len(parts) != 5:
                continue
            cls, xc, yc, w, h = parts
            cls_int = int(cls)
            if class_remap is not None:
                if cls_int not in class_remap:
                    # Drop labels with class IDs not in the mapping (e.g. t4)
                    continue
                cls_int = class_remap[cls_int]
            boxes.append([float(xc), float(yc), float(w), float(h)])
            labels.append(cls_int)
    return (np.asarray(boxes, dtype=np.float32),
            np.asarray(labels, dtype=np.int64))


class YoloDataset(Dataset):
    """Reads images + YOLO-format labels. Optional horizontal flip is OFF —
    documents have canonical orientation. Mosaic/mixup also intentionally off."""

    def __init__(self, image_dir: Path, label_dir: Path, input_size: int = 480,
                 file_list: Sequence[str] | None = None,
                 class_remap: dict[int, int] | None = None):
        self.image_dir = Path(image_dir)
        self.label_dir = Path(label_dir)
        self.input_size = input_size
        # `class_remap=None` means labels are ALREADY in our runtime schema.
        # Pass `class_remap=YOLO_DLA_CLASS_REMAP` explicitly when loading raw
        # YOLO-DLA labels (which use the dataset YAML's id space).
        self.class_remap = class_remap

        if file_list is None:
            self.files = sorted(
                [p.name for p in self.image_dir.glob("*.jpg")]
                + [p.name for p in self.image_dir.glob("*.png")]
            )
        else:
            self.files = list(file_list)

    def __len__(self) -> int:
        return len(self.files)

    def __getitem__(self, idx: int) -> tuple[torch.Tensor, torch.Tensor]:
        fname = self.files[idx]
        img_path = self.image_dir / fname
        lbl_path = self.label_dir / (Path(fname).stem + ".txt")

        with Image.open(img_path) as image:
            image = image.convert("RGB")
            orig_w, orig_h = image.size
            canvas, scale, pad_x, pad_y = letterbox(image, self.input_size)

        boxes, labels = _load_labels(lbl_path, self.class_remap)
        boxes = _transform_labels(boxes, orig_w, orig_h, scale, pad_x, pad_y,
                                  self.input_size)

        # to tensor: CHW float32 in [0,1] — no ImageNet normalisation. Matches
        # the C# preprocessing pipeline; backbone is fine-tuned from scratch
        # to that input distribution.
        arr = np.asarray(canvas, dtype=np.float32) / 255.0
        chw = torch.from_numpy(arr.transpose(2, 0, 1)).contiguous()

        # labels tensor: (N, 5) = [class, cx, cy, w, h]
        if len(boxes):
            lab = torch.cat([
                torch.from_numpy(labels).float().unsqueeze(1),
                torch.from_numpy(boxes),
            ], dim=1)
        else:
            lab = torch.zeros((0, 5), dtype=torch.float32)

        return chw, lab


def yolo_collate(batch: list[tuple[torch.Tensor, torch.Tensor]]
                 ) -> tuple[torch.Tensor, list[torch.Tensor]]:
    """Stack images, keep labels ragged (variable N per image)."""
    images = torch.stack([b[0] for b in batch], dim=0)
    labels = [b[1] for b in batch]
    return images, labels


def compute_class_weights(label_dir: Path, num_classes: int,
                          class_remap: dict[int, int] | None = None,
                          clamp: tuple[float, float] = (0.3, 10.0)
                          ) -> tuple[list[float], list[int]]:
    """Inverse-frequency square-root class weights from label files.

    Formula: w_c = sqrt(median_count / count_c), clamped to [0.3, 10.0].

    Square root softens the rebalance — pure inverse frequency would have
    `paragraph` (51% of data) get weight ~0.02 and `t3` (29 instances)
    weight ~100. Sqrt gives roughly [0.3, 10] which preserves majority-class
    learning while giving rare classes ~30× more gradient than the unweighted
    baseline.

    Returns (weights_list, counts_list) — both length `num_classes`.

    `class_remap` must be None for runtime-schema labels (the post-merge
    common case). Pass YOLO_DLA_CLASS_REMAP explicitly when scanning raw
    YOLO-DLA dataset-schema labels.
    """
    counts = np.zeros(num_classes, dtype=np.int64)
    for lbl_path in Path(label_dir).glob("*.txt"):
        _, labels = _load_labels(lbl_path, class_remap)
        for c in labels:
            if 0 <= c < num_classes:
                counts[c] += 1
    nonzero = counts[counts > 0]
    if len(nonzero) == 0:
        return [1.0] * num_classes, counts.tolist()
    median = float(np.median(nonzero))
    safe_counts = np.maximum(counts, 1).astype(np.float64)
    weights = np.sqrt(median / safe_counts)
    weights = np.clip(weights, clamp[0], clamp[1])
    # Classes with zero counts get the upper clamp (model shouldn't be punished
    # for them, but if it predicts them spuriously the rare-class signal kicks
    # in to suppress).
    weights[counts == 0] = clamp[1]
    return weights.tolist(), counts.tolist()


def make_train_val_split(image_dir: Path, label_dir: Path,
                         val_fraction: float = 0.05, seed: int = 42
                         ) -> tuple[list[str], list[str]]:
    """Random train/val split over images that have label files."""
    image_dir = Path(image_dir)
    label_dir = Path(label_dir)
    files = sorted(
        [p.name for p in image_dir.glob("*.jpg") if (label_dir / (p.stem + ".txt")).exists()]
        + [p.name for p in image_dir.glob("*.png") if (label_dir / (p.stem + ".txt")).exists()]
    )
    rng = random.Random(seed)
    rng.shuffle(files)
    n_val = max(1, int(len(files) * val_fraction))
    return files[n_val:], files[:n_val]


if __name__ == "__main__":
    import sys
    image_dir = Path(sys.argv[1] if len(sys.argv) > 1 else "/home/stefan/Downloads/18000/images")
    label_dir = Path(sys.argv[2] if len(sys.argv) > 2 else "/home/stefan/Downloads/18000/labels")
    ds = YoloDataset(image_dir, label_dir, input_size=480)
    print(f"Dataset: {len(ds)} images")
    img, lab = ds[0]
    print(f"  image: {tuple(img.shape)}  range [{img.min():.3f}, {img.max():.3f}]")
    print(f"  labels: {tuple(lab.shape)}")
    print(f"  first 3 labels:\n{lab[:3]}")
