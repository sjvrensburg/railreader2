"""Render side-by-side overlays comparing the v2 and v4 (current) detectors
on a set of diverse pages, for visual quality comparison.

Inputs:
    PDFs / image paths (mix of in-distribution and held-out genres)
Outputs:
    /tmp/v2_vs_v4/<label>.png — single PNG per page with v2 on the left,
    v4 on the right, annotated with class names + reading-order indices.

Designed to use CPU so it doesn't fight a running training job.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

import pypdfium2 as pdfium
from PIL import Image, ImageDraw

# Reuse the existing harness — tiny_layout for both v2 and v4 (same arch)
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


def side_by_side(image: Image.Image, v2_boxes: list, v4_boxes: list,
                 label_v2: str = "v2", label_v4: str = "v4 (current)") -> Image.Image:
    annotated_v2 = annotate(image, v2_boxes)
    annotated_v4 = annotate(image, v4_boxes)
    W, H = image.size
    title_h = 40
    out = Image.new("RGB", (W * 2, H + title_h), (255, 255, 255))
    out.paste(annotated_v2, (0, title_h))
    out.paste(annotated_v4, (W, title_h))
    draw = ImageDraw.Draw(out)
    f = _font(28)
    draw.text((10, 6), label_v2, fill=(0, 0, 0), font=f)
    draw.text((W + 10, 6), label_v4, fill=(0, 0, 0), font=f)
    return out


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--v2", type=Path,
                   default=Path("/home/stefan/railreader2/experiments/layout-detector/runs/v2/tiny_layout.onnx"))
    p.add_argument("--v4", type=Path, default=Path("/tmp/v4_partial.onnx"))
    p.add_argument("--output", type=Path, default=Path("/tmp/v2_vs_v4"))
    args = p.parse_args()

    args.output.mkdir(parents=True, exist_ok=True)

    print("loading models on CPU...", flush=True)
    sess_v2 = tiny_layout.load_model(args.v2, providers=["CPUExecutionProvider"])
    sess_v4 = tiny_layout.load_model(args.v4, providers=["CPUExecutionProvider"])

    # Curated test pages: mix of held-out genres
    danam = Path("/home/stefan/Documents/Research 2026/Misc/Distribution-Aware_Neural_Additive_Models_Robust_Interpretable_Deep_Learning_with_Feature_Selection.pdf")
    stat321 = Path("/home/stefan/Documents/Teaching 2026/STAT321/Notes/13-unit-root-tests.pdf")

    test_pages: list[tuple[str, Path | None, int]] = []  # (label, pdf_path, page_idx)
    if danam.exists():
        test_pages.extend([
            ("danam_p3_ordering", danam, 2),   # user reported "odd ordering"
            ("danam_p4_ordering", danam, 3),   # user reported "odd ordering"
            ("danam_p5_recall",   danam, 4),   # user reported "misses most elements"
        ])
    if stat321.exists():
        # User reported misses on pages 1, 3-8
        test_pages.extend([
            ("stat321_p1_recall", stat321, 0),
            ("stat321_p4_recall", stat321, 3),
            ("stat321_p6_recall", stat321, 5),
        ])

    # Some random DocLayNet samples (in v4 training, NOT in v2)
    dln = sorted(Path("/home/stefan/Downloads/doclaynet_sample").glob("*.jpg"))
    if dln:
        # Pick a few that look visually diverse
        for i, idx in enumerate([100, 500, 2500]):
            if idx < len(dln):
                test_pages.append((f"doclaynet_{i}", None, -1))  # we'll handle path differently
                test_pages[-1] = (f"doclaynet_{idx:06d}", dln[idx], None)  # type: ignore

    print(f"comparing on {len(test_pages)} test pages")

    for label, src, page in test_pages:
        try:
            if page is None:
                # Already an image file
                with Image.open(src) as image:
                    image = image.convert("RGB")
            else:
                image = render_page(src, page)
            v2_raw = tiny_layout.run(sess_v2, image)
            v4_raw = tiny_layout.run(sess_v4, image)
            v2_ordered = xy.sort(v2_raw)
            v4_ordered = xy.sort(v4_raw)
            print(f"  {label}: v2={len(v2_ordered):>3} v4={len(v4_ordered):>3}")
            out = side_by_side(image, v2_ordered, v4_ordered,
                               label_v2=f"v2 ({len(v2_ordered)} dets)",
                               label_v4=f"v4 partial @ ep10 ({len(v4_ordered)} dets)")
            out.save(args.output / f"{label}.png")
        except Exception as e:
            print(f"  {label}: ERROR {e}")

    print(f"\nwrote comparisons to {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
