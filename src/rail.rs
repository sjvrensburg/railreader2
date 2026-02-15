use crate::layout::{is_navigable, LayoutBlock, LineInfo, PageAnalysis};

const RAIL_ZOOM_THRESHOLD: f64 = 1.5;
const VERTICAL_SWIPE_THRESHOLD: f64 = 20.0;
const SNAP_DURATION_MS: f64 = 300.0;

#[derive(Debug)]
pub enum NavResult {
    Ok,
    PageBoundaryNext,
    PageBoundaryPrev,
}

struct SnapAnimation {
    start_x: f64,
    start_y: f64,
    target_x: f64,
    target_y: f64,
    start_time: std::time::Instant,
}

pub struct RailNav {
    analysis: Option<PageAnalysis>,
    navigable_indices: Vec<usize>,
    pub current_block: usize,
    pub current_line: usize,
    pub active: bool,
    accumulated_dy: f64,
    snap: Option<SnapAnimation>,
}

impl Default for RailNav {
    fn default() -> Self {
        Self::new()
    }
}

impl RailNav {
    pub fn new() -> Self {
        Self {
            analysis: None,
            navigable_indices: Vec::new(),
            current_block: 0,
            current_line: 0,
            active: false,
            accumulated_dy: 0.0,
            snap: None,
        }
    }

    pub fn set_analysis(&mut self, analysis: PageAnalysis) {
        self.navigable_indices = analysis
            .blocks
            .iter()
            .enumerate()
            .filter(|(_, b)| is_navigable(b.class_id))
            .map(|(i, _)| i)
            .collect();
        self.analysis = Some(analysis);
        self.current_block = 0;
        self.current_line = 0;
        self.accumulated_dy = 0.0;
        self.snap = None;
    }

    pub fn has_analysis(&self) -> bool {
        self.analysis.is_some() && !self.navigable_indices.is_empty()
    }

    pub fn update_zoom(
        &mut self,
        zoom: f64,
        camera_x: f64,
        camera_y: f64,
        window_width: f64,
        window_height: f64,
    ) {
        let should_be_active = zoom >= RAIL_ZOOM_THRESHOLD && self.has_analysis();

        if should_be_active && !self.active {
            // Activating - find nearest block to viewport center
            self.active = true;
            self.find_nearest_block(camera_x, camera_y, zoom, window_width, window_height);
        } else if !should_be_active && self.active {
            self.active = false;
            self.snap = None;
        }
    }

    fn find_nearest_block(
        &mut self,
        camera_x: f64,
        camera_y: f64,
        zoom: f64,
        window_width: f64,
        window_height: f64,
    ) {
        let analysis = match &self.analysis {
            Some(a) => a,
            None => return,
        };

        // Viewport center in page coordinates
        let center_x = (window_width / 2.0 - camera_x) / zoom;
        let center_y = (window_height / 2.0 - camera_y) / zoom;

        let mut best_dist = f64::MAX;
        let mut best_idx = 0;

        for (i, &block_idx) in self.navigable_indices.iter().enumerate() {
            let block = &analysis.blocks[block_idx];
            let bx = block.bbox.x as f64 + block.bbox.w as f64 / 2.0;
            let by = block.bbox.y as f64 + block.bbox.h as f64 / 2.0;
            let dist = (bx - center_x).powi(2) + (by - center_y).powi(2);
            if dist < best_dist {
                best_dist = dist;
                best_idx = i;
            }
        }

        self.current_block = best_idx;
        self.current_line = 0;
    }

    pub fn next_line(&mut self) -> NavResult {
        if !self.active || self.navigable_indices.is_empty() {
            return NavResult::Ok;
        }

        let block = self.current_navigable_block();
        let line_count = block.lines.len();

        if self.current_line + 1 < line_count {
            self.current_line += 1;
            NavResult::Ok
        } else if self.current_block + 1 < self.navigable_indices.len() {
            self.current_block += 1;
            self.current_line = 0;
            NavResult::Ok
        } else {
            NavResult::PageBoundaryNext
        }
    }

    pub fn prev_line(&mut self) -> NavResult {
        if !self.active || self.navigable_indices.is_empty() {
            return NavResult::Ok;
        }

        if self.current_line > 0 {
            self.current_line -= 1;
            NavResult::Ok
        } else if self.current_block > 0 {
            self.current_block -= 1;
            let block = self.current_navigable_block();
            self.current_line = block.lines.len().saturating_sub(1);
            NavResult::Ok
        } else {
            NavResult::PageBoundaryPrev
        }
    }

    pub fn next_block(&mut self) -> NavResult {
        if !self.active || self.navigable_indices.is_empty() {
            return NavResult::Ok;
        }

        if self.current_block + 1 < self.navigable_indices.len() {
            self.current_block += 1;
            self.current_line = 0;
            NavResult::Ok
        } else {
            NavResult::PageBoundaryNext
        }
    }

    pub fn prev_block(&mut self) -> NavResult {
        if !self.active || self.navigable_indices.is_empty() {
            return NavResult::Ok;
        }

        if self.current_block > 0 {
            self.current_block -= 1;
            self.current_line = 0;
            NavResult::Ok
        } else {
            NavResult::PageBoundaryPrev
        }
    }

    pub fn handle_scroll(&mut self, dy: f64) -> Option<NavResult> {
        if !self.active {
            return None;
        }

        self.accumulated_dy += dy;

        if self.accumulated_dy > VERTICAL_SWIPE_THRESHOLD {
            self.accumulated_dy = 0.0;
            Some(self.prev_line())
        } else if self.accumulated_dy < -VERTICAL_SWIPE_THRESHOLD {
            self.accumulated_dy = 0.0;
            Some(self.next_line())
        } else {
            None
        }
    }

    pub fn constrain_pan(&self, camera_x: &mut f64, zoom: f64, window_width: f64) {
        if !self.active || self.navigable_indices.is_empty() {
            return;
        }

        let block = self.current_navigable_block();
        let margin = block.bbox.w as f64 * 0.05;
        let block_left = block.bbox.x as f64 - margin;
        let block_right = block.bbox.x as f64 + block.bbox.w as f64 + margin;

        // Camera offset such that block edges are visible
        let max_offset_x = -block_left * zoom + window_width * 0.05;
        let min_offset_x = -(block_right * zoom) + window_width * 0.95;

        *camera_x = camera_x.clamp(min_offset_x, max_offset_x);
    }

    pub fn start_snap_to_current(
        &mut self,
        camera_x: f64,
        camera_y: f64,
        zoom: f64,
        window_width: f64,
        window_height: f64,
    ) {
        if !self.active || self.navigable_indices.is_empty() {
            return;
        }

        let (target_x, target_y) = self.compute_target_camera(zoom, window_width, window_height);

        self.snap = Some(SnapAnimation {
            start_x: camera_x,
            start_y: camera_y,
            target_x,
            target_y,
            start_time: std::time::Instant::now(),
        });
    }

    fn compute_target_camera(
        &self,
        zoom: f64,
        window_width: f64,
        window_height: f64,
    ) -> (f64, f64) {
        let block = self.current_navigable_block();
        let line = self.current_line_info();

        // Center the current line vertically
        let target_y = window_height / 2.0 - line.y as f64 * zoom;

        // Center the block horizontally
        let block_cx = block.bbox.x as f64 + block.bbox.w as f64 / 2.0;
        let target_x = window_width / 2.0 - block_cx * zoom;

        (target_x, target_y)
    }

    /// Advance snap animation. Returns true if still animating.
    pub fn tick(&mut self, camera_x: &mut f64, camera_y: &mut f64) -> bool {
        let snap = match &self.snap {
            Some(s) => s,
            None => return false,
        };

        let elapsed = snap.start_time.elapsed().as_secs_f64() * 1000.0;
        let t = (elapsed / SNAP_DURATION_MS).min(1.0);
        let eased = 1.0 - (1.0 - t).powi(3); // cubic ease-out

        *camera_x = snap.start_x + (snap.target_x - snap.start_x) * eased;
        *camera_y = snap.start_y + (snap.target_y - snap.start_y) * eased;

        if t >= 1.0 {
            self.snap = None;
            false
        } else {
            true
        }
    }

    pub fn is_animating(&self) -> bool {
        self.snap.is_some()
    }

    pub fn current_navigable_block(&self) -> &LayoutBlock {
        let analysis = self.analysis.as_ref().unwrap();
        let block_idx = self.navigable_indices[self.current_block];
        &analysis.blocks[block_idx]
    }

    pub fn current_line_info(&self) -> &LineInfo {
        let block = self.current_navigable_block();
        &block.lines[self.current_line.min(block.lines.len() - 1)]
    }

    pub fn navigable_count(&self) -> usize {
        self.navigable_indices.len()
    }

    pub fn current_line_count(&self) -> usize {
        if self.navigable_indices.is_empty() {
            return 0;
        }
        self.current_navigable_block().lines.len()
    }

    pub fn analysis(&self) -> Option<&PageAnalysis> {
        self.analysis.as_ref()
    }
}
