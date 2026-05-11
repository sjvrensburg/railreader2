"""Render a PDF page with PP-DocLayoutV3's reading order overlaid as numbered boxes.

Use this to see what the "correct" reading order should be on a failing PDF,
then compare visually against RailReader2's XY-cut overlay.

Example:
    python visualise.py paper.pdf --page 1 --output gt.png
    python visualise.py paper.pdf --pages 1-3 --output-dir out/
"""

from __future__ import annotations

import argparse
import colorsys
from pathlib import Path

import pypdfium2 as pdfium
from PIL import Image, ImageDraw, ImageFont

from teacher import TeacherBox, load_teacher, run as run_teacher


def class_colour(cls: int) -> tuple[int, int, int]:
    """Distinct hue per class for visual separation."""
    h = (cls * 0.137) % 1.0  # golden-ratio-ish spread
    r, g, b = colorsys.hsv_to_rgb(h, 0.7, 0.95)
    return int(r * 255), int(g * 255), int(b * 255)


def _font(size: int) -> ImageFont.ImageFont:
    for candidate in [
        "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
        "/usr/share/fonts/TTF/DejaVuSans-Bold.ttf",
        "/Library/Fonts/Arial Bold.ttf",
    ]:
        try:
            return ImageFont.truetype(candidate, size)
        except OSError:
            continue
    return ImageFont.load_default()


def render_page(pdf_path: Path, page_index: int, dpi: int = 150) -> Image.Image:
    pdf = pdfium.PdfDocument(str(pdf_path))
    try:
        page = pdf[page_index]
        bitmap = page.render(scale=dpi / 72.0)
        return bitmap.to_pil().convert("RGB")
    finally:
        pdf.close()


def annotate(image: Image.Image, boxes: list[TeacherBox]) -> Image.Image:
    out = image.copy()
    draw = ImageDraw.Draw(out)
    W, H = out.size
    font = _font(max(12, H // 80))
    label_pad = 3

    for b in boxes:
        colour = class_colour(b.cls)
        x1, y1 = int(b.x * W), int(b.y * H)
        x2, y2 = int((b.x + b.w) * W), int((b.y + b.h) * H)
        draw.rectangle([x1, y1, x2, y2], outline=colour, width=3)

        label = f"#{b.order} {b.cls_name} ({int(b.conf * 100)}%)"
        tx, ty = x1 + label_pad, max(0, y1 - 22)
        tw = draw.textlength(label, font=font)
        draw.rectangle([tx - label_pad, ty, tx + tw + label_pad, ty + 20], fill=colour)
        # Black text on light colours, white on dark
        bg_lightness = sum(colour) / 3
        text_colour = (0, 0, 0) if bg_lightness > 140 else (255, 255, 255)
        draw.text((tx, ty), label, fill=text_colour, font=font)

    return out


def parse_page_spec(spec: str | None, page_count: int) -> list[int]:
    """Parse '1', '1-3', or None (all)."""
    if not spec:
        return list(range(page_count))
    if "-" in spec:
        a, b = spec.split("-", 1)
        return list(range(int(a) - 1, int(b)))
    return [int(spec) - 1]


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("pdf", type=Path)
    p.add_argument("--page", type=str, default="1",
                   help="1-based page number or range like '1-3'")
    p.add_argument("--output", type=Path, default=None,
                   help="Single-file output (only valid for one page)")
    p.add_argument("--output-dir", type=Path, default=None,
                   help="Directory to write one PNG per page")
    p.add_argument("--teacher", type=Path,
                   default=Path(__file__).parent.parent.parent / "models" / "PP-DocLayoutV3.onnx")
    p.add_argument("--dpi", type=int, default=150)
    args = p.parse_args()

    if not args.teacher.exists():
        raise SystemExit(f"Teacher model not found: {args.teacher}")

    pdf = pdfium.PdfDocument(str(args.pdf))
    page_count = len(pdf)
    pdf.close()
    pages = parse_page_spec(args.page, page_count)

    if args.output and len(pages) > 1:
        raise SystemExit("--output requires a single page; use --output-dir for ranges")
    if not args.output and not args.output_dir:
        args.output_dir = Path("vis-out")

    session = load_teacher(args.teacher)

    for i in pages:
        page_img = render_page(args.pdf, i, args.dpi)
        boxes = run_teacher(session, page_img)
        annotated = annotate(page_img, boxes)
        if args.output:
            out_path = args.output
        else:
            args.output_dir.mkdir(parents=True, exist_ok=True)
            out_path = args.output_dir / f"{args.pdf.stem}_p{i + 1:03d}.png"
        annotated.save(out_path)
        print(f"page {i + 1}: {len(boxes)} blocks → {out_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
