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

Current result (Heron): precision 1.00 on all three. Recall 1.00 on Attention and Breusch, 0.50 on
Medvigy. All 11 misses are **panel mentions** (`Fig. 2a` → number `2a` never matches caption `2`).

Known limits: the three papers are easy, and both baselines (first/nearest caption match) tie the
resolver on them. The set needs harder documents — repeated numbering, dangling references, scans —
before it can show the resolver beating a naive match.
