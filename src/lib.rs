pub mod fonts;
pub mod images;
pub mod interpreter;

use anyhow::{anyhow, Result};
use lopdf::{Document, Object};
use vello::Scene;

use crate::interpreter::VelloPdfInterpreter;

/// Render a single page of a PDF document into a vello Scene.
/// Returns (scene, page_width, page_height).
pub fn render_page(doc: &Document, page_number: u32) -> Result<(Scene, f64, f64)> {
    let page_id = doc
        .page_iter()
        .nth((page_number - 1) as usize)
        .ok_or_else(|| anyhow!("Page {} not found", page_number))?;

    let page = doc
        .get_dictionary(page_id)
        .map_err(|e| anyhow!("Failed to get page dictionary: {}", e))?;

    let (width, height) = get_media_box(doc, page)?;

    let content = doc
        .get_page_content(page_id)
        .map_err(|e| anyhow!("Failed to get page content: {}", e))?;
    let operations = lopdf::content::Content::decode(&content)
        .map_err(|e| anyhow!("Failed to decode content stream: {}", e))?
        .operations;

    let mut scene = Scene::new();
    let mut interp = VelloPdfInterpreter::new(doc, page_id, width, height);
    interp.run(&mut scene, &operations)?;

    Ok((scene, width, height))
}

/// Extract MediaBox from a page dictionary.
/// Falls back to US Letter (612x792) if not found.
fn get_media_box(doc: &Document, page: &lopdf::Dictionary) -> Result<(f64, f64)> {
    // Try to get MediaBox directly from the page
    if let Ok(Object::Array(ref media_box)) = page.get(b"MediaBox") {
        if media_box.len() >= 4 {
            let x1 = as_f64(&media_box[0]).unwrap_or(0.0);
            let y1 = as_f64(&media_box[1]).unwrap_or(0.0);
            let x2 = as_f64(&media_box[2]).unwrap_or(612.0);
            let y2 = as_f64(&media_box[3]).unwrap_or(792.0);
            return Ok(((x2 - x1).abs(), (y2 - y1).abs()));
        }
    }

    // Try to resolve reference
    if let Ok(Object::Reference(ref_id)) = page.get(b"MediaBox") {
        if let Ok(Object::Array(ref media_box)) = doc.get_object(*ref_id) {
            if media_box.len() >= 4 {
                let x1 = as_f64(&media_box[0]).unwrap_or(0.0);
                let y1 = as_f64(&media_box[1]).unwrap_or(0.0);
                let x2 = as_f64(&media_box[2]).unwrap_or(612.0);
                let y2 = as_f64(&media_box[3]).unwrap_or(792.0);
                return Ok(((x2 - x1).abs(), (y2 - y1).abs()));
            }
        }
    }

    // Fallback: US Letter
    log::warn!("MediaBox not found on page, falling back to US Letter (612x792)");
    Ok((612.0, 792.0))
}

/// Convert a lopdf Object to f64.
fn as_f64(obj: &Object) -> Option<f64> {
    match obj {
        Object::Integer(i) => Some(*i as f64),
        Object::Real(f) => Some(*f as f64),
        _ => None,
    }
}
