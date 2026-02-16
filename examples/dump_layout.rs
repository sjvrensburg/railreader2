use anyhow::Result;
use railreader2::layout;

fn main() -> Result<()> {
    let pdf_path = std::env::args().nth(1).expect("Usage: dump_layout <pdf>");
    let doc = mupdf::Document::open(&pdf_path)?;

    let model_path =
        std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("models/PP-DocLayoutV3.onnx");
    let mut session = layout::load_model(model_path.to_str().unwrap())?;

    // First, dump raw tensor output
    println!("=== Raw tensor output ===");
    layout::dump_raw_detections(&mut session, &doc, 0)?;

    println!("\n=== Processed analysis ===");
    let analysis = layout::analyze_page(&mut session, &doc, 0)?;

    println!(
        "Page: {:.1} x {:.1} pts",
        analysis.page_width, analysis.page_height
    );
    println!("\n{} blocks detected:\n", analysis.blocks.len());
    println!(
        "{:<6} {:<20} {:<8} {:<40} {:<6} {}",
        "Order", "Class", "Conf", "BBox (x, y, w, h)", "Lines", "Navigable"
    );
    println!("{}", "-".repeat(100));

    for block in &analysis.blocks {
        let class_name = if block.class_id < layout::LAYOUT_CLASSES.len() {
            layout::LAYOUT_CLASSES[block.class_id]
        } else {
            "unknown"
        };
        let navigable = layout::is_navigable(block.class_id);
        println!(
            "{:<6} {:<20} {:<8.2} ({:>6.1}, {:>6.1}, {:>6.1}, {:>6.1})          {:<6} {}",
            block.order,
            class_name,
            block.confidence,
            block.bbox.x,
            block.bbox.y,
            block.bbox.w,
            block.bbox.h,
            block.lines.len(),
            if navigable { "YES" } else { "no" },
        );
    }

    Ok(())
}
