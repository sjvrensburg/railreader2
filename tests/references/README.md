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
| murphy-pml1-ch3 | 29 | Murphy, *Probabilistic Machine Learning: An Introduction*, chapter 3 only (`Figure 3.x` labels, forward and backward references, two dangling cross-chapter references that must abstain, six `Exercise 3.x` mentions) |
| eptcs-scss2021-proceedings | 96 | EPTCS full volume (CC-BY, 141 pp., 11 papers): **genuinely repeated labels**, Figure 1 on 8 pages, Table 1 on 4. Numbering restarts per paper. 52 pages carry mentions, 89 must resolve nothing. Includes 45 **statement** mentions (Definition/Example/Theorem/Lemma/Proposition/Remark; one foreign `Theorem 1.4 in [2]` that must abstain) |
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

Precision is 1.00 on every document. Recall is 1.00 on all but the EPTCS volume (0.92) and Murphy chapter 3 (0.89).

History of the three fixes this set motivated (before → after):

| Document | Before | After | Fix |
|---|---|---|---|
| Medvigy | P 1.00, R 0.50 | P 1.00, R 1.00 | panel mentions (`Fig. 2a` → Figure 2) |
| applsci | P 0.95, R 0.95 | P 1.00, R 1.00 | one-to-one caption↔float binding (stacked floats) |
| EPTCS | P 0.84, R 0.84 | P 1.00, R 0.86 | 12-page cap on flat numbers; nearest page never skipped |

Two further bugs surfaced by the EPTCS volume: `Resolve` could pass over a nearer page it had no
indexing budget for and return a farther cached match, and the cap made a negative-cache entry
position-dependent (a miss near one page must not hide a float near another). Both have tests.

### Equations

Labels were written **before** equation support existed: declared tags from `pdftotext -bbox-layout`
(a parenthesised number in a display block, at the right margin in three documents and the *left*
margin in Breusch 1979), mentions from the PDF text. The first, deliberately plain implementation
(cue words + a bare `(N)` after a word, validated against declared tags only) scored P 1.00 / R 1.00
on all 19 equation mentions in Attention, Breusch, arXiv 2504.09663, and Murphy ch. 3, including the
negatives (`O(1)`, `AR(1)`, `(2017)`, `(0.52)`, journal issues). Running it over the EPTCS volume
then found six genuine `Eq. (N)` mentions I had not labelled; they are labelled now (6/6 correct).

Rules added afterwards from reading Cauchy's source, **not** from a corpus failure, so they are
covered by synthetic hard-negative unit tests only: a bare `(1)` that opens its clause (list
marker / proof step) is not a mention; results named inside `[IK99, Lemma 1.15]`, `[2, eqs. (5) and
(6)]`, `(Smith 2019, Theorem 3)` belong to the cited work (an interval `[0, 1]` is not a citation);
`Theorem (3.2)` is a statement; dot-leader contents entries are not statement headers. Labels carry
the PDF's SHA-256 and the runner refuses a different file.

Honest scope: five documents, all of which I have looked at while building this; the equation
numbers in four of them are flat (restart per paper), so the 12-page cap does real work. Not
covered: equations numbered inside `align` environments with several tags in one detected block
(they all map to that block), tags the detector does not type as display maths, scans.

### Statements (Definition/Theorem/Example/…)

On the EPTCS volume 44 of 45 statement mentions resolve correctly and none wrongly. The one miss is
a mention split by a soft hyphen (`Re\x02mark 2.15`). Murphy's four missed `Exercise 3.1–3.3` mentions
abstain because the detector typed the whole exercise list as one `Algorithm` block. Earlier runs
found three real bugs here, each now covered by a test: a header's own line hiding a later genuine
mention on the page (benchmark scan), a mention wrapped to the start of a line being read as a
header (`…are given in⏎Remark 2.15. We`), and headers typed `Heading`/`Caption` not being indexed.

Only two documents contain statements, so this is the thinnest-validated part: the toggle ships off.

Remaining EPTCS figure/table misses (7, all abstentions): the detector gave a figure's content a `Table` or
`Algorithm` role, or a "table" holding an XML listing an `Algorithm` role, so no float of the right
kind sits by the caption. That is a detector limit; the resolver now abstains instead of binding
the wrong float. Closing it would need captions to accept any non-text block as their float.

## Unlabelled holdout audit (110 unseen arXiv papers)

Hand-labelling does not scale, so four batches of **October 2026** arXiv papers (math.ST, stat.ME,
math.PR, cs.LG, econ.EM, math.OC, stat.ML, newest-first at four different offsets, 10–60 pages, no
overlap; 110 papers / 3,489 pages) were run with no labels, and a **stratified sample of resolved
mentions was read by eye** (mention context beside the start of the block it resolved to; every
multi-candidate resolution included). The PDFs are not redistributed.

| Batch | Docs / pages | Mentions | Resolved | Sample read | Wrong, before that batch's fixes |
|---|---|---|---|---|---|
| A | 30 / 935 | 2,725 | 2,285 (84%) | 218 (33 multi-candidate) | ~20 (9%; the multi-candidate rows were oversampled) |
| B | 26 / 840 | 1,803 | 1,391 (77%) | 180 | 4 (2%) |
| C | 28 / 862 | 1,985 | 1,604 (81%) | 189 | ~7 (4%) |
| D | 26 / 852 | 2,063 | 1,620 (79%) | 210 | ~4 (2%) |

Every batch was audited *before* its findings were fixed, so each row is the error rate on documents
the code had not been tuned against; each batch still turned up one or two new patterns. Expect
roughly 2–4 wrong pins in 100 on a new document (mostly rare patterns), not zero. Fixed patterns
(each with a unit test): contents lines read as mentions; `Proposition 1(d).` read as a header;
superscript indices (`µ (2)`), numbered conditions (`conditions (1) and (3)`, `(1)–(3)`),
asymptotic notation (`oP (1)`, `log (2)`) and a conjunction-led list item (`…cost, and (2) time`)
read as equations; mentions merged into a display block read as tags; an equation number defined
twice in the window; appendix-lettered headers (`Lemma C.1.`) not indexed; `Tables A-2 and B-1`
parsed as a range; non-ASCII citation keys; multi-author citation parentheticals
(`(Coogan & Arcak, 2015, Def. 1)`); summary bullets (`• Theorem 4.1 (…):`) read as the theorem.

Recall (abstentions where an independent text search finds the label): most abstentions are
dangling references to other works. About a quarter of batch B's 412 abstentions were flat-numbered
references more than 12 pages from their target, which the fixed cap dropped to protect proceedings
volumes. The cap is now **adaptive**: it applies only to documents whose flat labels restart
(a label defined on two pages more than 12 apart) or whose analysis is unfinished. Re-running
batch D: resolved 1,620 → 1,757 of 2,069 mentions (+132; 63 equations, 19 figures, 18 lemmas,
13 theorems, 12 tables, 5 propositions, 2 remarks), and I read all 133 newly resolved mentions
(one more, an equation defined twice, correctly went back to abstaining after the check beyond the
window was added): all but that one were right: appendix equations cited from proofs, statements
named in proof headings, appendix figures and tables. The nine labelled documents are unchanged
(precision 1.00; the EPTCS volume keeps its cap because its restarts are detected).

## Citations

Citations (`[12]`, `[3, 7–9]`, `[IK99]`, `Smith et al. (2019)`, `(Smith and Jones, 2019a; Lee, 2020)`)
resolve to the matching entry of the document's reference list. The list is found by its heading
and runs to the next heading; a citation resolves to the next list after it, and abstains when that
is ambiguous (a proceedings volume with one list per paper) or more than 25 pages away. Author-year
citations are matched on first-author surname and year, with the author count (single, two, et al.)
told apart; an unsuffixed year matching 2024a and 2024b abstains.

Labelled citations: Attention, Breusch, applsci, the foundation-model survey, 2504.09663v2, Medvigy,
the EPTCS volume (243 labels) and svd. Precision is 1.00 on all of them; recall is 1.00 except the EPTCS
volume (the first pages of a paper right after another paper's list, and one `X [8]` form not detected).

Unseen batch D (26 arXiv papers): read by eye from random samples of resolved citations,
numeric and author-year. Findings that became rules and tests: intervals such as `[2, 9]` after a
math symbol or "in/on" are not citations; DOIs and arXiv ids are not publication years;
initials-first entries (`G. Qu and A. Wierman. … 2020.`) and AMS "and Coauthors" are parsed. The
entry parser change resolved 86 more author-year citations, which I read: all were right.

Limits: author-year matching depends on the entry parser reading the first author and year, so
unusual entry layouts abstain; multi-list volumes depend on detecting each paper's first section.

## Limits

- On the single-paper documents the baselines tie or trail the resolver. The EPTCS volume separates
  them: `first_match` 0.27, `nearest_match` P 0.86 (7 wrong answers) against the resolver's P 1.00.
- The 12-page cap was chosen from the EPTCS volume's error distances (13–28 pages) and the longest
  legitimate flat reference in this set (7 pages, appendix tables). It is not tuned beyond that; a
  long thesis with flat numbering and a 40-page-away appendix table would now abstain.
- Nothing yet tests abstention on ambiguous labels, only on pages with no mentions.
- Scanned pages (OCR) are not covered: `references` uses the PDF text layer.
