use crate::layout::{is_navigable, LayoutBlock, LineInfo, PageAnalysis};

const RAIL_ZOOM_THRESHOLD: f64 = 3.0;
const SNAP_DURATION_MS: f64 = 300.0;

/// Horizontal scroll speed in page-coord points per second (initial).
const SCROLL_SPEED_START: f64 = 60.0;
/// Maximum scroll speed after holding for a while.
const SCROLL_SPEED_MAX: f64 = 400.0;
/// Time in seconds to reach max speed from start.
const SCROLL_RAMP_TIME: f64 = 1.5;

#[derive(Debug)]
pub enum NavResult {
    Ok,
    PageBoundaryNext,
    PageBoundaryPrev,
}

/// Horizontal scroll direction while key is held.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum ScrollDir {
    Forward,
    Backward,
}

struct SnapAnimation {
    start_x: f64,
    start_y: f64,
    target_x: f64,
    target_y: f64,
    start_time: std::time::Instant,
    duration_ms: f64,
}

pub struct RailNav {
    analysis: Option<PageAnalysis>,
    navigable_indices: Vec<usize>,
    pub current_block: usize,
    pub current_line: usize,
    pub active: bool,
    snap: Option<SnapAnimation>,
    /// Current held-scroll direction (None = not scrolling).
    scroll_dir: Option<ScrollDir>,
    /// When the current scroll hold started.
    scroll_hold_start: Option<std::time::Instant>,
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
            snap: None,
            scroll_dir: None,
            scroll_hold_start: None,
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
        self.snap = None;
        self.scroll_dir = None;
        self.scroll_hold_start = None;
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
            self.active = true;
            self.find_nearest_block(camera_x, camera_y, zoom, window_width, window_height);
        } else if !should_be_active && self.active {
            self.active = false;
            self.snap = None;
            self.scroll_dir = None;
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

    /// Move to next line within block, or next block, or signal page boundary.
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

    /// Move to previous line, or previous block (last line), or signal page boundary.
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

    /// Start continuous horizontal scrolling (called on key press).
    pub fn start_scroll(&mut self, dir: ScrollDir) {
        if !self.active || self.navigable_indices.is_empty() {
            return;
        }
        if self.scroll_dir != Some(dir) {
            self.scroll_dir = Some(dir);
            self.scroll_hold_start = Some(std::time::Instant::now());
        }
    }

    /// Stop continuous horizontal scrolling (called on key release).
    pub fn stop_scroll(&mut self) {
        self.scroll_dir = None;
        self.scroll_hold_start = None;
    }

    /// Clamp camera X so the viewport stays within the current block bounds.
    fn clamp_x(&self, camera_x: f64, zoom: f64, window_width: f64) -> f64 {
        if self.navigable_indices.is_empty() {
            return camera_x;
        }

        let block = self.current_navigable_block();
        let margin = block.bbox.w as f64 * 0.05;
        let block_left = block.bbox.x as f64 - margin;
        let block_right = block.bbox.x as f64 + block.bbox.w as f64 + margin;
        let block_width_px = (block_right - block_left) * zoom;

        if block_width_px <= window_width {
            // Block fits in viewport — center it
            let center = (block_left + block_right) / 2.0;
            window_width / 2.0 - center * zoom
        } else {
            // offset_x such that left edge of block is at left edge of window
            let max_x = -block_left * zoom;
            // offset_x such that right edge of block is at right edge of window
            let min_x = window_width - block_right * zoom;
            camera_x.clamp(min_x, max_x)
        }
    }

    /// Start a snap animation to the current line's start (left edge of block).
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
            duration_ms: SNAP_DURATION_MS,
        });
    }

    /// Compute camera position that shows the START of the current line.
    /// Left edge of block with small margin, line centered vertically.
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

        // Position at LEFT edge of block with a 5% window margin
        let target_x = window_width * 0.05 - block.bbox.x as f64 * zoom;

        (target_x, target_y)
    }

    /// Advance animations and continuous scroll. Returns true if still animating.
    pub fn tick(
        &mut self,
        camera_x: &mut f64,
        camera_y: &mut f64,
        dt_secs: f64,
        zoom: f64,
        window_width: f64,
    ) -> bool {
        let mut animating = false;

        // Handle snap animation (for vertical line snaps)
        if let Some(snap) = &self.snap {
            let elapsed = snap.start_time.elapsed().as_secs_f64() * 1000.0;
            let t = (elapsed / snap.duration_ms).min(1.0);
            let eased = 1.0 - (1.0 - t).powi(3); // cubic ease-out

            *camera_x = snap.start_x + (snap.target_x - snap.start_x) * eased;
            *camera_y = snap.start_y + (snap.target_y - snap.start_y) * eased;

            if t >= 1.0 {
                self.snap = None;
            } else {
                animating = true;
            }
        }

        // Handle continuous horizontal scrolling
        if let (Some(dir), Some(hold_start)) = (self.scroll_dir, self.scroll_hold_start) {
            let hold_secs = hold_start.elapsed().as_secs_f64();
            // Ramp from start speed to max speed over SCROLL_RAMP_TIME
            let t = (hold_secs / SCROLL_RAMP_TIME).min(1.0);
            let speed = SCROLL_SPEED_START + (SCROLL_SPEED_MAX - SCROLL_SPEED_START) * t * t;

            let delta = speed * dt_secs * zoom;
            let new_x = match dir {
                ScrollDir::Forward => *camera_x - delta,
                ScrollDir::Backward => *camera_x + delta,
            };
            *camera_x = self.clamp_x(new_x, zoom, window_width);
            animating = true;
        }

        animating
    }

    pub fn is_animating(&self) -> bool {
        self.snap.is_some() || self.scroll_dir.is_some()
    }

    /// Jump to the last block and last line (for navigating back from next page).
    pub fn jump_to_end(&mut self) {
        if self.navigable_indices.is_empty() {
            return;
        }
        self.current_block = self.navigable_indices.len() - 1;
        let block = self.current_navigable_block();
        self.current_line = block.lines.len().saturating_sub(1);
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
