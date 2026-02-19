use anyhow::Result;
use ort::session::Session;
use ort::value::TensorRef;
use std::collections::HashSet;

pub const INPUT_SIZE: u32 = 800;
const CONFIDENCE_THRESHOLD: f32 = 0.4;
const NMS_IOU_THRESHOLD: f32 = 0.5;

// Luminance threshold for "dark" pixels in line detection. Pixels with luminance
// below this value (on a 0–255 scale) are considered ink/text. Derived empirically
// for typical black-on-white document pages.
const DARK_LUMINANCE_THRESHOLD: f32 = 160.0;

// Fraction of mean row density used as the adaptive threshold for line detection.
// Rows with density above (mean_density * this factor) are considered part of a
// text line. Lower values detect fainter lines; higher values are more selective.
const DENSITY_THRESHOLD_FRACTION: f32 = 0.15;

// Minimum height in pixels for a contiguous dark run to qualify as a text line.
// Runs shorter than this are treated as noise (e.g. underlines, specks).
const MIN_LINE_HEIGHT_PX: usize = 3;

/// 25 layout classes from PP-DocLayoutV3 (alphabetical order).
/// Source: https://huggingface.co/PaddlePaddle/PP-DocLayoutV3 inference.yml
pub const LAYOUT_CLASSES: [&str; 25] = [
    "abstract",          // 0
    "algorithm",         // 1
    "aside_text",        // 2
    "chart",             // 3
    "content",           // 4
    "display_formula",   // 5
    "doc_title",         // 6
    "figure_title",      // 7
    "footer",            // 8
    "footer_image",      // 9
    "footnote",          // 10
    "formula_number",    // 11
    "header",            // 12
    "header_image",      // 13
    "image",             // 14
    "inline_formula",    // 15
    "number",            // 16
    "paragraph_title",   // 17
    "reference",         // 18
    "reference_content", // 19
    "seal",              // 20
    "table",             // 21
    "text",              // 22
    "vertical_text",     // 23
    "vision_footnote",   // 24
];

/// Returns the default set of navigable class IDs for rail mode (readable text only).
pub fn default_navigable_classes() -> HashSet<usize> {
    [
        0,  // abstract
        1,  // algorithm
        2,  // aside_text
        4,  // content
        6,  // doc_title
        10, // footnote
        17, // paragraph_title
        18, // reference
        19, // reference_content
        22, // text
    ]
    .into_iter()
    .collect()
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

/// Input data for the analysis worker thread. Contains the raw RGB pixmap bytes
/// rendered on the main thread (MuPDF requirement) plus dimensions needed for
/// preprocessing, inference, and coordinate mapping.
pub struct AnalysisInput {
    pub rgb_bytes: Vec<u8>,
    pub px_w: u32,
    pub px_h: u32,
    pub page_w: f64,
    pub page_h: f64,
    pub page_number: i32,
}

pub fn load_model(path: &str) -> Result<Session> {
    let session = Session::builder()?
        .with_optimization_level(ort::session::builder::GraphOptimizationLevel::Level3)?
        .commit_from_file(path)?;
    Ok(session)
}

/// Main-thread: render pixmap via MuPDF and package for the worker thread.
pub fn prepare_analysis_input(
    doc: &mupdf::Document,
    page_number: i32,
) -> Result<AnalysisInput> {
    let (rgb_bytes, px_w, px_h, page_w, page_h) =
        crate::render_page_pixmap(doc, page_number, INPUT_SIZE)?;
    Ok(AnalysisInput {
        rgb_bytes,
        px_w,
        px_h,
        page_w,
        page_h,
        page_number,
    })
}

/// Worker-thread: preprocess + ONNX inference + NMS + line detection.
/// No MuPDF needed — operates entirely on the raw RGB bytes.
pub fn run_analysis(session: &mut Session, input: AnalysisInput) -> Result<PageAnalysis> {
    let orig_h = input.px_h as usize;
    let orig_w = input.px_w as usize;
    let target = INPUT_SIZE as usize;

    let scale_h = target as f32 / orig_h as f32;
    let scale_w = target as f32 / orig_w as f32;

    let chw_data = preprocess_image(&input.rgb_bytes, orig_w, orig_h, target);

    let im_shape_data = vec![target as f32, target as f32];
    let scale_factor_data = vec![scale_h, scale_w];

    let im_shape = TensorRef::from_array_view(([1i64, 2], im_shape_data.as_slice()))?;
    let image =
        TensorRef::from_array_view(([1i64, 3, target as i64, target as i64], chw_data.as_slice()))?;
    let scale_factor = TensorRef::from_array_view(([1i64, 2], scale_factor_data.as_slice()))?;

    let outputs = session.run(ort::inputs![im_shape, image, scale_factor])?;

    let (shape, data) = outputs[0].try_extract_tensor::<f32>()?;
    let (n_detections, cols) = if shape.len() == 2 {
        (shape[0] as usize, shape[1] as usize)
    } else {
        (0, 6)
    };

    let scale_x = input.page_w as f32 / orig_w as f32;
    let scale_y = input.page_h as f32 / orig_h as f32;

    let has_reading_order = cols >= 7;
    let mut raw_blocks: Vec<LayoutBlock> = Vec::new();
    for i in 0..n_detections {
        let base = i * cols;
        let class_id = data[base] as usize;
        let confidence = data[base + 1];
        let xmin = data[base + 2];
        let ymin = data[base + 3];
        let xmax = data[base + 4];
        let ymax = data[base + 5];
        let model_order = if has_reading_order {
            data[base + 6] as usize
        } else {
            0
        };

        if confidence < CONFIDENCE_THRESHOLD {
            continue;
        }

        if class_id >= LAYOUT_CLASSES.len() {
            continue;
        }

        let x = xmin.max(0.0);
        let y = ymin.max(0.0);
        let w = xmax.min(orig_w as f32) - x;
        let h = ymax.min(orig_h as f32) - y;

        if w <= 0.0 || h <= 0.0 {
            continue;
        }

        let min_dim = 5.0;
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
            order: model_order,
            lines: Vec::new(),
        });
    }

    nms(&mut raw_blocks, NMS_IOU_THRESHOLD);

    raw_blocks.sort_by(|a, b| {
        a.order
            .cmp(&b.order)
            .then(a.bbox.y.partial_cmp(&b.bbox.y).unwrap())
    });
    for (i, block) in raw_blocks.iter_mut().enumerate() {
        block.order = i;
    }

    detect_lines_for_blocks(
        &mut raw_blocks,
        &input.rgb_bytes,
        orig_w,
        orig_h,
        scale_x,
        scale_y,
    );

    Ok(PageAnalysis {
        blocks: raw_blocks,
        page_width: input.page_w,
        page_height: input.page_h,
    })
}

/// Nearest-neighbor resize to `target x target` with ImageNet normalization, HWC→CHW.
///
/// ImageNet mean/std are the channel-wise statistics of the ImageNet-1K training set.
/// Most vision models (including PP-DocLayoutV3) expect inputs normalized this way
/// so activations are centered around zero with unit variance.
fn preprocess_image(rgb_bytes: &[u8], orig_w: usize, orig_h: usize, target: usize) -> Vec<f32> {
    // PP-DocLayoutV3 uses mean=[0,0,0] std=[1,1,1] (no ImageNet normalization).
    // Just scale pixels to [0, 1] and convert to CHW layout.
    let scale_h = target as f32 / orig_h as f32;
    let scale_w = target as f32 / orig_w as f32;
    let pixel_count = target * target;
    let mut chw_data = vec![0.0f32; 3 * pixel_count];

    for y in 0..target {
        for x in 0..target {
            let src_y = ((y as f32 / scale_h) as usize).min(orig_h - 1);
            let src_x = ((x as f32 / scale_w) as usize).min(orig_w - 1);
            let src_idx = (src_y * orig_w + src_x) * 3;
            let dst_idx = y * target + x;
            for c in 0..3 {
                chw_data[c * pixel_count + dst_idx] = rgb_bytes[src_idx + c] as f32 / 255.0;
            }
        }
    }

    chw_data
}

/// Combined analyze: renders pixmap + runs ONNX inference in one call.
/// Used by the `dump_layout` example. For the main app, use
/// `prepare_analysis_input` + `run_analysis` separately.
pub fn analyze_page(
    session: &mut Session,
    doc: &mupdf::Document,
    page_number: i32,
) -> Result<PageAnalysis> {
    let input = prepare_analysis_input(doc, page_number)?;
    run_analysis(session, input)
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

/// Compute per-row dark pixel density for a crop of the RGB image.
///
/// Returns a smoothed density profile (one value per row) where each value is
/// the fraction of pixels in that row whose luminance falls below
/// `DARK_LUMINANCE_THRESHOLD`. A radius-1 moving average is applied to reduce noise.
fn compute_row_densities(
    rgb_bytes: &[u8],
    img_w: usize,
    crop_x: usize,
    crop_y: usize,
    crop_w: usize,
    crop_h: usize,
) -> Vec<f32> {
    let mut profile = vec![0.0f32; crop_h];
    #[allow(clippy::needless_range_loop)]
    for row in 0..crop_h {
        let mut dark_count = 0u32;
        for col in 0..crop_w {
            let pixel_idx = ((crop_y + row) * img_w + (crop_x + col)) * 3;
            if pixel_idx + 2 < rgb_bytes.len() {
                let r = rgb_bytes[pixel_idx] as f32;
                let g = rgb_bytes[pixel_idx + 1] as f32;
                let b = rgb_bytes[pixel_idx + 2] as f32;
                let lum = r * 0.299 + g * 0.587 + b * 0.114;
                if lum < DARK_LUMINANCE_THRESHOLD {
                    dark_count += 1;
                }
            }
        }
        profile[row] = dark_count as f32 / crop_w as f32;
    }

    // Smooth with radius-1 moving average
    let mut smoothed = vec![0.0f32; crop_h];
    #[allow(clippy::needless_range_loop)]
    for r in 0..crop_h {
        let start = r.saturating_sub(1);
        let end = (r + 2).min(crop_h);
        let sum: f32 = profile[start..end].iter().sum();
        smoothed[r] = sum / (end - start) as f32;
    }

    smoothed
}

/// Find contiguous runs of rows whose density exceeds an adaptive threshold.
///
/// Returns `(start_row, height)` pairs in pixel coordinates. Runs shorter than
/// `MIN_LINE_HEIGHT_PX` are discarded as noise.
fn find_line_runs(densities: &[f32]) -> Vec<(usize, usize)> {
    // Adaptive threshold: DENSITY_THRESHOLD_FRACTION of mean non-zero density
    let non_zero: Vec<f32> = densities.iter().copied().filter(|&v| v > 0.005).collect();
    let threshold = if non_zero.is_empty() {
        0.005
    } else {
        let mean_density: f32 = non_zero.iter().sum::<f32>() / non_zero.len() as f32;
        (mean_density * DENSITY_THRESHOLD_FRACTION).max(0.005)
    };

    let mut runs = Vec::new();
    let mut run_start: Option<usize> = None;

    for (r, &density) in densities.iter().enumerate() {
        if density > threshold {
            if run_start.is_none() {
                run_start = Some(r);
            }
        } else if let Some(start) = run_start {
            let run_h = r - start;
            if run_h >= MIN_LINE_HEIGHT_PX {
                runs.push((start, run_h));
            }
            run_start = None;
        }
    }
    // Handle run that extends to end
    if let Some(start) = run_start {
        let run_h = densities.len() - start;
        if run_h >= MIN_LINE_HEIGHT_PX {
            runs.push((start, run_h));
        }
    }

    runs
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

        let densities = compute_row_densities(rgb_bytes, img_w, px_x, px_y, px_w, px_h);
        let runs = find_line_runs(&densities);

        let mut lines: Vec<LineInfo> = runs
            .into_iter()
            .map(|(start, run_h)| {
                let center_y_px = start as f32 + run_h as f32 / 2.0;
                LineInfo {
                    y: block.bbox.y + center_y_px * scale_y,
                    height: run_h as f32 * scale_y,
                }
            })
            .collect();

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
