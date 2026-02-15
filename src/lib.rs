use anyhow::Result;
use mupdf::Matrix;

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
