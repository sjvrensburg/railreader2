use anyhow::{anyhow, Result};
use lopdf::content::Operation;
use vello::kurbo::{Affine, BezPath, Cap, Join, Point, Rect, Stroke};
use vello::peniko::{Color, Fill};
use vello::Scene;

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
}

impl Default for TextState {
    fn default() -> Self {
        Self {
            text_matrix: Affine::IDENTITY,
            text_line_matrix: Affine::IDENTITY,
            font_size: 12.0,
        }
    }
}

pub struct VelloPdfInterpreter {
    state_stack: Vec<GraphicsState>,
    state: GraphicsState,
    current_path: BezPath,
    base_transform: Affine,
    text_state: TextState,
    clip_depth: usize,
    _page_width: f64,
    _page_height: f64,
    current_point: Option<Point>,
    pending_clip: Option<Fill>,
}

impl VelloPdfInterpreter {
    pub fn new(page_width: f64, page_height: f64) -> Self {
        // Flip Y: PDF origin is bottom-left, vello is top-left
        let base_transform = Affine::new([1.0, 0.0, 0.0, -1.0, 0.0, page_height]);
        Self {
            state_stack: Vec::new(),
            state: GraphicsState::default(),
            current_path: BezPath::new(),
            base_transform,
            text_state: TextState::default(),
            clip_depth: 0,
            _page_width: page_width,
            _page_height: page_height,
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
        // Pop any remaining clip layers
        for _ in 0..self.clip_depth {
            scene.pop_layer();
        }
        Ok(())
    }

    fn execute(&mut self, scene: &mut Scene, op: &Operation) -> Result<()> {
        match op.operator.as_str() {
            // --- Graphics state ---
            "q" => self.op_save(),
            "Q" => self.op_restore(scene),
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
            "TD" => self.op_text_move(&op.operands), // simplified: same as Td for PoC
            "Tm" => self.op_text_matrix(&op.operands),
            "T*" => self.op_text_next_line(),
            "Tj" => self.op_show_text(scene, &op.operands),
            "TJ" => self.op_show_text_array(scene, &op.operands),
            "'" => self.op_show_text(scene, &op.operands), // simplified

            // --- XObject ---
            "Do" => {
                log::debug!("Ignoring XObject Do operator");
                Ok(())
            }

            // --- Misc (ignore) ---
            "d" | "i" | "M" | "gs" | "ri" | "Tc" | "Tw" | "Tz" | "TL" | "Tr" | "Ts" => {
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

    fn op_restore(&mut self, _scene: &mut Scene) -> Result<()> {
        if let Some(state) = self.state_stack.pop() {
            self.state = state;
        }
        // If we had a pending clip from this level, it's now gone
        self.pending_clip = None;
        Ok(())
    }

    fn op_concat_matrix(&mut self, operands: &[lopdf::Object]) -> Result<()> {
        let vals = get_numbers(operands, 6)?;
        let m = Affine::new([vals[0], vals[1], vals[2], vals[3], vals[4], vals[5]]);
        self.state.ctm *= m;
        Ok(())
    }

    fn op_set_line_width(&mut self, operands: &[lopdf::Object]) -> Result<()> {
        self.state.line_width = get_number(&operands[0])?;
        Ok(())
    }

    fn op_set_line_cap(&mut self, operands: &[lopdf::Object]) -> Result<()> {
        let cap = get_number(&operands[0])? as i32;
        self.state.line_cap = match cap {
            0 => Cap::Butt,
            1 => Cap::Round,
            2 => Cap::Square,
            _ => Cap::Butt,
        };
        Ok(())
    }

    fn op_set_line_join(&mut self, operands: &[lopdf::Object]) -> Result<()> {
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

    fn op_move_to(&mut self, operands: &[lopdf::Object]) -> Result<()> {
        let vals = get_numbers(operands, 2)?;
        let pt = Point::new(vals[0], vals[1]);
        self.current_path.move_to(pt);
        self.current_point = Some(pt);
        Ok(())
    }

    fn op_line_to(&mut self, operands: &[lopdf::Object]) -> Result<()> {
        let vals = get_numbers(operands, 2)?;
        let pt = Point::new(vals[0], vals[1]);
        self.current_path.line_to(pt);
        self.current_point = Some(pt);
        Ok(())
    }

    fn op_curve_to(&mut self, operands: &[lopdf::Object]) -> Result<()> {
        let vals = get_numbers(operands, 6)?;
        let p1 = Point::new(vals[0], vals[1]);
        let p2 = Point::new(vals[2], vals[3]);
        let p3 = Point::new(vals[4], vals[5]);
        self.current_path.curve_to(p1, p2, p3);
        self.current_point = Some(p3);
        Ok(())
    }

    fn op_curve_to_v(&mut self, operands: &[lopdf::Object]) -> Result<()> {
        let vals = get_numbers(operands, 4)?;
        let cp = self.current_point.unwrap_or(Point::ZERO);
        let p2 = Point::new(vals[0], vals[1]);
        let p3 = Point::new(vals[2], vals[3]);
        self.current_path.curve_to(cp, p2, p3);
        self.current_point = Some(p3);
        Ok(())
    }

    fn op_curve_to_y(&mut self, operands: &[lopdf::Object]) -> Result<()> {
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

    fn op_rectangle(&mut self, operands: &[lopdf::Object]) -> Result<()> {
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

    fn op_set_fill_rgb(&mut self, operands: &[lopdf::Object]) -> Result<()> {
        let vals = get_numbers(operands, 3)?;
        self.state.fill_color = Color::new([vals[0] as f32, vals[1] as f32, vals[2] as f32, 1.0]);
        Ok(())
    }

    fn op_set_stroke_rgb(&mut self, operands: &[lopdf::Object]) -> Result<()> {
        let vals = get_numbers(operands, 3)?;
        self.state.stroke_color = Color::new([vals[0] as f32, vals[1] as f32, vals[2] as f32, 1.0]);
        Ok(())
    }

    fn op_set_fill_gray(&mut self, operands: &[lopdf::Object]) -> Result<()> {
        let g = get_number(&operands[0])? as f32;
        self.state.fill_color = Color::new([g, g, g, 1.0]);
        Ok(())
    }

    fn op_set_stroke_gray(&mut self, operands: &[lopdf::Object]) -> Result<()> {
        let g = get_number(&operands[0])? as f32;
        self.state.stroke_color = Color::new([g, g, g, 1.0]);
        Ok(())
    }

    fn op_set_fill_cmyk(&mut self, operands: &[lopdf::Object]) -> Result<()> {
        let vals = get_numbers(operands, 4)?;
        let (c, m, y, k) = (vals[0], vals[1], vals[2], vals[3]);
        let r = ((1.0 - c) * (1.0 - k)) as f32;
        let g = ((1.0 - m) * (1.0 - k)) as f32;
        let b = ((1.0 - y) * (1.0 - k)) as f32;
        self.state.fill_color = Color::new([r, g, b, 1.0]);
        Ok(())
    }

    fn op_set_stroke_cmyk(&mut self, operands: &[lopdf::Object]) -> Result<()> {
        let vals = get_numbers(operands, 4)?;
        let (c, m, y, k) = (vals[0], vals[1], vals[2], vals[3]);
        let r = ((1.0 - c) * (1.0 - k)) as f32;
        let g = ((1.0 - m) * (1.0 - k)) as f32;
        let b = ((1.0 - y) * (1.0 - k)) as f32;
        self.state.stroke_color = Color::new([r, g, b, 1.0]);
        Ok(())
    }

    // --- Text operators (placeholder rendering) ---

    fn op_begin_text(&mut self) -> Result<()> {
        self.text_state.text_matrix = Affine::IDENTITY;
        self.text_state.text_line_matrix = Affine::IDENTITY;
        Ok(())
    }

    fn op_end_text(&mut self) -> Result<()> {
        Ok(())
    }

    fn op_set_font(&mut self, operands: &[lopdf::Object]) -> Result<()> {
        if operands.len() >= 2 {
            self.text_state.font_size = get_number(&operands[1])?;
        }
        Ok(())
    }

    fn op_text_move(&mut self, operands: &[lopdf::Object]) -> Result<()> {
        let vals = get_numbers(operands, 2)?;
        let translate = Affine::translate((vals[0], vals[1]));
        self.text_state.text_line_matrix *= translate;
        self.text_state.text_matrix = self.text_state.text_line_matrix;
        Ok(())
    }

    fn op_text_matrix(&mut self, operands: &[lopdf::Object]) -> Result<()> {
        let vals = get_numbers(operands, 6)?;
        let m = Affine::new([vals[0], vals[1], vals[2], vals[3], vals[4], vals[5]]);
        self.text_state.text_matrix = m;
        self.text_state.text_line_matrix = m;
        Ok(())
    }

    fn op_text_next_line(&mut self) -> Result<()> {
        // Move to next line using default leading (approximate)
        let translate = Affine::translate((0.0, -self.text_state.font_size));
        self.text_state.text_line_matrix *= translate;
        self.text_state.text_matrix = self.text_state.text_line_matrix;
        Ok(())
    }

    fn op_show_text(&mut self, scene: &mut Scene, operands: &[lopdf::Object]) -> Result<()> {
        let char_count = match operands.first() {
            Some(lopdf::Object::String(bytes, _)) => bytes.len().max(1),
            _ => 4,
        };
        self.draw_text_placeholder(scene, char_count);
        // Advance text position
        let advance = self.text_state.font_size * 0.6 * char_count as f64;
        let translate = Affine::translate((advance, 0.0));
        self.text_state.text_matrix *= translate;
        Ok(())
    }

    fn op_show_text_array(&mut self, scene: &mut Scene, operands: &[lopdf::Object]) -> Result<()> {
        let total_chars = match operands.first() {
            Some(lopdf::Object::Array(arr)) => {
                let mut count = 0usize;
                for item in arr {
                    if let lopdf::Object::String(bytes, _) = item {
                        count += bytes.len();
                    }
                }
                count.max(1)
            }
            _ => 4,
        };
        self.draw_text_placeholder(scene, total_chars);
        let advance = self.text_state.font_size * 0.6 * total_chars as f64;
        let translate = Affine::translate((advance, 0.0));
        self.text_state.text_matrix *= translate;
        Ok(())
    }

    fn draw_text_placeholder(&self, scene: &mut Scene, char_count: usize) {
        let font_size = self.text_state.font_size;
        let width = font_size * 0.6 * char_count as f64;
        let height = font_size;

        // The text rendering matrix combines CTM with text matrix
        let text_transform = self.base_transform * self.state.ctm * self.text_state.text_matrix;

        // Draw a semi-transparent rectangle as placeholder
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
}

// --- Helper functions ---

fn get_number(obj: &lopdf::Object) -> Result<f64> {
    match obj {
        lopdf::Object::Integer(i) => Ok(*i as f64),
        lopdf::Object::Real(f) => Ok(*f as f64),
        _ => Err(anyhow!("Expected number, got {:?}", obj)),
    }
}

fn get_numbers(operands: &[lopdf::Object], count: usize) -> Result<Vec<f64>> {
    if operands.len() < count {
        return Err(anyhow!(
            "Expected {} operands, got {}",
            count,
            operands.len()
        ));
    }
    operands[..count].iter().map(get_number).collect()
}
