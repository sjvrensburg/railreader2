use anyhow::Result;
use ort::session::Session;
use ort::value::TensorRef;

pub const INPUT_SIZE: u32 = 800;
const CONFIDENCE_THRESHOLD: f32 = 0.4;
const NMS_IOU_THRESHOLD: f32 = 0.5;
const COLUMN_THRESHOLD_RATIO: f32 = 0.15;

/// 23 layout classes from PP-DocLayoutV3.
pub const LAYOUT_CLASSES: [&str; 23] = [
    "document_title",    // 0
    "paragraph_title",   // 1
    "text",              // 2
    "page_number",       // 3
    "abstract",          // 4
    "table_of_contents", // 5
    "references",        // 6
    "footnote",          // 7
    "table",             // 8
    "header",            // 9
    "footer",            // 10
    "algorithm",         // 11
    "formula",           // 12
    "formula_number",    // 13
    "image",             // 14
    "figure_caption",    // 15
    "table_caption",     // 16
    "seal",              // 17
    "figure_title",      // 18
    "figure",            // 19
    "header_image",      // 20
    "footer_image",      // 21
    "aside_text",        // 22
];

/// Classes that are navigable in rail mode (readable text only).
const NAVIGABLE_CLASSES: [usize; 8] = [
    0,  // document_title
    1,  // paragraph_title
    2,  // text
    4,  // abstract
    6,  // references
    7,  // footnote
    11, // algorithm
    22, // aside_text
];

pub fn is_navigable(class_id: usize) -> bool {
    NAVIGABLE_CLASSES.contains(&class_id)
}

#[derive(Debug, Clone)]
pub struct BBox {
    pub x: f32,
    pub y: f32,
    pub w: f32,
    pub h: f32,
}

#[derive(Debug, Clone)]
pub struct LineInfo {
    pub y: f32,
    pub height: f32,
}

#[derive(Debug, Clone)]
pub struct LayoutBlock {
    pub bbox: BBox,
    pub class_id: usize,
    pub confidence: f32,
    pub order: usize,
    pub lines: Vec<LineInfo>,
}

#[derive(Debug, Clone)]
pub struct PageAnalysis {
    pub blocks: Vec<LayoutBlock>,
    pub page_width: f64,
    pub page_height: f64,
}

pub fn load_model(path: &str) -> Result<Session> {
    let session = Session::builder()?
        .with_optimization_level(ort::session::builder::GraphOptimizationLevel::Level3)?
        .commit_from_file(path)?;
    Ok(session)
}

/// Dump raw model output for debugging.
pub fn dump_raw_detections(
    session: &mut Session,
    doc: &mupdf::Document,
    page_number: i32,
) -> Result<()> {
    let (rgb_bytes, px_w, px_h, _page_w, _page_h) =
        crate::render_page_pixmap(doc, page_number, INPUT_SIZE)?;

    let orig_h = px_h as usize;
    let orig_w = px_w as usize;
    let target = INPUT_SIZE as usize;
    let scale_h = target as f32 / orig_h as f32;
    let scale_w = target as f32 / orig_w as f32;

    let mean = [0.485f32, 0.456, 0.406];
    let std_val = [0.229f32, 0.224, 0.225];
    let pixel_count = target * target;
    let mut chw_data = vec![0.0f32; 3 * pixel_count];
    for y in 0..target {
        for x in 0..target {
            let src_y = ((y as f32 / scale_h) as usize).min(orig_h - 1);
            let src_x = ((x as f32 / scale_w) as usize).min(orig_w - 1);
            let src_idx = (src_y * orig_w + src_x) * 3;
            let dst_idx = y * target + x;
            for c in 0..3 {
                let val = rgb_bytes[src_idx + c] as f32 / 255.0;
                chw_data[c * pixel_count + dst_idx] = (val - mean[c]) / std_val[c];
            }
        }
    }

    let im_shape_data = vec![target as f32, target as f32];
    let scale_factor_data = vec![scale_h, scale_w];
    let im_shape = TensorRef::from_array_view(([1i64, 2], im_shape_data.as_slice()))?;
    let image =
        TensorRef::from_array_view(([1i64, 3, target as i64, target as i64], chw_data.as_slice()))?;
    let scale_factor = TensorRef::from_array_view(([1i64, 2], scale_factor_data.as_slice()))?;

    let outputs = session.run(ort::inputs![im_shape, image, scale_factor])?;
    let (shape, data) = outputs[0].try_extract_tensor::<f32>()?;

    println!("Output shape: {:?}", shape);
    println!(
        "Pixel dims: {}x{}, scale_h={:.3}, scale_w={:.3}",
        orig_w, orig_h, scale_h, scale_w
    );

    let n = if shape.len() == 2 {
        shape[0] as usize
    } else {
        0
    };
    let cols = if shape.len() == 2 {
        shape[1] as usize
    } else {
        6
    };

    println!("\nRaw detections ({} rows, {} cols):", n, cols);
    for i in 0..n {
        let base = i * cols;
        let row: Vec<f32> = (0..cols).map(|c| data[base + c]).collect();
        println!("  [{}] {:?}", i, row);
    }

    Ok(())
}

pub fn analyze_page(
    session: &mut Session,
    doc: &mupdf::Document,
    page_number: i32,
) -> Result<PageAnalysis> {
    let (rgb_bytes, px_w, px_h, page_w, page_h) =
        crate::render_page_pixmap(doc, page_number, INPUT_SIZE)?;

    let orig_h = px_h as usize;
    let orig_w = px_w as usize;
    let target = INPUT_SIZE as usize;

    // Stretch-resize to 800x800 (matching reference implementation)
    let scale_h = target as f32 / orig_h as f32;
    let scale_w = target as f32 / orig_w as f32;

    let mean = [0.485f32, 0.456, 0.406];
    let std_val = [0.229f32, 0.224, 0.225];

    let pixel_count = target * target;
    let mut chw_data = vec![0.0f32; 3 * pixel_count];

    // Nearest-neighbor resize to 800x800 and ImageNet normalize, HWC→CHW
    for y in 0..target {
        for x in 0..target {
            let src_y = ((y as f32 / scale_h) as usize).min(orig_h - 1);
            let src_x = ((x as f32 / scale_w) as usize).min(orig_w - 1);
            let src_idx = (src_y * orig_w + src_x) * 3;
            let dst_idx = y * target + x;
            for c in 0..3 {
                let val = rgb_bytes[src_idx + c] as f32 / 255.0;
                chw_data[c * pixel_count + dst_idx] = (val - mean[c]) / std_val[c];
            }
        }
    }

    // Build input tensors matching reference implementation:
    // im_shape: [resized_h, resized_w] = [800, 800]
    let im_shape_data = vec![target as f32, target as f32];
    // scale_factor: [800/orig_h, 800/orig_w]
    let scale_factor_data = vec![scale_h, scale_w];

    let im_shape = TensorRef::from_array_view(([1i64, 2], im_shape_data.as_slice()))?;
    let image =
        TensorRef::from_array_view(([1i64, 3, target as i64, target as i64], chw_data.as_slice()))?;
    let scale_factor = TensorRef::from_array_view(([1i64, 2], scale_factor_data.as_slice()))?;

    // Run inference
    let outputs = session.run(ort::inputs![im_shape, image, scale_factor])?;

    // Output: [N, cols] -> [class_id, confidence, xmin, ymin, xmax, ymax, ...]
    // Coordinates are in original pixel space (model applies scale_factor internally)
    let (shape, data) = outputs[0].try_extract_tensor::<f32>()?;
    let (n_detections, cols) = if shape.len() == 2 {
        (shape[0] as usize, shape[1] as usize)
    } else {
        (0, 6)
    };

    // Scale from original pixel coords to page points
    let scale_x = page_w as f32 / orig_w as f32;
    let scale_y = page_h as f32 / orig_h as f32;

    // Parse detections
    let mut raw_blocks: Vec<LayoutBlock> = Vec::new();
    for i in 0..n_detections {
        let base = i * cols;
        let class_id = data[base] as usize;
        let confidence = data[base + 1];
        let xmin = data[base + 2];
        let ymin = data[base + 3];
        let xmax = data[base + 4];
        let ymax = data[base + 5];

        if confidence < CONFIDENCE_THRESHOLD {
            continue;
        }

        if class_id >= LAYOUT_CLASSES.len() {
            continue;
        }

        // Clamp to page bounds (output is in original pixel coords)
        let x = xmin.max(0.0);
        let y = ymin.max(0.0);
        let w = xmax.min(orig_w as f32) - x;
        let h = ymax.min(orig_h as f32) - y;

        if w <= 0.0 || h <= 0.0 {
            continue;
        }

        // Skip tiny detections (likely spurious)
        let min_dim = 5.0; // minimum 5 pixels
        if w < min_dim || h < min_dim {
            continue;
        }

        raw_blocks.push(LayoutBlock {
            bbox: BBox {
                x: x * scale_x,
                y: y * scale_y,
                w: w * scale_x,
                h: h * scale_y,
            },
            class_id,
            confidence,
            order: 0,
            lines: Vec::new(),
        });
    }

    // NMS
    nms(&mut raw_blocks, NMS_IOU_THRESHOLD);

    // Reading order
    assign_reading_order(&mut raw_blocks, page_w as f32);

    // Sort by reading order
    raw_blocks.sort_by_key(|b| b.order);

    // Line detection for navigable blocks
    detect_lines_for_blocks(
        &mut raw_blocks,
        &rgb_bytes,
        orig_w,
        orig_h,
        scale_x,
        scale_y,
    );

    Ok(PageAnalysis {
        blocks: raw_blocks,
        page_width: page_w,
        page_height: page_h,
    })
}

pub fn fallback_analysis(page_width: f64, page_height: f64) -> PageAnalysis {
    let strip_count = 8;
    let strip_h = page_height as f32 / strip_count as f32;
    let blocks = (0..strip_count)
        .map(|i| LayoutBlock {
            bbox: BBox {
                x: 0.0,
                y: i as f32 * strip_h,
                w: page_width as f32,
                h: strip_h,
            },
            class_id: 2, // text
            confidence: 1.0,
            order: i,
            lines: vec![LineInfo {
                y: i as f32 * strip_h + strip_h / 2.0,
                height: strip_h,
            }],
        })
        .collect();

    PageAnalysis {
        blocks,
        page_width,
        page_height,
    }
}

fn iou(a: &BBox, b: &BBox) -> f32 {
    let x1 = a.x.max(b.x);
    let y1 = a.y.max(b.y);
    let x2 = (a.x + a.w).min(b.x + b.w);
    let y2 = (a.y + a.h).min(b.y + b.h);

    let inter = (x2 - x1).max(0.0) * (y2 - y1).max(0.0);
    let area_a = a.w * a.h;
    let area_b = b.w * b.h;
    let union = area_a + area_b - inter;

    if union <= 0.0 {
        0.0
    } else {
        inter / union
    }
}

fn nms(blocks: &mut Vec<LayoutBlock>, threshold: f32) {
    // Sort by confidence descending
    blocks.sort_by(|a, b| b.confidence.partial_cmp(&a.confidence).unwrap());

    let mut keep = vec![true; blocks.len()];

    for i in 0..blocks.len() {
        if !keep[i] {
            continue;
        }
        for j in (i + 1)..blocks.len() {
            if !keep[j] {
                continue;
            }
            if iou(&blocks[i].bbox, &blocks[j].bbox) > threshold {
                keep[j] = false;
            }
        }
    }

    let mut idx = 0;
    blocks.retain(|_| {
        let k = keep[idx];
        idx += 1;
        k
    });
}

fn assign_reading_order(blocks: &mut [LayoutBlock], page_width: f32) {
    if blocks.is_empty() {
        return;
    }

    let column_threshold = page_width * COLUMN_THRESHOLD_RATIO;

    // Use overlap-based column clustering: two blocks are in the same column if
    // their horizontal extents overlap significantly, or their left edges are close.
    // This avoids splitting blocks of different widths (e.g., full-width text vs
    // narrow footnotes) into separate columns.

    // Union-find for column assignment
    let n = blocks.len();
    let mut parent: Vec<usize> = (0..n).collect();
    fn find(parent: &mut [usize], i: usize) -> usize {
        if parent[i] != i {
            parent[i] = find(parent, parent[i]);
        }
        parent[i]
    }
    fn union(parent: &mut [usize], a: usize, b: usize) {
        let ra = find(parent, a);
        let rb = find(parent, b);
        if ra != rb {
            parent[ra] = rb;
        }
    }

    for i in 0..n {
        for j in (i + 1)..n {
            let a = &blocks[i].bbox;
            let b = &blocks[j].bbox;

            // Check if left edges are close (within column threshold)
            let left_close = (a.x - b.x).abs() < column_threshold;

            // Check horizontal overlap
            let overlap_start = a.x.max(b.x);
            let overlap_end = (a.x + a.w).min(b.x + b.w);
            let overlap = (overlap_end - overlap_start).max(0.0);
            let min_width = a.w.min(b.w);
            let has_overlap = min_width > 0.0 && overlap / min_width > 0.3;

            if left_close || has_overlap {
                union(&mut parent, i, j);
            }
        }
    }

    // Group into columns
    let mut column_map: std::collections::HashMap<usize, Vec<usize>> =
        std::collections::HashMap::new();
    for i in 0..n {
        let root = find(&mut parent, i);
        column_map.entry(root).or_default().push(i);
    }
    let mut columns: Vec<Vec<usize>> = column_map.into_values().collect();

    // Sort columns left-to-right by minimum x
    columns.sort_by(|a, b| {
        let min_x_a = a.iter().map(|&i| blocks[i].bbox.x).fold(f32::MAX, f32::min);
        let min_x_b = b.iter().map(|&i| blocks[i].bbox.x).fold(f32::MAX, f32::min);
        min_x_a.partial_cmp(&min_x_b).unwrap()
    });

    let mut order = 0;
    for col in &mut columns {
        col.sort_by(|&a, &b| blocks[a].bbox.y.partial_cmp(&blocks[b].bbox.y).unwrap());
        for &idx in col.iter() {
            blocks[idx].order = order;
            order += 1;
        }
    }
}

fn detect_lines_for_blocks(
    blocks: &mut [LayoutBlock],
    rgb_bytes: &[u8],
    img_w: usize,
    img_h: usize,
    scale_x: f32,
    scale_y: f32,
) {
    for block in blocks.iter_mut() {
        if !is_navigable(block.class_id) {
            continue;
        }

        // Convert block bbox back to pixel coordinates
        let px_x = (block.bbox.x / scale_x).round() as usize;
        let px_y = (block.bbox.y / scale_y).round() as usize;
        let px_w = (block.bbox.w / scale_x).round() as usize;
        let px_h = (block.bbox.h / scale_y).round() as usize;

        // Clamp to image bounds
        let px_x = px_x.min(img_w.saturating_sub(1));
        let px_y = px_y.min(img_h.saturating_sub(1));
        let px_w = px_w.min(img_w - px_x);
        let px_h = px_h.min(img_h - px_y);

        if px_w == 0 || px_h == 0 {
            block.lines.push(LineInfo {
                y: block.bbox.y + block.bbox.h / 2.0,
                height: block.bbox.h,
            });
            continue;
        }

        // Horizontal projection profiling
        let mut profile = vec![0.0f32; px_h];
        #[allow(clippy::needless_range_loop)]
        for row in 0..px_h {
            let mut dark_count = 0u32;
            for col in 0..px_w {
                let pixel_idx = ((px_y + row) * img_w + (px_x + col)) * 3;
                if pixel_idx + 2 < rgb_bytes.len() {
                    let r = rgb_bytes[pixel_idx] as f32;
                    let g = rgb_bytes[pixel_idx + 1] as f32;
                    let b = rgb_bytes[pixel_idx + 2] as f32;
                    let lum = r * 0.299 + g * 0.587 + b * 0.114;
                    if lum < 160.0 {
                        dark_count += 1;
                    }
                }
            }
            profile[row] = dark_count as f32 / px_w as f32;
        }

        // Smooth with radius-1 moving average
        let mut smoothed = vec![0.0f32; px_h];
        #[allow(clippy::needless_range_loop)]
        for r in 0..px_h {
            let start = r.saturating_sub(1);
            let end = (r + 2).min(px_h);
            let sum: f32 = profile[start..end].iter().sum();
            smoothed[r] = sum / (end - start) as f32;
        }

        // Adaptive threshold: 15% of mean non-zero density
        let non_zero: Vec<f32> = smoothed.iter().copied().filter(|&v| v > 0.005).collect();
        let threshold = if non_zero.is_empty() {
            0.005
        } else {
            let mean_density: f32 = non_zero.iter().sum::<f32>() / non_zero.len() as f32;
            (mean_density * 0.15).max(0.005)
        };

        // Find contiguous dark runs
        let mut lines = Vec::new();
        let mut run_start: Option<usize> = None;

        #[allow(clippy::needless_range_loop)]
        for r in 0..px_h {
            if smoothed[r] > threshold {
                if run_start.is_none() {
                    run_start = Some(r);
                }
            } else if let Some(start) = run_start {
                let run_h = r - start;
                if run_h >= 3 {
                    let center_y_px = start as f32 + run_h as f32 / 2.0;
                    lines.push(LineInfo {
                        y: block.bbox.y + center_y_px * scale_y,
                        height: run_h as f32 * scale_y,
                    });
                }
                run_start = None;
            }
        }
        // Handle run that extends to end
        if let Some(start) = run_start {
            let run_h = px_h - start;
            if run_h >= 3 {
                let center_y_px = start as f32 + run_h as f32 / 2.0;
                lines.push(LineInfo {
                    y: block.bbox.y + center_y_px * scale_y,
                    height: run_h as f32 * scale_y,
                });
            }
        }

        // Fallback: single line spanning block
        if lines.is_empty() {
            lines.push(LineInfo {
                y: block.bbox.y + block.bbox.h / 2.0,
                height: block.bbox.h,
            });
        }

        block.lines = lines;
    }
}
