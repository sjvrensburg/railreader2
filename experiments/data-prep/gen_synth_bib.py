"""Generate synthetic academic-style bibliography pages from BibTeX.

Pipeline per page:
  1. Sample config (class, bibstyle, columns, font size, density, margins)
  2. Sample K (30-80) BibTeX entries from an input .bib corpus
  3. Build a tex file from a template
  4. Compile via pdflatex + bibtex (3-pass dance)
  5. Render PDF page(s) to PNG via pypdfium2

We bias the layout distribution toward the small-dense two-column
academic style that v6 failed to detect (e.g., danam_p5_references).

Usage:
    python gen_synth_bib.py \\
        --anthology /path/to/anthology.bib \\
        --output /path/to/synth_bib \\
        --n 2000
"""

from __future__ import annotations

import argparse
import io
import json
import pickle
import random
import subprocess
import sys
import tempfile
import time
from pathlib import Path

import bibtexparser
from bibtexparser.bibdatabase import BibDatabase
import pypdfium2 as pdfium
from tqdm import tqdm


# Latin codepoint cutoff. Keeps Basic Latin + Latin-1 Supplement + Latin
# Extended-A/B (covers French, German, Spanish, Czech, Turkish, Vietnamese,
# etc.) and drops CJK, Cyrillic, Greek, Arabic, Devanagari, etc. We're
# rendering with pdflatex + utf8 inputenc which only handles Latin script.
LATIN_CODEPOINT_MAX = 0x024F

# LaTeX macros that mark non-Latin content that the Unicode filter misses
# (e.g., title fields that encode Cyrillic as \CYRS, \CYRYA rather than raw
# UTF-8). Reject entries containing any of these.
NON_LATIN_LATEX_MACROS = (
    r"\CYR", r"\cyr", r"\textcyr", r"\selectcyrillic",   # Cyrillic
    r"\textgrk", r"\textgreek",                          # Greek
    r"\textchinese", r"\textjapanese", r"\textcjk",      # CJK
    r"\textarab", r"\textfarsi",                         # Arabic / Farsi
    r"\textdevanagari", r"\texthebrew",                  # Devanagari / Hebrew
)

# Fields stripped before writing the .bib file. URLs/DOIs/note often contain
# unescaped underscores or hashes that bibstyles render via \doi/\url, which
# fail under pdflatex without hyperref. The bibliography visual style does
# not need accurate URLs — we only need plausible-looking ref entries.
STRIP_FIELDS = ("url", "doi", "howpublished", "note", "eprint", "archiveprefix",
                "primaryclass", "isbn", "issn", "pdf")


# Bibstyles available with natbib + bibtex on this install. Split by
# citation mode — natbib requires matching package options (the default
# author-year mode rejects numeric bibstyles and vice versa).
NATBIB_NUMERIC_STYLES = ["plain", "abbrv", "unsrt", "alpha", "plainnat",
                         "abbrvnat", "ieeetr"]
NATBIB_AUTHORYEAR_STYLES = ["apalike", "plainnat", "abbrvnat"]

# Per-template weights — bias toward two-column / academic styles where v6
# fails. acmart dropped — its dep chain (everyshi, totpages, environ, ...)
# is incomplete in TinyTeX and the visual style is close enough to ieeetran.
TEMPLATE_WEIGHTS = {
    "article_twocol": 5,
    "article_onecol": 2,
    "ieeetran":       4,
    "elsarticle":     2,
}


def _pick_natbib_style(rng: random.Random) -> tuple[str, str]:
    """Pick a bibstyle + matching natbib package options.

    Returns (bibstyle, natbib_options_with_brackets).
    """
    # Bias 70/30 toward numeric (matches the academic-paper bibliography
    # style most common in CS / stats publications).
    if rng.random() < 0.7:
        bibstyle = rng.choice(NATBIB_NUMERIC_STYLES)
        opts = rng.choice(["[numbers,sort]", "[numbers]",
                           "[numbers,sort&compress]"])
    else:
        bibstyle = rng.choice(NATBIB_AUTHORYEAR_STYLES)
        opts = rng.choice(["[authoryear]", "[authoryear,round]",
                           "[authoryear,square]"])
    return bibstyle, opts


def build_article_twocol(rng: random.Random) -> tuple[str, dict]:
    bibstyle, nat_opts = _pick_natbib_style(rng)
    fsize = rng.choice([9, 10, 11])
    margin = rng.choice([1.5, 1.8, 2.0, 2.5])
    columnsep = rng.choice([6, 8, 10, 12])
    tex = (
        f"\\documentclass[{fsize}pt,twocolumn,a4paper]{{article}}\n"
        f"\\usepackage[margin={margin}cm,a4paper]{{geometry}}\n"
        f"\\usepackage{nat_opts}{{natbib}}\n"
        f"\\usepackage[T1]{{fontenc}}\n"
        f"\\usepackage[utf8]{{inputenc}}\n"
        f"\\setlength{{\\columnsep}}{{{columnsep}pt}}\n"
        f"\\pagestyle{{empty}}\n"
        f"\\bibliographystyle{{{bibstyle}}}\n"
        f"\\begin{{document}}\n"
        f"\\nocite{{*}}\n"
        f"\\renewcommand{{\\refname}}{{}}\n"
        f"\\bibliography{{refs}}\n"
        f"\\end{{document}}\n"
    )
    cfg = {"template": "article_twocol", "bibstyle": bibstyle,
           "natbib_opts": nat_opts,
           "font_size": fsize, "margin_cm": margin, "columnsep_pt": columnsep}
    return tex, cfg


def build_article_onecol(rng: random.Random) -> tuple[str, dict]:
    bibstyle, nat_opts = _pick_natbib_style(rng)
    fsize = rng.choice([10, 11, 12])
    margin = rng.choice([2.0, 2.5, 3.0])
    tex = (
        f"\\documentclass[{fsize}pt,a4paper]{{article}}\n"
        f"\\usepackage[margin={margin}cm,a4paper]{{geometry}}\n"
        f"\\usepackage{nat_opts}{{natbib}}\n"
        f"\\usepackage[T1]{{fontenc}}\n"
        f"\\usepackage[utf8]{{inputenc}}\n"
        f"\\pagestyle{{empty}}\n"
        f"\\bibliographystyle{{{bibstyle}}}\n"
        f"\\begin{{document}}\n"
        f"\\nocite{{*}}\n"
        f"\\renewcommand{{\\refname}}{{}}\n"
        f"\\bibliography{{refs}}\n"
        f"\\end{{document}}\n"
    )
    cfg = {"template": "article_onecol", "bibstyle": bibstyle,
           "natbib_opts": nat_opts,
           "font_size": fsize, "margin_cm": margin}
    return tex, cfg


def build_ieeetran(rng: random.Random) -> tuple[str, dict]:
    fsize = rng.choice(["9pt", "10pt", "11pt"])
    journal = rng.choice(["journal", "conference"])
    tex = (
        f"\\documentclass[{fsize},{journal},a4paper]{{IEEEtran}}\n"
        f"\\usepackage[T1]{{fontenc}}\n"
        f"\\usepackage[utf8]{{inputenc}}\n"
        f"\\pagestyle{{empty}}\n"
        f"\\bibliographystyle{{IEEEtran}}\n"
        f"\\begin{{document}}\n"
        f"\\nocite{{*}}\n"
        f"\\bibliography{{refs}}\n"
        f"\\end{{document}}\n"
    )
    cfg = {"template": "ieeetran", "bibstyle": "IEEEtran",
           "font_size": fsize, "ieeetran_mode": journal}
    return tex, cfg


def build_elsarticle(rng: random.Random) -> tuple[str, dict]:
    # elsarticle has many model options; "5p" is two-column with abstract,
    # "3p" is single-column. We pick a bibstyle from natbib-compatible ones.
    model = rng.choice(["5p", "3p"])
    bibstyle = rng.choice(["elsarticle-num", "elsarticle-harv",
                           "elsarticle-num-names"])
    tex = (
        f"\\documentclass[{model},sort&compress,a4paper]{{elsarticle}}\n"
        f"\\usepackage[T1]{{fontenc}}\n"
        f"\\usepackage[utf8]{{inputenc}}\n"
        f"\\pagestyle{{empty}}\n"
        f"\\bibliographystyle{{{bibstyle}}}\n"
        f"\\begin{{document}}\n"
        f"\\nocite{{*}}\n"
        f"\\bibliography{{refs}}\n"
        f"\\end{{document}}\n"
    )
    cfg = {"template": "elsarticle", "bibstyle": bibstyle, "elsarticle_model": model}
    return tex, cfg


TEMPLATE_BUILDERS = {
    "article_twocol": build_article_twocol,
    "article_onecol": build_article_onecol,
    "ieeetran":       build_ieeetran,
    "elsarticle":     build_elsarticle,
}


def sample_template(rng: random.Random) -> str:
    names = list(TEMPLATE_WEIGHTS.keys())
    weights = [TEMPLATE_WEIGHTS[n] for n in names]
    return rng.choices(names, weights=weights, k=1)[0]


def is_latin_only(entry: dict) -> bool:
    """True if all major bib fields are Latin script and contain no
    non-Latin LaTeX escape macros.

    pdflatex+utf8 inputenc can't render CJK / Cyrillic / Greek / etc.
    Those entries cause compile failures.
    """
    fields = ("author", "title", "journal", "booktitle", "editor",
              "publisher", "address")
    for f in fields:
        v = entry.get(f, "")
        for ch in v:
            if ord(ch) > LATIN_CODEPOINT_MAX:
                return False
        for macro in NON_LATIN_LATEX_MACROS:
            if macro in v:
                return False
    return True


def sanitise_entry(entry: dict) -> dict:
    """Return a copy of the entry with URL/DOI/note fields stripped.

    These commonly contain unescaped underscores or hashes that crash
    pdflatex when rendered by \\doi / \\url. We don't need accurate URLs
    for the visual training corpus.
    """
    return {k: v for k, v in entry.items() if k.lower() not in STRIP_FIELDS}


def entries_to_bibtex(entries: list[dict]) -> str:
    """Build a .bib file string from a sampled subset of entries."""
    db = BibDatabase()
    db.entries = [sanitise_entry(e) for e in entries]
    out = io.StringIO()
    bibtexparser.dump(db, out)
    return out.getvalue()


def load_or_parse_bibtex(bib_path: Path, cache_dir: Path | None = None
                          ) -> list[dict]:
    """Load BibTeX entries, caching the parsed result to a pickle.

    bibtexparser takes 15+ minutes to parse the 84 MB anthology.bib. Cache
    the parsed entries to a pickle so subsequent runs load in seconds. Cache
    is keyed by source file mtime; rebuild if source is newer.
    """
    if cache_dir is None:
        cache_dir = bib_path.parent
    cache = cache_dir / f"{bib_path.stem}.entries.pkl"
    if cache.exists() and cache.stat().st_mtime > bib_path.stat().st_mtime:
        print(f"  loading cached entries from {cache}", flush=True)
        with open(cache, "rb") as f:
            return pickle.load(f)
    print(f"  parsing {bib_path} (no cache — this is slow, ~15min)...",
          flush=True)
    t0 = time.time()
    with open(bib_path, encoding="utf-8", errors="ignore") as f:
        db = bibtexparser.load(f)
    print(f"  parsed {len(db.entries)} entries in {time.time()-t0:.1f}s")
    print(f"  caching to {cache} ...")
    with open(cache, "wb") as f:
        pickle.dump(db.entries, f)
    return db.entries


def compile_latex(tex_text: str, bib_text: str, workdir: Path,
                  engine: str = "pdflatex",
                  timeout: int = 60) -> Path | None:
    """Run engine + bibtex + 2x engine. Returns Path to PDF or None on failure."""
    (workdir / "main.tex").write_text(tex_text)
    (workdir / "refs.bib").write_text(bib_text)

    pdflatex_cmd = [engine, "-interaction=nonstopmode", "-halt-on-error",
                    "-no-shell-escape", "main.tex"]
    bibtex_cmd = ["bibtex", "main"]

    # Pass 1: build .aux with citation info
    # Pass 2: bibtex resolves citations → .bbl
    # Pass 3: build doc with .bbl
    # Pass 4: settle cross-refs / page numbers
    for cmd in (pdflatex_cmd, bibtex_cmd, pdflatex_cmd, pdflatex_cmd):
        try:
            subprocess.run(cmd, cwd=workdir, capture_output=True, timeout=timeout)
        except subprocess.TimeoutExpired:
            return None
    pdf = workdir / "main.pdf"
    return pdf if pdf.exists() and pdf.stat().st_size > 0 else None


def render_pdf_pages(pdf_path: Path, output_dir: Path, base_name: str,
                     dpi: int = 150, max_pages: int = 3) -> list[Path]:
    """Render up to max_pages pages of pdf_path to PNG. Returns paths."""
    doc = pdfium.PdfDocument(str(pdf_path))
    try:
        out_paths = []
        n = min(len(doc), max_pages)
        for i in range(n):
            page = doc[i]
            img = page.render(scale=dpi / 72.0).to_pil().convert("RGB")
            path = output_dir / (f"{base_name}.png" if n == 1
                                  else f"{base_name}_p{i:02d}.png")
            img.save(path)
            out_paths.append(path)
        return out_paths
    finally:
        doc.close()


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--anthology", type=Path, required=True,
                   help="Input BibTeX file (e.g. anthology.bib)")
    p.add_argument("--output", type=Path, required=True,
                   help="Output dir for PNG pages + manifest.json")
    p.add_argument("--n", type=int, default=2000,
                   help="Number of pages to generate")
    p.add_argument("--min-refs", type=int, default=30)
    p.add_argument("--max-refs", type=int, default=80)
    p.add_argument("--max-pages-per-doc", type=int, default=2,
                   help="If a doc spans multiple PDF pages, save up to this "
                        "many. Sometimes a 60-ref two-column doc fits on 1 "
                        "page, sometimes 2. Single doc → multiple PNGs.")
    p.add_argument("--seed", type=int, default=42)
    p.add_argument("--keep-tmp", action="store_true",
                   help="Keep LaTeX intermediate files (debugging)")
    p.add_argument("--prefix", default="synth_bib",
                   help="Filename prefix for output images")
    args = p.parse_args()

    args.output.mkdir(parents=True, exist_ok=True)

    print(f"loading {args.anthology}...", flush=True)
    entries = load_or_parse_bibtex(args.anthology)

    # Filter to types that look like real references (skip @proceedings,
    # @book that may have weird structure, @comment, etc.) AND Latin-only
    # text (pdflatex+utf8 inputenc can't render CJK/Cyrillic/etc.)
    usable_types = {"article", "inproceedings", "conference", "techreport",
                    "phdthesis", "misc", "incollection"}
    pool = [e for e in entries
            if e.get("ENTRYTYPE", "").lower() in usable_types
            and "author" in e and "title" in e
            and is_latin_only(e)]
    print(f"  {len(pool)} usable Latin-script entries after filtering")
    if len(pool) < args.max_refs * 2:
        print(f"  WARN: pool too small for diversity")

    rng = random.Random(args.seed)
    manifest = []
    n_success = 0
    n_compile_fail = 0

    pbar = tqdm(range(args.n), desc="generating")
    for i in pbar:
        template_name = sample_template(rng)
        builder = TEMPLATE_BUILDERS[template_name]
        tex, cfg = builder(rng)

        n_refs = rng.randint(args.min_refs, args.max_refs)
        sampled = rng.sample(pool, k=min(n_refs, len(pool)))
        bib_text = entries_to_bibtex(sampled)

        base_name = f"{args.prefix}_{i:06d}"
        with tempfile.TemporaryDirectory(prefix="synthbib_") as td:
            workdir = Path(td)
            pdf = compile_latex(tex, bib_text, workdir)
            if pdf is None:
                n_compile_fail += 1
                # Save tex + bib for first 3 failures to help debug
                if n_compile_fail <= 3 and args.keep_tmp:
                    fail_dir = args.output / "_fail" / base_name
                    fail_dir.mkdir(parents=True, exist_ok=True)
                    (fail_dir / "main.tex").write_text(tex)
                    (fail_dir / "refs.bib").write_text(bib_text)
                continue

            paths = render_pdf_pages(pdf, args.output, base_name,
                                     max_pages=args.max_pages_per_doc)
        for path_idx, path in enumerate(paths):
            manifest.append({
                "file": path.name,
                "template": template_name,
                "n_refs_total": len(sampled),
                "page_idx": path_idx,
                "n_pages_total": len(paths),
                **cfg,
            })
            n_success += 1

        if (i + 1) % 50 == 0:
            pbar.set_postfix(saved=n_success, fail=n_compile_fail)

    (args.output / "manifest.json").write_text(json.dumps(manifest, indent=2))
    print(f"\ngenerated {n_success} pages (from {args.n} docs)")
    print(f"  compile failures: {n_compile_fail}")
    by_template = {}
    for m in manifest:
        by_template[m["template"]] = by_template.get(m["template"], 0) + 1
    print(f"  by template: {by_template}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
