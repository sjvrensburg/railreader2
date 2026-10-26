# Reference-resolution labels

Ground truth for `RailReader2.Cli references --ground-truth`. Labels were made by hand: mentions
from `pdftotext`, targets checked against the PDF's captions and rendered pages (not copied from tool
output). Block indices are Docling Heron (INT8) block positions, so a layout-model change can
invalidate them; re-check against `structure --analyze` when it does.

The PDFs themselves are not in the repo (`experiments/PDFs/` is gitignored). Run:

    ./scripts/references-benchmark.sh [--min-precision 0.95]

| Paper | Mentions | Notes |
|---|---|---|
| Attention_is_all_you_need | 10 | ML, two-column, 4 tables, multi-panel Figure 2 (`also_accept_blocks`) |
| Medvigy-TrendsDailySolar-2012 | 22 | climate, `FIG. N.` captions, panel mentions ("Fig. 2a") |
| Breusch-SimpleTestHeteroscedasticity-1979 | 5 | econometrics, roman-numeral `TABLE I` captions |
| applsci-15-05957 | 21 | MDPI survey, 18 figures, stacked floats with captions below, "Figures 5 and 6" plurals, table continued over two pages |
| Foundation Models ... Tutorial and Survey | 7 | two-column, float on the first page, full-page taxonomy figure |
| 2504.09663v2 | 4 | arXiv, appendix Table 1 continued over three pages ("Table 1 (continued)") |
| murphy-pml1-ch3 | 23 | Murphy, *Probabilistic Machine Learning: An Introduction*, chapter 3 only (`Figure 3.x` labels, forward and backward references, two dangling cross-chapter references that must abstain) |
| svd_ols_ridge_pcr | 0 | negative control: lecture notes with no floats; every page must resolve nothing |

`murphy-pml1-ch3.pdf` is a local excerpt of the book's CC-BY-NC-ND draft (not redistributed here):

    curl -L -o book1.pdf https://github.com/probml/pml-book/releases/latest/download/book1.pdf
    qpdf book1.pdf --pages . 107-136 -- experiments/PDFs/murphy-pml1-ch3.pdf

(`pdfseparate`/`pdfunite` produce a 125 MB file; `qpdf` gives 3.7 MB.) Murphy numbers floats by
chapter-section, so labels are unique across the whole book: it tests scale, long-distance
references and dangling labels, not repeated labels.

## Current result (Heron INT8)

Precision 1.00 on every paper except applsci (0.95); Murphy ch. 3 is clean (21/21, both dangling references abstained). Recall 1.00 except Medvigy (0.50) and applsci (0.95).

- **Panel mentions** (Medvigy, 11 misses): `Fig. 2a` has number `2a`, which never matches caption `2`.
- **Stacked floats** (applsci, 1 false positive): Figure 17 and Figure 18 plots are stacked with
  captions below. Figure 17's caption is nearer the *next* plot by a few points, so it resolves to
  Figure 18's plot. The caption sits below its own float, so a below-caption/above-caption
  preference would fix it.

## Limits

- Both baselines tie the resolver everywhere, including the applsci error: they share its
  nearest-float geometry. They only differ on label choice (first vs nearest caption), which this
  corpus never exercises. A baseline that picks the nearest float by plain distance, and documents
  with genuinely repeated labels (per-chapter numbering with no chapter prefix, appendices
  restating "Table 1") are needed before
  the comparison says anything.
- Nothing yet tests abstention on ambiguous labels, only on pages with no mentions.
- Scanned pages (OCR) are not covered: `references` uses the PDF text layer.
