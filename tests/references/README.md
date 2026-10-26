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

Precision 1.00 on every paper except applsci (0.95); Murphy ch. 3 is clean (21/21, both dangling references abstained). Recall 1.00 except Medvigy (0.50) and applsci (0.95).

- **Panel mentions** (Medvigy, 11 misses): `Fig. 2a` has number `2a`, which never matches caption `2`.
- **Stacked floats** (applsci, 1 false positive): Figure 17 and Figure 18 plots are stacked with
  captions below. Figure 17's caption is nearer the *next* plot by a few points, so it resolves to
  Figure 18's plot. The caption sits below its own float, so a below-caption/above-caption
  preference would fix it.

### EPTCS proceedings (the repeated-label case)

Resolver P=0.84 R=0.84, `first_match` 0.27, `nearest_match` 0.86 (8 errors, all precision losses):

- **No document boundary** (6 errors): a paper whose own caption isn't registered resolves to another
  paper's same-numbered float, 13–20 pages away (Hydra paper's "Figure 1"/"Figure 4" → the Hadoop
  and benchmark papers). Abstaining when the nearest match is far away would turn these into
  abstentions instead of wrong answers.
- **Role-gated float choice** (2 errors): a Figure whose content the detector labelled
  `Table`/`Algorithm` is never bound; a Table 3 holding an XML listing (`Algorithm` block) binds to
  the next real `Table`. Detector errors, but the resolver should not turn them into wrong targets.
- A forward reference to an appendix table (`Table 2 in the Appendix`) resolves to an earlier paper's
  Table 2 because that one is nearer in page distance.

## Limits

- On the single-paper documents both baselines tie the resolver (they share its nearest-float
  geometry). Only the EPTCS volume separates them, and there `nearest_match` is marginally better
  than the resolver (0.86 vs 0.84): the baselines have no role gate. A baseline that picks the nearest
  float by plain distance would be a useful third one.
- Nothing yet tests abstention on ambiguous labels, only on pages with no mentions.
- Scanned pages (OCR) are not covered: `references` uses the PDF text layer.
