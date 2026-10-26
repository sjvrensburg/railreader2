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
| eptcs-scss2021-proceedings | 51 | EPTCS full volume (CC-BY, 141 pp., 11 papers): **genuinely repeated labels**, Figure 1 on 8 pages, Table 1 on 4. Numbering restarts per paper. 33 pages carry mentions, 108 must resolve nothing |
| svd_ols_ridge_pcr | 0 | negative control: lecture notes with no floats; every page must resolve nothing |

`murphy-pml1-ch3.pdf` is a local excerpt of the book's CC-BY-NC-ND draft (not redistributed here):

    curl -L -o book1.pdf https://github.com/probml/pml-book/releases/latest/download/book1.pdf
    qpdf book1.pdf --pages . 107-136 -- experiments/PDFs/murphy-pml1-ch3.pdf

(`pdfseparate`/`pdfunite` produce a 125 MB file; `qpdf` gives 3.7 MB.) Murphy numbers floats by
chapter-section, so labels are unique across the whole book: it tests scale, long-distance
references and dangling labels, not repeated labels.

`eptcs-scss2021-proceedings.pdf` is the unmodified full volume from
`https://eptcs.org/Published/SCSS2021/Proceedings.pdf` (Creative Commons Attribution; not in the repo).

## Current result (Heron INT8)

Precision is 1.00 on every document. Recall is 1.00 on all but the EPTCS volume (0.86).

History of the three fixes this set motivated (before → after):

| Document | Before | After | Fix |
|---|---|---|---|
| Medvigy | P 1.00, R 0.50 | P 1.00, R 1.00 | panel mentions (`Fig. 2a` → Figure 2) |
| applsci | P 0.95, R 0.95 | P 1.00, R 1.00 | one-to-one caption↔float binding (stacked floats) |
| EPTCS | P 0.84, R 0.84 | P 1.00, R 0.86 | 12-page cap on flat numbers; nearest page never skipped |

Two further bugs surfaced by the EPTCS volume: `Resolve` could pass over a nearer page it had no
indexing budget for and return a farther cached match, and the cap made a negative-cache entry
position-dependent (a miss near one page must not hide a float near another). Both have tests.

Remaining EPTCS misses (7, all abstentions): the detector gave a figure's content a `Table` or
`Algorithm` role, or a "table" holding an XML listing an `Algorithm` role, so no float of the right
kind sits by the caption. That is a detector limit; the resolver now abstains instead of binding
the wrong float. Closing it would need captions to accept any non-text block as their float.

## Limits

- On the single-paper documents the baselines tie or trail the resolver. The EPTCS volume separates
  them: `first_match` 0.27, `nearest_match` P 0.86 (7 wrong answers) against the resolver's P 1.00.
- The 12-page cap was chosen from the EPTCS volume's error distances (13–28 pages) and the longest
  legitimate flat reference in this set (7 pages, appendix tables). It is not tuned beyond that; a
  long thesis with flat numbering and a 40-page-away appendix table would now abstain.
- Nothing yet tests abstention on ambiguous labels, only on pages with no mentions.
- Scanned pages (OCR) are not covered: `references` uses the PDF text layer.
