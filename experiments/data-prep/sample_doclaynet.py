"""Download a random sample of DocLayNet images via HuggingFace datasets.

DocLayNet is IBM's diverse document-layout corpus (CDLA-Permissive-1.0):
80k pages spanning scientific papers, financial reports, patents, manuals,
government docs, and legal briefs. Adds the structural diversity that
YOLO-DLA (academic-papers-only) lacks.

We only need IMAGES — DocLayNet's own labels are in a different schema
than ours, so we'll re-label with PP-DocLayoutV3 downstream.

Usage:
    python sample_doclaynet.py --output ~/Downloads/doclaynet_sample --n 5000
"""

from __future__ import annotations

import argparse
import random
from pathlib import Path

from tqdm import tqdm


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--output", type=Path, required=True)
    p.add_argument("--n", type=int, default=5000,
                   help="How many images to sample (default 5000)")
    p.add_argument("--split", type=str, default="train",
                   choices=["train", "val", "test"])
    p.add_argument("--seed", type=int, default=42)
    p.add_argument("--dataset", type=str, default="docling-project/DocLayNet-v1.1",
                   help="HuggingFace dataset id (parquet-backed; the older "
                        "ds4sd/DocLayNet uses a legacy loader script that "
                        "datasets >= 4.x no longer supports)")
    args = p.parse_args()

    args.output.mkdir(parents=True, exist_ok=True)

    # Lazy-import to avoid the heavy datasets dep when the script isn't run
    print(f"loading {args.dataset} ({args.split} split, streaming)...", flush=True)
    from datasets import load_dataset
    ds = load_dataset(args.dataset, split=args.split, streaming=True)

    # The streaming dataset doesn't expose a known length, but DocLayNet train
    # has ~69k items. We'll shuffle the stream's buffer and take the first n.
    rng = random.Random(args.seed)
    # `.shuffle(seed)` with a buffer is the standard streaming-shuffle trick
    ds = ds.shuffle(seed=args.seed, buffer_size=10_000)
    ds = ds.take(args.n)

    rendered = 0
    skipped = 0
    pbar = tqdm(total=args.n, desc="downloading")
    for i, ex in enumerate(ds):
        try:
            img = ex["image"]
            if img.mode != "RGB":
                img = img.convert("RGB")
            # Filename: deterministic by index so a re-run produces the same set
            out_path = args.output / f"doclaynet_{i:06d}.jpg"
            img.save(out_path, "JPEG", quality=88)
            rendered += 1
        except Exception as e:
            skipped += 1
            tqdm.write(f"skip {i}: {e}")
        pbar.update(1)
    pbar.close()

    print(f"\ndownloaded {rendered} images, skipped {skipped}")
    print(f"output: {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
