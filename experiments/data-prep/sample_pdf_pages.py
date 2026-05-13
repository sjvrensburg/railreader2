"""Sample random pages from a set of PDF directories, render as JPGs.

Output: one .jpg per sampled page in --output. Filenames carry the source
PDF stem + page index so we can trace back. Seeded random sampling for
reproducibility.

Usage:
    python sample_pdf_pages.py \\
        --pdf-dir "/home/stefan/Documents/Teaching 2026/STAT312/Class Notes" \\
        --pdf-dir "/home/stefan/Documents/Teaching 2026/STAT321/Notes" \\
        --output /tmp/teaching_pages \\
        --pages-per-pdf 5 \\
        --dpi 150
"""

from __future__ import annotations

import argparse
import random
import re
from pathlib import Path

import pypdfium2 as pdfium
from tqdm import tqdm


def _safe_name(s: str) -> str:
    """Filesystem-safe filename component."""
    return re.sub(r"[^A-Za-z0-9._-]", "_", s)[:80]


def sample_pdf(pdf_path: Path, out_dir: Path, pages_per_pdf: int, dpi: int,
               rng: random.Random) -> int:
    """Render pages_per_pdf random pages from pdf_path as JPGs.

    Returns number of pages actually rendered (could be less if PDF has
    fewer than pages_per_pdf pages or rendering fails).
    """
    try:
        pdf = pdfium.PdfDocument(str(pdf_path))
    except Exception as e:
        print(f"  skip {pdf_path.name}: {e}")
        return 0
    try:
        n_pages = len(pdf)
        if n_pages == 0:
            return 0
        sample_n = min(pages_per_pdf, n_pages)
        page_indices = rng.sample(range(n_pages), sample_n)
        stem = _safe_name(pdf_path.stem)
        scale = dpi / 72.0
        rendered = 0
        for pidx in page_indices:
            try:
                bitmap = pdf[pidx].render(scale=scale).to_pil().convert("RGB")
                # 0-based to 1-based page numbering in filename
                out_path = out_dir / f"{stem}__p{pidx + 1:03d}.jpg"
                bitmap.save(out_path, "JPEG", quality=88)
                rendered += 1
            except Exception as e:
                print(f"  {pdf_path.name} p{pidx}: {e}")
        return rendered
    finally:
        pdf.close()


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--pdf-dir", action="append", required=True, type=Path,
                   help="Directory of PDFs to sample from (can repeat)")
    p.add_argument("--output", required=True, type=Path,
                   help="Output directory for rendered JPGs")
    p.add_argument("--pages-per-pdf", type=int, default=5)
    p.add_argument("--dpi", type=int, default=150)
    p.add_argument("--seed", type=int, default=42)
    p.add_argument("--max-depth", type=int, default=2,
                   help="Glob recursion depth for *.pdf (default 2)")
    args = p.parse_args()

    args.output.mkdir(parents=True, exist_ok=True)
    rng = random.Random(args.seed)

    # Collect all PDFs
    pdfs: list[Path] = []
    for d in args.pdf_dir:
        if not d.is_dir():
            print(f"WARNING: {d} is not a directory, skipping")
            continue
        # Glob with limited recursion
        for depth in range(args.max_depth + 1):
            pattern = "/".join(["*"] * depth + ["*.pdf"]) if depth else "*.pdf"
            pdfs.extend(d.glob(pattern))
    # De-duplicate (multi-depth glob can double up)
    pdfs = sorted(set(pdfs))

    print(f"found {len(pdfs)} PDFs across {len(args.pdf_dir)} directories")
    print(f"sampling {args.pages_per_pdf} random pages each at {args.dpi} DPI")
    print(f"output: {args.output}")

    total_rendered = 0
    for pdf_path in tqdm(pdfs, desc="sampling"):
        total_rendered += sample_pdf(pdf_path, args.output, args.pages_per_pdf,
                                     args.dpi, rng)

    print(f"\nrendered {total_rendered} page images from {len(pdfs)} PDFs")
    print(f"output dir: {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
