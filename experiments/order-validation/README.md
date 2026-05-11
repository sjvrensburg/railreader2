# XY-cut validation against PP-DocLayoutV3

Use PP-DocLayoutV3 as a reading-order oracle to debug and validate RailReader2's `XYCutSorter`. PP-DocLayoutV3 emits reading order natively via its Global Pointer Mechanism — its 7th tensor column is the order index. We treat that as ground truth, run XY-cut on the same detections, and look at where they disagree.

This is a **debugging tool, not a runtime dependency**. Nothing here ships with RailReader2; the goal is to find failure patterns in the C# XY-cut implementation and fix them.

## Setup

```bash
cd experiments/order-validation
uv venv && source .venv/bin/activate
uv pip install -e .
```

The PP-DocLayoutV3 ONNX is expected at `../../models/PP-DocLayoutV3.onnx` (already there from before the YOLO swap).

## Usage

### Visualise PP-DocLayoutV3's reading order on a PDF page

```bash
python visualise.py path/to/paper.pdf --page 1 --output ground_truth.png
```

Output: a PNG with each detected block outlined and labelled `#<order> <class> (<conf>)`. Numbers correspond to PP-DocLayoutV3's reading-order index — what we *should* be matching.

### Compare XY-cut to ground truth on a corpus

```bash
python compare.py --pdfs path/to/papers/ --output report/
```

Runs PP-DocLayoutV3 on each page, runs the bundled Python port of XY-cut (`xy_cut_port.py`) on the same detections, computes per-page Kendall τ and exact-match rate. Dumps a Markdown report plus side-by-side images for pages where τ < 0.9.

The Python XY-cut port mirrors the C# `XYCutSorter` line-for-line so changes there can be tested before being ported back.

## Workflow

1. Run `visualise.py` on the failing PDF to see what PP-DocLayoutV3 thinks the correct order is.
2. Compare against the screenshot you already captured of the RailReader2 GUI overlay.
3. Identify the specific geometric situation the C# XY-cut mishandles.
4. Patch the C# implementation; mirror the same patch in `xy_cut_port.py`.
5. Run `compare.py` over a corpus to confirm the fix doesn't regress other pages.
6. Add a regression test in `tests/RailReader.Core.Tests/XYCutSorterTests.cs` for the case.
