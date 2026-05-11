"""Python port of XYCutSorter from src/RailReader.Core/Services/XYCutSorter.cs.

Mirrors the C# implementation line-for-line so the validation harness exercises
the same algorithm RailReader2 ships at runtime. Coordinates are normalised
[0, 1], same as YoloBox / TeacherBox.

NOTE: the C# version uses PDF points (612x792 page → 5pt = 0.008 of page width).
Here we work in normalised coordinates, so thresholds are scaled accordingly:
PDF_PAGE_REFERENCE = 612pt; MinGapThreshold = 2pt → 2/612 ≈ 0.00327 normalised.
"""

from __future__ import annotations

from dataclasses import dataclass, replace
from typing import TypeVar


# Constants matching the C# XYCutSorter
DEFAULT_BETA = 0.7
DEFAULT_DENSITY_THRESHOLD = 0.9
OVERLAP_THRESHOLD = 0.1
MIN_OVERLAP_COUNT = 2
TALL_NARROW_HEIGHT_RATIO = 0.7
TALL_NARROW_WIDTH_RATIO = 0.1
WIDE_SHORT_HEIGHT_RATIO = 0.3
NARROW_ELEMENT_WIDTH_RATIO = 0.1

# 2pt on a 612pt-wide reference page → normalised
PDF_PAGE_REFERENCE = 612.0
MIN_GAP_THRESHOLD = 2.0 / PDF_PAGE_REFERENCE


T = TypeVar("T")


def _bbox(b) -> tuple[float, float, float, float]:
    """Return (x, y, w, h) from any object with those attributes."""
    return b.x, b.y, b.w, b.h


def _right(b): return b.x + b.w
def _bottom(b): return b.y + b.h
def _cx(b): return b.x + b.w / 2
def _cy(b): return b.y + b.h / 2


# ===== Phase 1: cross-layout pre-mask =====

def _h_overlap_ratio(a, b) -> float:
    ol = max(a.x, b.x)
    orr = min(_right(a), _right(b))
    ow = max(0.0, orr - ol)
    if ow <= 0:
        return 0.0
    smaller = min(a.w, b.w)
    return ow / smaller if smaller > 0 else 0.0


def _v_overlap_ratio(a, b) -> float:
    ot = max(a.y, b.y)
    ob = min(_bottom(a), _bottom(b))
    oh = max(0.0, ob - ot)
    if oh <= 0:
        return 0.0
    smaller = min(a.h, b.h)
    return oh / smaller if smaller > 0 else 0.0


def _has_min_h_overlaps(element, blocks: list, min_count: int) -> bool:
    count = 0
    for b in blocks:
        if b is element:
            continue
        if _h_overlap_ratio(element, b) >= OVERLAP_THRESHOLD:
            count += 1
            if count >= min_count:
                return True
    return False


def _has_min_v_overlaps(element, blocks: list, min_count: int) -> bool:
    count = 0
    for b in blocks:
        if b is element:
            continue
        if _v_overlap_ratio(element, b) >= OVERLAP_THRESHOLD:
            count += 1
            if count >= min_count:
                return True
    return False


def _identify_cross_layout(blocks: list, beta: float) -> list:
    if len(blocks) < 3:
        return []
    max_w = max(b.w for b in blocks)
    max_h = max(b.h for b in blocks)
    width_threshold = beta * max_w
    short_threshold = WIDE_SHORT_HEIGHT_RATIO * max_h
    tall_threshold = TALL_NARROW_HEIGHT_RATIO * max_h
    narrow_threshold = TALL_NARROW_WIDTH_RATIO * max_w

    cross_layout = []
    for b in blocks:
        is_wide = (b.w >= width_threshold and b.h <= short_threshold
                   and _has_min_h_overlaps(b, blocks, MIN_OVERLAP_COUNT))
        is_tall_narrow = (not is_wide
                          and b.h >= tall_threshold and b.w <= narrow_threshold
                          and _has_min_v_overlaps(b, blocks, MIN_OVERLAP_COUNT))
        if is_wide or is_tall_narrow:
            cross_layout.append(b)
    return cross_layout


# ===== Phase 2: density ratio =====

def _bounding_region(blocks: list) -> tuple[float, float, float, float] | None:
    if not blocks:
        return None
    min_x = min(b.x for b in blocks)
    min_y = min(b.y for b in blocks)
    max_x = max(_right(b) for b in blocks)
    max_y = max(_bottom(b) for b in blocks)
    if max_x <= min_x or max_y <= min_y:
        return None
    return min_x, min_y, max_x - min_x, max_y - min_y


def _density_ratio(blocks: list) -> float:
    region = _bounding_region(blocks)
    if region is None:
        return 1.0
    area = region[2] * region[3]
    if area <= 0:
        return 1.0
    content = sum(b.w * b.h for b in blocks)
    return min(1.0, content / area)


# ===== Phase 3: recursive segmentation =====

def _h_cut_by_edges(blocks: list) -> tuple[float, float]:
    """Returns (position, gap)."""
    if len(blocks) < 2:
        return 0.0, 0.0
    sorted_b = sorted(blocks, key=lambda b: (b.y, _bottom(b)))
    largest_gap = 0.0
    cut_pos = 0.0
    prev_bottom = None
    for b in sorted_b:
        top = b.y
        bot = _bottom(b)
        if prev_bottom is not None and prev_bottom < top:
            gap = top - prev_bottom
            if gap > largest_gap:
                largest_gap = gap
                cut_pos = (prev_bottom + top) / 2.0
        prev_bottom = bot if prev_bottom is None else max(prev_bottom, bot)
    return cut_pos, largest_gap


def _v_cut_by_edges(blocks: list) -> tuple[float, float]:
    if len(blocks) < 2:
        return 0.0, 0.0
    sorted_b = sorted(blocks, key=lambda b: (b.x, _right(b)))
    largest_gap = 0.0
    cut_pos = 0.0
    prev_right = None
    for b in sorted_b:
        left = b.x
        right = _right(b)
        if prev_right is not None and prev_right < left:
            gap = left - prev_right
            if gap > largest_gap:
                largest_gap = gap
                cut_pos = (prev_right + left) / 2.0
        prev_right = right if prev_right is None else max(prev_right, right)
    return cut_pos, largest_gap


def _find_best_h_cut(blocks: list) -> tuple[float, float]:
    pos, gap = _h_cut_by_edges(blocks)
    if gap >= MIN_GAP_THRESHOLD:
        return pos, gap
    region = _bounding_region(blocks)
    if len(blocks) >= 3 and region is not None:
        narrow_thresh = region[2] * NARROW_ELEMENT_WIDTH_RATIO
        filtered = [b for b in blocks if b.w >= narrow_thresh]
        if 2 <= len(filtered) < len(blocks):
            retry_pos, retry_gap = _h_cut_by_edges(filtered)
            if retry_gap > gap and retry_gap >= MIN_GAP_THRESHOLD:
                return retry_pos, retry_gap
    return pos, gap


def _find_best_v_cut(blocks: list) -> tuple[float, float]:
    pos, gap = _v_cut_by_edges(blocks)
    if gap >= MIN_GAP_THRESHOLD:
        return pos, gap
    region = _bounding_region(blocks)
    if len(blocks) >= 3 and region is not None:
        narrow_thresh = region[2] * NARROW_ELEMENT_WIDTH_RATIO
        filtered = [b for b in blocks if b.w >= narrow_thresh]
        if 2 <= len(filtered) < len(blocks):
            retry_pos, retry_gap = _v_cut_by_edges(filtered)
            if retry_gap > gap and retry_gap >= MIN_GAP_THRESHOLD:
                return retry_pos, retry_gap
    return pos, gap


def _split_h(blocks: list, cut_y: float) -> tuple[list, list]:
    above, below = [], []
    for b in blocks:
        if _cy(b) < cut_y:
            above.append(b)
        else:
            below.append(b)
    return above, below


def _split_v(blocks: list, cut_x: float) -> tuple[list, list]:
    left, right = [], []
    for b in blocks:
        if _cx(b) < cut_x:
            left.append(b)
        else:
            right.append(b)
    return left, right


def _sort_by_y_then_x(blocks: list) -> list:
    return sorted(blocks, key=lambda b: (b.y, b.x))


def _recursive_segment(blocks: list) -> list:
    if len(blocks) <= 1:
        return list(blocks)
    h_pos, h_gap = _find_best_h_cut(blocks)
    v_pos, v_gap = _find_best_v_cut(blocks)
    valid_h = h_gap >= MIN_GAP_THRESHOLD
    valid_v = v_gap >= MIN_GAP_THRESHOLD
    if valid_h and valid_v:
        use_h = h_gap > v_gap
    elif valid_h:
        use_h = True
    elif valid_v:
        use_h = False
    else:
        return _sort_by_y_then_x(blocks)
    if use_h:
        a, b = _split_h(blocks, h_pos)
    else:
        a, b = _split_v(blocks, v_pos)
    if not a or not b:
        return _sort_by_y_then_x(blocks)
    return _recursive_segment(a) + _recursive_segment(b)


# ===== Phase 4: merge cross-layout =====

def _merge_cross_layout(sorted_main: list, cross: list) -> list:
    if not cross:
        return sorted_main
    if not sorted_main:
        return _sort_by_y_then_x(cross)
    sorted_cross = _sort_by_y_then_x(cross)
    result = []
    m = c = 0
    while m < len(sorted_main) or c < len(sorted_cross):
        if c >= len(sorted_cross):
            result.append(sorted_main[m]); m += 1
        elif m >= len(sorted_main):
            result.append(sorted_cross[c]); c += 1
        elif sorted_cross[c].y <= sorted_main[m].y:
            result.append(sorted_cross[c]); c += 1
        else:
            result.append(sorted_main[m]); m += 1
    return result


# ===== Public API =====

def sort(blocks: list, beta: float = DEFAULT_BETA) -> list:
    """Return a new list of blocks in reading order. Mutates blocks' .order."""
    if len(blocks) == 0:
        return []
    if len(blocks) == 1:
        blocks[0].order = 0
        return list(blocks)
    cross = _identify_cross_layout(blocks, beta)
    remaining = [b for b in blocks if b not in cross] if cross else list(blocks)
    if not remaining:
        sorted_main = _sort_by_y_then_x(blocks)
        for i, b in enumerate(sorted_main):
            b.order = i
        return sorted_main
    sorted_main = _recursive_segment(remaining)
    result = _merge_cross_layout(sorted_main, cross)
    for i, b in enumerate(result):
        b.order = i
    return result
