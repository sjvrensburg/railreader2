"""PP-DocLayoutV3 (25 classes) → TinyLayoutYOLO runtime (16 classes) remap.

Lets us use PP-DocLayoutV3 as a teacher labeller on any document corpus
(PubLayNet, DocLayNet, our own user PDFs, etc.) and produce YOLO-format
labels in our runtime schema for training v4+ on broader data.

Rationale per class is in the README. Three classes are intentionally
*dropped* (not just remapped) because they have no direct analog in our
schema and including them would over-fragment training:

    formula_number   inline equation tags — too small/inline
    inline_formula   inline math — lives inside `paragraph` for us
    seal             Chinese-doc stamps — irrelevant for English content

The schemas are version-locked to:
  * PP-DocLayoutV3 inference.yml as bundled in models/PP-DocLayoutV3.onnx
  * TinyLayoutYOLO v2 runtime mapping in LayoutConstants.cs
"""

from __future__ import annotations


# PP-DocLayoutV3's 25-class schema (the order it's exported in).
# Source: https://huggingface.co/PaddlePaddle/PP-DocLayoutV3 inference.yml
PP_DOCLAYOUTV3_CLASSES = [
    "abstract",          #  0
    "algorithm",         #  1
    "aside_text",        #  2
    "chart",             #  3
    "content",           #  4
    "display_formula",   #  5
    "doc_title",         #  6
    "figure_title",      #  7
    "footer",            #  8
    "footer_image",      #  9
    "footnote",          # 10
    "formula_number",    # 11
    "header",            # 12
    "header_image",      # 13
    "image",             # 14
    "inline_formula",    # 15
    "number",            # 16
    "paragraph_title",   # 17
    "reference",         # 18
    "reference_content", # 19
    "seal",              # 20
    "table",             # 21
    "text",              # 22
    "vertical_text",     # 23
    "vision_footnote",   # 24
]


# TinyLayoutYOLO runtime schema. Mirror of LayoutConstants.LayoutClasses.
# Indexes are RUNTIME ids (what the ONNX emits), not the dataset YAML ids
# from the original YOLO-DLA repo.
TINY_RUNTIME_CLASSES = [
    "t",          #  0
    "t1",         #  1  top-level heading
    "t2",         #  2  subsection heading
    "t3",         #  3  sub-subsection (effectively unused)
    "paragraph",  #  4  BODY TEXT (primary)
    "author",     #  5  byline
    "keyword",    #  6  keywords list
    "abstract",   #  7
    "reference",  #  8  bibliography entries
    "graph",      #  9  figure
    "note",       # 10  caption (figure/table)
    "other",      # 11  page furniture
    "formula",    # 12  display equation
    "table",      # 13
    "footnote",   # 14
    "class17",    # 15  garbage — never assigned via remap
]


# The remap. None means DROP this class — don't write a label line for it.
# Source: detailed rationale in experiments/data-prep/README.md
PP_TO_TINY: dict[int, int | None] = {
    0:  7,     # abstract → abstract
    1:  12,    # algorithm → formula (math-display-style content)
    2:  11,    # aside_text → other (margin / sidebar)
    3:  9,     # chart → graph
    4:  4,     # content → paragraph (TOC / generic body)
    5:  12,    # display_formula → formula
    6:  1,     # doc_title → t1
    7:  10,    # figure_title → note (caption text)
    8:  11,    # footer → other
    9:  11,    # footer_image → other
    10: 14,    # footnote → footnote
    11: None,  # formula_number → DROP (inline tag, would over-fragment)
    12: 11,    # header → other
    13: 11,    # header_image → other
    14: 9,     # image → graph
    15: None,  # inline_formula → DROP (inline math lives inside paragraph)
    16: 11,    # number → other (page numbers)
    17: 2,     # paragraph_title → t2
    18: 8,     # reference → reference (bibliography heading)
    19: 8,     # reference_content → reference (entries)
    20: None,  # seal → DROP (Chinese-doc stamps, irrelevant)
    21: 13,    # table → table
    22: 4,     # text → paragraph
    23: 11,    # vertical_text → other (rotated margin text)
    24: 14,    # vision_footnote → footnote
}


def remap(pp_class_id: int) -> int | None:
    """Map a PP-DocLayoutV3 class id to a TinyLayoutYOLO runtime id.

    Returns None if the class should be dropped (formula_number,
    inline_formula, or seal). Callers should skip None-mapped detections.
    """
    return PP_TO_TINY.get(pp_class_id)


def describe_mapping() -> str:
    """Human-readable mapping summary for documentation / sanity checks."""
    lines = [f"{'PP idx':>6}  {'PP name':<20}  →  {'tiny idx':>8}  tiny name"]
    lines.append("-" * 60)
    for pp_id, pp_name in enumerate(PP_DOCLAYOUTV3_CLASSES):
        tiny_id = PP_TO_TINY.get(pp_id)
        if tiny_id is None:
            lines.append(f"{pp_id:>6}  {pp_name:<20}  →  {'(drop)':>8}")
        else:
            lines.append(f"{pp_id:>6}  {pp_name:<20}  →  {tiny_id:>8}  {TINY_RUNTIME_CLASSES[tiny_id]}")
    return "\n".join(lines)


if __name__ == "__main__":
    print(describe_mapping())
