pub mod cleanup;
pub mod config;
pub mod egui_integration;
pub mod layout;
pub mod rail;
pub mod tab;
pub mod ui;

use anyhow::Result;
use mupdf::{Colorspace, Matrix};

/// Render a single page of a PDF document to SVG via MuPDF.
/// Returns (svg_string, page_width_pts, page_height_pts).
pub fn render_page_svg(doc: &mupdf::Document, page_number: i32) -> Result<(String, f64, f64)> {
    let page = doc.load_page(page_number)?;
    let bounds = page.bounds()?;
    let width_pts = (bounds.x1 - bounds.x0) as f64;
    let height_pts = (bounds.y1 - bounds.y0) as f64;

    let svg_string = page.to_svg(&Matrix::IDENTITY)?;

    if std::env::var("DUMP_SVG").is_ok() {
        let path = format!("/tmp/page{}.svg", page_number);
        std::fs::write(&path, &svg_string).ok();
        log::info!("Dumped SVG ({} bytes) to {}", svg_string.len(), path);
    }

    Ok((svg_string, width_pts, height_pts))
}

/// Render a page to an RGB pixmap, scaled so the longest edge fits `target_size`.
/// Returns (rgb_bytes, pixel_width, pixel_height, page_pts_width, page_pts_height).
pub fn render_page_pixmap(
    doc: &mupdf::Document,
    page_number: i32,
    target_size: u32,
) -> Result<(Vec<u8>, u32, u32, f64, f64)> {
    let page = doc.load_page(page_number)?;
    let bounds = page.bounds()?;
    let width_pts = (bounds.x1 - bounds.x0) as f64;
    let height_pts = (bounds.y1 - bounds.y0) as f64;

    let longest = width_pts.max(height_pts);
    let scale = target_size as f64 / longest;
    let scale_f = scale as f32;

    let pixmap = page.to_pixmap(
        &Matrix::new_scale(scale_f, scale_f),
        &Colorspace::device_rgb(),
        false,
        true,
    )?;

    let pixel_width = pixmap.width();
    let pixel_height = pixmap.height();
    let samples = pixmap.samples().to_vec();

    Ok((samples, pixel_width, pixel_height, width_pts, height_pts))
}
