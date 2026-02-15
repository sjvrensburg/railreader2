use std::collections::HashMap;
use std::sync::Arc;

use anyhow::{anyhow, Result};
use lopdf::content::Operation;
use lopdf::{Document, Object, ObjectId};
use vello::kurbo::{Affine, BezPath, Cap, Join, Point, Rect, Stroke};
use vello::peniko::{Color, Fill, ImageBrush};
use vello::Scene;

use crate::fonts::{self, PdfEncoding, PdfFont};
use crate::images;

#[derive(Clone, Debug)]
struct GraphicsState {
    ctm: Affine,
    fill_color: Color,
    stroke_color: Color,
    line_width: f64,
    line_cap: Cap,
    line_join: Join,
}

impl Default for GraphicsState {
    fn default() -> Self {
        Self {
            ctm: Affine::IDENTITY,
            fill_color: Color::new([0.0, 0.0, 0.0, 1.0]),
            stroke_color: Color::new([0.0, 0.0, 0.0, 1.0]),
            line_width: 1.0,
            line_cap: Cap::Butt,
            line_join: Join::Miter,
        }
    }
}

#[derive(Clone, Debug)]
struct TextState {
    text_matrix: Affine,
    text_line_matrix: Affine,
    font_size: f64,
    current_font: Option<Arc<PdfFont>>,
    current_font_name: Vec<u8>,
    char_spacing: f64,
    word_spacing: f64,
    horizontal_scaling: f64,
    leading: f64,
    text_rise: f64,
    render_mode: u32,
}

impl Default for TextState {
    fn default() -> Self {
        Self {
            text_matrix: Affine::IDENTITY,
            text_line_matrix: Affine::IDENTITY,
            font_size: 12.0,
            current_font: None,
            current_font_name: Vec::new(),
            char_spacing: 0.0,
            word_spacing: 0.0,
            horizontal_scaling: 100.0,
            leading: 0.0,
            text_rise: 0.0,
            render_mode: 0,
        }
    }
}

pub struct VelloPdfInterpreter<'a> {
    doc: &'a Document,
    page_id: ObjectId,
    page_fonts: HashMap<Vec<u8>, Arc<PdfFont>>,
    state_stack: Vec<GraphicsState>,
    state: GraphicsState,
    current_path: BezPath,
    base_transform: Affine,
    text_state: TextState,
    clip_depth: usize,
    current_point: Option<Point>,
    pending_clip: Option<Fill>,
}

impl<'a> VelloPdfInterpreter<'a> {
    pub fn new(doc: &'a Document, page_id: ObjectId, _page_width: f64, page_height: f64) -> Self {
        let base_transform = Affine::new([1.0, 0.0, 0.0, -1.0, 0.0, page_height]);
        let page_fonts = fonts::extract_page_fonts(doc, page_id);
        log::debug!("Extracted {} fonts for page", page_fonts.len());
        Self {
            doc,
            page_id,
            page_fonts,
            state_stack: Vec::new(),
            state: GraphicsState::default(),
            current_path: BezPath::new(),
            base_transform,
            text_state: TextState::default(),
            clip_depth: 0,
            current_point: None,
            pending_clip: None,
        }
    }

    fn effective_transform(&self) -> Affine {
        self.base_transform * self.state.ctm
    }

    pub fn run(&mut self, scene: &mut Scene, operations: &[Operation]) -> Result<()> {
        for op in operations {
            if let Err(e) = self.execute(scene, op) {
                log::warn!("Skipping operator '{}': {}", op.operator, e);
            }
        }
        for _ in 0..self.clip_depth {
            scene.pop_layer();
        }
        Ok(())
    }

    fn execute(&mut self, scene: &mut Scene, op: &Operation) -> Result<()> {
        match op.operator.as_str() {
            // --- Graphics state ---
            "q" => self.op_save(),
            "Q" => self.op_restore(),
            "cm" => self.op_concat_matrix(&op.operands),
            "w" => self.op_set_line_width(&op.operands),
            "J" => self.op_set_line_cap(&op.operands),
            "j" => self.op_set_line_join(&op.operands),

            // --- Path construction ---
            "m" => self.op_move_to(&op.operands),
            "l" => self.op_line_to(&op.operands),
            "c" => self.op_curve_to(&op.operands),
            "v" => self.op_curve_to_v(&op.operands),
            "y" => self.op_curve_to_y(&op.operands),
            "h" => self.op_close_path(),
            "re" => self.op_rectangle(&op.operands),

            // --- Path painting ---
            "S" => self.op_stroke(scene),
            "s" => {
                self.op_close_path()?;
                self.op_stroke(scene)
            }
            "f" | "F" => self.op_fill(scene, Fill::NonZero),
            "f*" => self.op_fill(scene, Fill::EvenOdd),
            "B" => self.op_fill_stroke(scene, Fill::NonZero),
            "B*" => self.op_fill_stroke(scene, Fill::EvenOdd),
            "b" => {
                self.op_close_path()?;
                self.op_fill_stroke(scene, Fill::NonZero)
            }
            "b*" => {
                self.op_close_path()?;
                self.op_fill_stroke(scene, Fill::EvenOdd)
            }
            "n" => self.op_end_path(scene),

            // --- Color ---
            "rg" => self.op_set_fill_rgb(&op.operands),
            "RG" => self.op_set_stroke_rgb(&op.operands),
            "g" => self.op_set_fill_gray(&op.operands),
            "G" => self.op_set_stroke_gray(&op.operands),
            "k" => self.op_set_fill_cmyk(&op.operands),
            "K" => self.op_set_stroke_cmyk(&op.operands),
            "cs" | "CS" | "sc" | "SC" | "scn" | "SCN" => {
                log::debug!("Ignoring color space operator: {}", op.operator);
                Ok(())
            }

            // --- Clipping ---
            "W" => {
                self.pending_clip = Some(Fill::NonZero);
                Ok(())
            }
            "W*" => {
                self.pending_clip = Some(Fill::EvenOdd);
                Ok(())
            }

            // --- Text ---
            "BT" => self.op_begin_text(),
            "ET" => self.op_end_text(),
            "Tf" => self.op_set_font(&op.operands),
            "Td" => self.op_text_move(&op.operands),
            "TD" => self.op_text_move_set_leading(&op.operands),
            "Tm" => self.op_text_matrix(&op.operands),
            "T*" => self.op_text_next_line(),
            "Tj" => self.op_show_text(scene, &op.operands),
            "TJ" => self.op_show_text_array(scene, &op.operands),
            "'" => {
                self.op_text_next_line()?;
                self.op_show_text(scene, &op.operands)
            }
            "\"" => {
                if op.operands.len() >= 3 {
                    self.text_state.word_spacing = get_number(&op.operands[0]).unwrap_or(0.0);
                    self.text_state.char_spacing = get_number(&op.operands[1]).unwrap_or(0.0);
                    self.op_text_next_line()?;
                    self.op_show_text(scene, &[op.operands[2].clone()])
                } else {
                    Ok(())
                }
            }

            // --- Text state ---
            "Tc" => {
                self.text_state.char_spacing = get_number(&op.operands[0])?;
                Ok(())
            }
            "Tw" => {
                self.text_state.word_spacing = get_number(&op.operands[0])?;
                Ok(())
            }
            "Tz" => {
                self.text_state.horizontal_scaling = get_number(&op.operands[0])?;
                Ok(())
            }
            "TL" => {
                self.text_state.leading = get_number(&op.operands[0])?;
                Ok(())
            }
            "Tr" => {
                self.text_state.render_mode = get_number(&op.operands[0])? as u32;
                Ok(())
            }
            "Ts" => {
                self.text_state.text_rise = get_number(&op.operands[0])?;
                Ok(())
            }

            // --- XObject ---
            "Do" => self.op_do_xobject(scene, &op.operands),

            // --- Misc (ignore) ---
            "d" | "i" | "M" | "gs" | "ri" => {
                log::trace!("Ignoring operator: {}", op.operator);
                Ok(())
            }

            _ => {
                log::debug!("Unknown operator: {}", op.operator);
                Ok(())
            }
        }
    }

    // --- Graphics state operators ---

    fn op_save(&mut self) -> Result<()> {
        self.state_stack.push(self.state.clone());
        Ok(())
    }

    fn op_restore(&mut self) -> Result<()> {
        if let Some(state) = self.state_stack.pop() {
            self.state = state;
        }
        self.pending_clip = None;
        Ok(())
    }

    fn op_concat_matrix(&mut self, operands: &[Object]) -> Result<()> {
        let vals = get_numbers(operands, 6)?;
        let m = Affine::new([vals[0], vals[1], vals[2], vals[3], vals[4], vals[5]]);
        self.state.ctm *= m;
        Ok(())
    }

    fn op_set_line_width(&mut self, operands: &[Object]) -> Result<()> {
        self.state.line_width = get_number(&operands[0])?;
        Ok(())
    }

    fn op_set_line_cap(&mut self, operands: &[Object]) -> Result<()> {
        let cap = get_number(&operands[0])? as i32;
        self.state.line_cap = match cap {
            0 => Cap::Butt,
            1 => Cap::Round,
            2 => Cap::Square,
            _ => Cap::Butt,
        };
        Ok(())
    }

    fn op_set_line_join(&mut self, operands: &[Object]) -> Result<()> {
        let join = get_number(&operands[0])? as i32;
        self.state.line_join = match join {
            0 => Join::Miter,
            1 => Join::Round,
            2 => Join::Bevel,
            _ => Join::Miter,
        };
        Ok(())
    }

    // --- Path construction operators ---

    fn op_move_to(&mut self, operands: &[Object]) -> Result<()> {
        let vals = get_numbers(operands, 2)?;
        let pt = Point::new(vals[0], vals[1]);
        self.current_path.move_to(pt);
        self.current_point = Some(pt);
        Ok(())
    }

    fn op_line_to(&mut self, operands: &[Object]) -> Result<()> {
        let vals = get_numbers(operands, 2)?;
        let pt = Point::new(vals[0], vals[1]);
        self.current_path.line_to(pt);
        self.current_point = Some(pt);
        Ok(())
    }

    fn op_curve_to(&mut self, operands: &[Object]) -> Result<()> {
        let vals = get_numbers(operands, 6)?;
        let p1 = Point::new(vals[0], vals[1]);
        let p2 = Point::new(vals[2], vals[3]);
        let p3 = Point::new(vals[4], vals[5]);
        self.current_path.curve_to(p1, p2, p3);
        self.current_point = Some(p3);
        Ok(())
    }

    fn op_curve_to_v(&mut self, operands: &[Object]) -> Result<()> {
        let vals = get_numbers(operands, 4)?;
        let cp = self.current_point.unwrap_or(Point::ZERO);
        let p2 = Point::new(vals[0], vals[1]);
        let p3 = Point::new(vals[2], vals[3]);
        self.current_path.curve_to(cp, p2, p3);
        self.current_point = Some(p3);
        Ok(())
    }

    fn op_curve_to_y(&mut self, operands: &[Object]) -> Result<()> {
        let vals = get_numbers(operands, 4)?;
        let p1 = Point::new(vals[0], vals[1]);
        let p3 = Point::new(vals[2], vals[3]);
        self.current_path.curve_to(p1, p3, p3);
        self.current_point = Some(p3);
        Ok(())
    }

    fn op_close_path(&mut self) -> Result<()> {
        self.current_path.close_path();
        Ok(())
    }

    fn op_rectangle(&mut self, operands: &[Object]) -> Result<()> {
        let vals = get_numbers(operands, 4)?;
        let (x, y, w, h) = (vals[0], vals[1], vals[2], vals[3]);
        self.current_path.move_to(Point::new(x, y));
        self.current_path.line_to(Point::new(x + w, y));
        self.current_path.line_to(Point::new(x + w, y + h));
        self.current_path.line_to(Point::new(x, y + h));
        self.current_path.close_path();
        self.current_point = Some(Point::new(x, y));
        Ok(())
    }

    // --- Path painting operators ---

    fn apply_pending_clip(&mut self, scene: &mut Scene) {
        if self.pending_clip.take().is_some() {
            let transform = self.effective_transform();
            scene.push_clip_layer(transform, &self.current_path);
            self.clip_depth += 1;
        }
    }

    fn op_stroke(&mut self, scene: &mut Scene) -> Result<()> {
        self.apply_pending_clip(scene);
        let transform = self.effective_transform();
        let stroke = Stroke::new(self.state.line_width)
            .with_caps(self.state.line_cap)
            .with_join(self.state.line_join);
        scene.stroke(
            &stroke,
            transform,
            self.state.stroke_color,
            None,
            &self.current_path,
        );
        self.current_path = BezPath::new();
        self.current_point = None;
        Ok(())
    }

    fn op_fill(&mut self, scene: &mut Scene, rule: Fill) -> Result<()> {
        self.apply_pending_clip(scene);
        let transform = self.effective_transform();
        scene.fill(
            rule,
            transform,
            self.state.fill_color,
            None,
            &self.current_path,
        );
        self.current_path = BezPath::new();
        self.current_point = None;
        Ok(())
    }

    fn op_fill_stroke(&mut self, scene: &mut Scene, rule: Fill) -> Result<()> {
        self.apply_pending_clip(scene);
        let transform = self.effective_transform();
        let path = self.current_path.clone();
        scene.fill(rule, transform, self.state.fill_color, None, &path);
        let stroke = Stroke::new(self.state.line_width)
            .with_caps(self.state.line_cap)
            .with_join(self.state.line_join);
        scene.stroke(&stroke, transform, self.state.stroke_color, None, &path);
        self.current_path = BezPath::new();
        self.current_point = None;
        Ok(())
    }

    fn op_end_path(&mut self, scene: &mut Scene) -> Result<()> {
        self.apply_pending_clip(scene);
        self.current_path = BezPath::new();
        self.current_point = None;
        Ok(())
    }

    // --- Color operators ---

    fn op_set_fill_rgb(&mut self, operands: &[Object]) -> Result<()> {
        let vals = get_numbers(operands, 3)?;
        self.state.fill_color = Color::new([vals[0] as f32, vals[1] as f32, vals[2] as f32, 1.0]);
        Ok(())
    }

    fn op_set_stroke_rgb(&mut self, operands: &[Object]) -> Result<()> {
        let vals = get_numbers(operands, 3)?;
        self.state.stroke_color = Color::new([vals[0] as f32, vals[1] as f32, vals[2] as f32, 1.0]);
        Ok(())
    }

    fn op_set_fill_gray(&mut self, operands: &[Object]) -> Result<()> {
        let g = get_number(&operands[0])? as f32;
        self.state.fill_color = Color::new([g, g, g, 1.0]);
        Ok(())
    }

    fn op_set_stroke_gray(&mut self, operands: &[Object]) -> Result<()> {
        let g = get_number(&operands[0])? as f32;
        self.state.stroke_color = Color::new([g, g, g, 1.0]);
        Ok(())
    }

    fn op_set_fill_cmyk(&mut self, operands: &[Object]) -> Result<()> {
        let vals = get_numbers(operands, 4)?;
        let (c, m, y, k) = (vals[0], vals[1], vals[2], vals[3]);
        let r = ((1.0 - c) * (1.0 - k)) as f32;
        let g = ((1.0 - m) * (1.0 - k)) as f32;
        let b = ((1.0 - y) * (1.0 - k)) as f32;
        self.state.fill_color = Color::new([r, g, b, 1.0]);
        Ok(())
    }

    fn op_set_stroke_cmyk(&mut self, operands: &[Object]) -> Result<()> {
        let vals = get_numbers(operands, 4)?;
        let (c, m, y, k) = (vals[0], vals[1], vals[2], vals[3]);
        let r = ((1.0 - c) * (1.0 - k)) as f32;
        let g = ((1.0 - m) * (1.0 - k)) as f32;
        let b = ((1.0 - y) * (1.0 - k)) as f32;
        self.state.stroke_color = Color::new([r, g, b, 1.0]);
        Ok(())
    }

    // --- Text operators ---

    fn op_begin_text(&mut self) -> Result<()> {
        self.text_state.text_matrix = Affine::IDENTITY;
        self.text_state.text_line_matrix = Affine::IDENTITY;
        Ok(())
    }

    fn op_end_text(&mut self) -> Result<()> {
        Ok(())
    }

    fn op_set_font(&mut self, operands: &[Object]) -> Result<()> {
        if operands.len() >= 2 {
            let font_name = match &operands[0] {
                Object::Name(n) => n.clone(),
                _ => return Err(anyhow!("Tf: expected name")),
            };
            self.text_state.font_size = get_number(&operands[1])?;
            self.text_state.current_font = self.page_fonts.get(&font_name).cloned();
            self.text_state.current_font_name = font_name.clone();
            if self.text_state.current_font.is_none() {
                log::debug!(
                    "Font not found in page resources: {}",
                    String::from_utf8_lossy(&font_name)
                );
            }
        }
        Ok(())
    }

    fn op_text_move(&mut self, operands: &[Object]) -> Result<()> {
        let vals = get_numbers(operands, 2)?;
        let translate = Affine::translate((vals[0], vals[1]));
        self.text_state.text_line_matrix *= translate;
        self.text_state.text_matrix = self.text_state.text_line_matrix;
        Ok(())
    }

    fn op_text_move_set_leading(&mut self, operands: &[Object]) -> Result<()> {
        let vals = get_numbers(operands, 2)?;
        self.text_state.leading = -vals[1];
        let translate = Affine::translate((vals[0], vals[1]));
        self.text_state.text_line_matrix *= translate;
        self.text_state.text_matrix = self.text_state.text_line_matrix;
        Ok(())
    }

    fn op_text_matrix(&mut self, operands: &[Object]) -> Result<()> {
        let vals = get_numbers(operands, 6)?;
        let m = Affine::new([vals[0], vals[1], vals[2], vals[3], vals[4], vals[5]]);
        self.text_state.text_matrix = m;
        self.text_state.text_line_matrix = m;
        Ok(())
    }

    fn op_text_next_line(&mut self) -> Result<()> {
        let leading = if self.text_state.leading != 0.0 {
            self.text_state.leading
        } else {
            -self.text_state.font_size
        };
        let translate = Affine::translate((0.0, -leading));
        self.text_state.text_line_matrix *= translate;
        self.text_state.text_matrix = self.text_state.text_line_matrix;
        Ok(())
    }

    fn op_show_text(&mut self, scene: &mut Scene, operands: &[Object]) -> Result<()> {
        let bytes = match operands.first() {
            Some(Object::String(bytes, _)) => bytes,
            _ => return Ok(()),
        };
        self.render_text_string(scene, bytes)
    }

    fn op_show_text_array(&mut self, scene: &mut Scene, operands: &[Object]) -> Result<()> {
        let arr = match operands.first() {
            Some(Object::Array(arr)) => arr,
            _ => return Ok(()),
        };

        let h_scale = self.text_state.horizontal_scaling / 100.0;

        for item in arr {
            match item {
                Object::String(bytes, _) => {
                    self.render_text_string(scene, bytes)?;
                }
                Object::Integer(n) => {
                    let adjustment = -*n as f64 / 1000.0 * self.text_state.font_size * h_scale;
                    self.text_state.text_matrix *= Affine::translate((adjustment, 0.0));
                }
                Object::Real(n) => {
                    let adjustment = -*n as f64 / 1000.0 * self.text_state.font_size * h_scale;
                    self.text_state.text_matrix *= Affine::translate((adjustment, 0.0));
                }
                _ => {}
            }
        }
        Ok(())
    }

    fn render_text_string(&mut self, scene: &mut Scene, bytes: &[u8]) -> Result<()> {
        // Invisible text mode
        if self.text_state.render_mode == 3 {
            return Ok(());
        }

        let font = match &self.text_state.current_font {
            Some(f) => f.clone(),
            None => {
                // Fall back to placeholder
                self.draw_text_placeholder(scene, bytes.len());
                let advance = self.text_state.font_size * 0.6 * bytes.len() as f64;
                self.text_state.text_matrix *= Affine::translate((advance, 0.0));
                return Ok(());
            }
        };

        let decoded = fonts::decode_string(bytes, &font.encoding);
        if decoded.is_empty() {
            return Ok(());
        }

        // Build glyph run
        let font_ref = skrifa::FontRef::new(font.font_data.data.as_ref())
            .or_else(|_| {
                skrifa::FontRef::from_index(font.font_data.data.as_ref(), font.font_data.index)
            })
            .ok();

        let charmap = font_ref.as_ref().map(|f| {
            use skrifa::MetadataProvider;
            f.charmap()
        });

        let font_size = self.text_state.font_size;
        let h_scale = self.text_state.horizontal_scaling / 100.0;
        let is_identity = matches!(font.encoding, PdfEncoding::Identity);

        let mut glyphs = Vec::with_capacity(decoded.len());
        let mut cursor_x: f64 = 0.0;

        for (unicode_char, char_code) in &decoded {
            // Get glyph ID
            let glyph_id = if is_identity {
                // For Identity encoding, char_code is the glyph ID
                skrifa::GlyphId::new(*char_code as u32)
            } else {
                // Try to map Unicode char through font's cmap
                charmap
                    .as_ref()
                    .and_then(|cm| cm.map(*unicode_char))
                    .unwrap_or(skrifa::GlyphId::new(*char_code as u32))
            };

            glyphs.push(vello::Glyph {
                id: glyph_id.to_u32(),
                x: cursor_x as f32,
                y: self.text_state.text_rise as f32,
            });

            // Compute advance width
            let advance = get_char_advance(&font, *char_code, font_size);

            let extra = if *unicode_char == ' ' {
                self.text_state.word_spacing
            } else {
                0.0
            };

            cursor_x += (advance + self.text_state.char_spacing + extra) * h_scale;
        }

        if !glyphs.is_empty() {
            // Transform: base_transform * CTM * text_matrix * glyph_y_flip
            // The glyph_y_flip compensates for the base_transform Y flip
            let text_transform = self.base_transform * self.state.ctm * self.text_state.text_matrix;
            let glyph_flip = Affine::new([1.0, 0.0, 0.0, -1.0, 0.0, 0.0]);
            let transform = text_transform * glyph_flip;

            let brush = self.state.fill_color;

            scene
                .draw_glyphs(&font.font_data)
                .font_size(font_size as f32)
                .transform(transform)
                .brush(brush)
                .draw(Fill::NonZero, glyphs.into_iter());
        }

        // Advance text matrix
        self.text_state.text_matrix *= Affine::translate((cursor_x, 0.0));
        Ok(())
    }

    fn draw_text_placeholder(&self, scene: &mut Scene, char_count: usize) {
        let font_size = self.text_state.font_size;
        let width = font_size * 0.6 * char_count as f64;
        let height = font_size;
        let text_transform = self.base_transform * self.state.ctm * self.text_state.text_matrix;
        let rect = Rect::new(0.0, -height * 0.8, width, height * 0.2);
        let placeholder_color = Color::new([0.6, 0.6, 0.9, 0.3]);
        scene.fill(
            Fill::NonZero,
            text_transform,
            placeholder_color,
            None,
            &rect,
        );
    }

    // --- XObject operators ---

    fn op_do_xobject(&mut self, scene: &mut Scene, operands: &[Object]) -> Result<()> {
        let name = match operands.first() {
            Some(Object::Name(n)) => n,
            _ => return Err(anyhow!("Do: expected name")),
        };

        let xobject_id = match self.get_xobject_ref(name) {
            Ok(id) => id,
            Err(e) => {
                log::debug!(
                    "XObject '{}' not found: {}",
                    String::from_utf8_lossy(name),
                    e
                );
                return Ok(());
            }
        };

        let obj = self
            .doc
            .get_object(xobject_id)
            .map_err(|e| anyhow!("Failed to get XObject: {}", e))?;

        let stream = match obj {
            Object::Stream(ref s) => s,
            _ => {
                log::debug!("XObject is not a stream");
                return Ok(());
            }
        };

        let subtype: &[u8] = stream
            .dict
            .get(b"Subtype")
            .ok()
            .and_then(|o| o.as_name().ok())
            .unwrap_or(b"");

        match subtype {
            b"Image" => {
                match images::decode_image_xobject(self.doc, stream) {
                    Ok(decoded) => {
                        let transform = self.effective_transform();
                        let image_brush = ImageBrush::new(decoded.image_data);
                        scene.draw_image(&image_brush, transform);
                    }
                    Err(e) => {
                        log::warn!("Failed to decode image XObject: {}", e);
                    }
                }
                Ok(())
            }
            b"Form" => {
                log::debug!("Ignoring Form XObject");
                Ok(())
            }
            _ => {
                log::debug!(
                    "Unknown XObject subtype: {}",
                    String::from_utf8_lossy(subtype)
                );
                Ok(())
            }
        }
    }

    fn get_xobject_ref(&self, name: &[u8]) -> Result<ObjectId> {
        let page = self
            .doc
            .get_dictionary(self.page_id)
            .map_err(|e| anyhow!("Failed to get page dict: {}", e))?;

        let resources = match page.get(b"Resources") {
            Ok(Object::Dictionary(d)) => d.clone(),
            Ok(Object::Reference(id)) => self
                .doc
                .get_dictionary(*id)
                .map_err(|e| anyhow!("Failed to resolve Resources: {}", e))?
                .clone(),
            _ => return Err(anyhow!("No Resources on page")),
        };

        let xobjects = match resources.get(b"XObject") {
            Ok(Object::Dictionary(d)) => d,
            Ok(Object::Reference(id)) => self
                .doc
                .get_dictionary(*id)
                .map_err(|e| anyhow!("Failed to resolve XObject dict: {}", e))?,
            _ => return Err(anyhow!("No XObject dict in Resources")),
        };

        match xobjects.get(name) {
            Ok(Object::Reference(id)) => Ok(*id),
            _ => Err(anyhow!(
                "XObject '{}' not found",
                String::from_utf8_lossy(name)
            )),
        }
    }
}

// --- Helper functions ---

fn get_char_advance(font: &PdfFont, char_code: u16, font_size: f64) -> f64 {
    let code_idx = char_code as u32;
    if code_idx >= font.first_char && ((code_idx - font.first_char) as usize) < font.widths.len() {
        let w = font.widths[(code_idx - font.first_char) as usize];
        w / 1000.0 * font_size
    } else if !font.widths.is_empty()
        && font.first_char == 0
        && (code_idx as usize) < font.widths.len()
    {
        // CID fonts with first_char=0
        let w = font.widths[code_idx as usize];
        w / 1000.0 * font_size
    } else {
        // Fallback: use a reasonable default advance
        font_size * 0.6
    }
}

fn get_number(obj: &Object) -> Result<f64> {
    match obj {
        Object::Integer(i) => Ok(*i as f64),
        Object::Real(f) => Ok(*f as f64),
        _ => Err(anyhow!("Expected number, got {:?}", obj)),
    }
}

fn get_numbers(operands: &[Object], count: usize) -> Result<Vec<f64>> {
    if operands.len() < count {
        return Err(anyhow!(
            "Expected {} operands, got {}",
            count,
            operands.len()
        ));
    }
    operands[..count].iter().map(get_number).collect()
}
