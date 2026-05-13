"""Export TinyLayoutYOLO (v2 multi-level) to ONNX with decoding bundled in.

Mirrors the YOLO26 end-to-end-NMS-free interface that RailReader2's C# path
already understands:

  input:  images   [1, 3, S, S]  float32 in [0, 1], RGB, letterboxed
  output: output0  [1, N, 6]     [x1, y1, x2, y2, conf, cls] per detection

N = N_p3 + N_p4 = (S/8)² + (S/16)² (4500 at S=480).
Coords are in input-image (letterboxed) pixel space.

NMS is NOT applied here — `LayoutAnalyzer.PostProcessBlocks` in C# runs
`Nms()` after thresholding, which we want to preserve.
"""

from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np
import torch
import torch.nn as nn

from layout_detector import build_model
from layout_detector.model import STRIDES


class InferenceWrapper(nn.Module):
    """Wraps TinyLayoutYOLO with decode-to-[x1,y1,x2,y2,conf,cls] across both
    pyramid levels, concatenated into a single (B, N, 6) tensor.

    ONNX-trace-friendly: only sigmoid/max/exp/reshape/stack/concat.
    """

    def __init__(self, model: nn.Module, strides: tuple[int, int] = STRIDES):
        super().__init__()
        self.model = model
        self.strides = strides

    def _decode_level(self, level_out: dict[str, torch.Tensor],
                      stride: int) -> torch.Tensor:
        cls = level_out["cls"]   # (B, C, H, W)
        reg = level_out["reg"]   # (B, 4, H, W)
        obj = level_out["obj"]   # (B, 1, H, W)
        B, C, H, W = cls.shape
        s = float(stride)

        device = cls.device
        dtype = cls.dtype
        yy, xx = torch.meshgrid(
            torch.arange(H, device=device, dtype=dtype),
            torch.arange(W, device=device, dtype=dtype),
            indexing="ij",
        )
        xx = xx.unsqueeze(0); yy = yy.unsqueeze(0)

        tx = reg[:, 0]; ty = reg[:, 1]
        lw = reg[:, 2]; lh = reg[:, 3]
        cx = (xx + torch.sigmoid(tx)) * s
        cy = (yy + torch.sigmoid(ty)) * s
        bw = torch.exp(lw) * s
        bh = torch.exp(lh) * s
        x1 = cx - bw / 2
        y1 = cy - bh / 2
        x2 = cx + bw / 2
        y2 = cy + bh / 2

        obj_p = torch.sigmoid(obj).squeeze(1)
        cls_p = torch.sigmoid(cls)
        cls_max, cls_idx = cls_p.max(dim=1)
        score = obj_p * cls_max

        x1 = x1.reshape(B, -1)
        y1 = y1.reshape(B, -1)
        x2 = x2.reshape(B, -1)
        y2 = y2.reshape(B, -1)
        score = score.reshape(B, -1)
        cls_f = cls_idx.reshape(B, -1).to(dtype)

        return torch.stack([x1, y1, x2, y2, score, cls_f], dim=-1)  # (B, HW, 6)

    def forward(self, images: torch.Tensor) -> torch.Tensor:
        out = self.model(images)
        det_p3 = self._decode_level(out["p3"], self.strides[0])
        det_p4 = self._decode_level(out["p4"], self.strides[1])
        return torch.cat([det_p3, det_p4], dim=1)   # (B, N3+N4, 6)


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--checkpoint", type=Path, default=Path("runs/v2/best.pt"))
    p.add_argument("--output", type=Path, default=Path("runs/v2/tiny_layout.onnx"))
    p.add_argument("--input-size", type=int, default=480,
                   help="Must match LayoutConstants.InputSize in RailReader2 C#")
    p.add_argument("--num-classes", type=int, default=16)
    p.add_argument("--opset", type=int, default=17)
    p.add_argument("--test-image", type=Path, default=None,
                   help="Optional: run on this image and print top-5 detections")
    args = p.parse_args()

    print(f"loading checkpoint: {args.checkpoint}")
    ckpt = torch.load(args.checkpoint, map_location="cpu", weights_only=False)
    state = ckpt["model"] if isinstance(ckpt, dict) and "model" in ckpt else ckpt

    model = build_model(num_classes=args.num_classes, pretrained=False)
    model.load_state_dict(state)
    model.eval()
    print(f"params: {sum(t.numel() for t in model.parameters()) / 1e6:.2f}M")

    inf = InferenceWrapper(model).eval()
    dummy = torch.zeros(1, 3, args.input_size, args.input_size, dtype=torch.float32)

    with torch.no_grad():
        torch_out = inf(dummy)
    n_p3 = (args.input_size // STRIDES[0]) ** 2
    n_p4 = (args.input_size // STRIDES[1]) ** 2
    expected_n = n_p3 + n_p4
    print(f"forward shape: {tuple(torch_out.shape)}  expected: (1, {expected_n}, 6)  "
          f"(P3: {n_p3}, P4: {n_p4})")
    assert torch_out.shape == (1, expected_n, 6), "decoded output shape mismatch"

    args.output.parent.mkdir(parents=True, exist_ok=True)
    print(f"exporting to {args.output}  (opset={args.opset})...")
    torch.onnx.export(
        inf,
        dummy,
        args.output,
        opset_version=args.opset,
        input_names=["images"],
        output_names=["output0"],
        dynamic_axes={
            "images":  {0: "batch"},
            "output0": {0: "batch"},
        },
        do_constant_folding=True,
    )

    size_mb = args.output.stat().st_size / 1e6
    print(f"wrote {args.output}  ({size_mb:.2f} MB)")

    print("\nverifying with onnxruntime...")
    import onnxruntime as ort
    so = ort.SessionOptions()
    so.log_severity_level = 3
    sess = ort.InferenceSession(str(args.output), sess_options=so,
                                providers=["CPUExecutionProvider"])
    print(f"  inputs:  {[(i.name, i.shape) for i in sess.get_inputs()]}")
    print(f"  outputs: {[(o.name, o.shape) for o in sess.get_outputs()]}")

    ort_out = sess.run(None, {"images": dummy.numpy()})[0]
    print(f"  onnx output shape:     {ort_out.shape}")
    diff = float(np.abs(torch_out.numpy() - ort_out).max())
    print(f"  max abs diff vs torch: {diff:.6e}")
    if diff > 1e-2:
        print("  WARNING: drift exceeds 1e-2 — something off with the export")

    if args.test_image is not None and args.test_image.exists():
        print(f"\ntest image: {args.test_image}")
        from PIL import Image
        from layout_detector.dataset import letterbox
        with Image.open(args.test_image) as img:
            img = img.convert("RGB")
            canvas, scale, pad_x, pad_y = letterbox(img, args.input_size)
        arr = np.asarray(canvas, dtype=np.float32) / 255.0
        chw = np.transpose(arr, (2, 0, 1))[None]
        det = sess.run(None, {"images": chw})[0][0]
        keep = np.argsort(det[:, 4])[::-1][:5]
        cls_names = [
            "t", "t1", "t2", "t3", "paragraph", "author", "keyword", "abstract",
            "reference", "graph", "note", "other", "formula", "table", "footnote",
            "class17",
        ]
        print("  top-5 by score:")
        for idx in keep:
            x1, y1, x2, y2, conf, c = det[idx]
            name = cls_names[int(c)] if 0 <= int(c) < len(cls_names) else f"cls{int(c)}"
            print(f"    {name:<10}  conf={conf:.3f}  "
                  f"box=({x1:6.1f},{y1:6.1f},{x2:6.1f},{y2:6.1f})")

    print("\nready to drop into RailReader2:")
    print(f"  cp {args.output} ../../models/<new-name>.onnx")
    print(f"  Update src/RailReader.Core/DocumentController.cs FindModelPath() filename")
    print(f"  Update src/RailReader.Core/Services/LayoutConstants.cs InputSize "
          f"to {args.input_size} (currently 640)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
