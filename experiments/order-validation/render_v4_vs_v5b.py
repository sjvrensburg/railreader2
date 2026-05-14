"""Render side-by-side overlays comparing v4 (current production) and v5b
(RCM-only ablation) on the diagnostic pages that motivated v5.

Inputs:
    v4 ONNX (current production) + v5b ONNX (ablation result)
Outputs:
    /tmp/v4_vs_v5b/<label>.png — single PNG per page, v4 left, v5b right.

Focus: pages where v4 underperformed and motivated trying RCM.
    - DANAM p5: references-heavy, v4 missed elements
    - doclaynet_000100: missed entire paragraphs
    - doclaynet_000500: financial table, both detectors caught only 1 box

CPU only — avoids fighting any subsequent GPU runs.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

import pypdfium2 as pdfium
from PIL import Image, ImageDraw

sys.path.insert(0, str(Path(__file__).parent))
import tiny_layout
import xy_cut as xy
from visualise import annotate, _font


def render_page(pdf_path: Path, page_index: int, dpi: int = 150) -> Image.Image:
    pdf = pdfium.PdfDocument(str(pdf_path))
    try:
        return pdf[page_index].render(scale=dpi / 72.0).to_pil().convert("RGB")
    finally:
        pdf.close()


def side_by_side(image: Image.Image, a_boxes: list, b_boxes: list,
                 label_a: str, label_b: str) -> Image.Image:
    annotated_a = annotate(image, a_boxes)
    annotated_b = annotate(image, b_boxes)
    W, H = image.size
    title_h = 40
    out = Image.new("RGB", (W * 2, H + title_h), (255, 255, 255))
    out.paste(annotated_a, (0, title_h))
    out.paste(annotated_b, (W, title_h))
    draw = ImageDraw.Draw(out)
    f = _font(28)
    draw.text((10, 6), label_a, fill=(0, 0, 0), font=f)
    draw.text((W + 10, 6), label_b, fill=(0, 0, 0), font=f)
    return out


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--v4", type=Path,
                   default=Path("/home/stefan/railreader2/experiments/layout-detector/runs/v4/tiny_layout.onnx"))
    p.add_argument("--v5b", type=Path,
                   default=Path("/home/stefan/railreader2/experiments/layout-detector/runs/v5b/tiny_layout.onnx"))
    p.add_argument("--output", type=Path, default=Path("/tmp/v4_vs_v5b"))
    p.add_argument("--conf-threshold", type=float, default=0.25,
                   help="Confidence threshold (default 0.25 = production)")
    args = p.parse_args()

    args.output.mkdir(parents=True, exist_ok=True)

    print("loading models on CPU...", flush=True)
    sess_v4 = tiny_layout.load_model(args.v4, providers=["CPUExecutionProvider"])
    sess_v5b = tiny_layout.load_model(args.v5b, providers=["CPUExecutionProvider"])

    danam = Path("/home/stefan/Documents/Research 2026/Misc/Distribution-Aware_Neural_Additive_Models_Robust_Interpretable_Deep_Learning_with_Feature_Selection.pdf")
    stat321 = Path("/home/stefan/Documents/Teaching 2026/STAT321/Notes/13-unit-root-tests.pdf")
    dln_dir = Path("/home/stefan/Downloads/doclaynet_sample")

    test_pages: list[tuple[str, Path | None, int | None]] = []
    if danam.exists():
        test_pages += [
            ("danam_p3", danam, 2),
            ("danam_p4", danam, 3),
            ("danam_p5_references", danam, 4),
        ]
    if stat321.exists():
        test_pages += [
            ("stat321_p1", stat321, 0),
            ("stat321_p4", stat321, 3),
            ("stat321_p6", stat321, 5),
        ]
    for idx in (100, 500):
        img = dln_dir / f"doclaynet_{idx:06d}.jpg"
        if img.exists():
            test_pages.append((f"doclaynet_{idx:06d}", img, None))

    print(f"comparing on {len(test_pages)} test pages")

    for label, src, page in test_pages:
        try:
            if page is None:
                with Image.open(src) as image:
                    image = image.convert("RGB")
            else:
                image = render_page(src, page)
            v4_raw = tiny_layout.run(sess_v4, image, conf_threshold=args.conf_threshold)
            v5b_raw = tiny_layout.run(sess_v5b, image, conf_threshold=args.conf_threshold)
            v4_ordered = xy.sort(v4_raw)
            v5b_ordered = xy.sort(v5b_raw)
            print(f"  {label}: v4={len(v4_ordered):>3}  v5b={len(v5b_ordered):>3}  "
                  f"(delta {len(v5b_ordered) - len(v4_ordered):+d})")
            out = side_by_side(image, v4_ordered, v5b_ordered,
                               label_a=f"v4 ({len(v4_ordered)} dets)",
                               label_b=f"v5b RCM-only ({len(v5b_ordered)} dets)")
            out.save(args.output / f"{label}.png")
        except Exception as e:
            print(f"  {label}: ERROR {e}")

    print(f"\nwrote comparisons to {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
