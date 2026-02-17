use std::collections::{HashMap, HashSet, VecDeque};
use std::sync::atomic::{AtomicU64, Ordering};

use crate::config::Config;
use crate::layout::{self, PageAnalysis};
use crate::rail::RailNav;
use crate::worker::{AnalysisRequest, AnalysisWorker};

const ZOOM_MIN: f64 = 0.1;
const ZOOM_MAX: f64 = 20.0;

static NEXT_TAB_ID: AtomicU64 = AtomicU64::new(1);

pub type TabId = u64;

fn next_tab_id() -> TabId {
    NEXT_TAB_ID.fetch_add(1, Ordering::Relaxed)
}

#[derive(Debug, Clone)]
pub struct Outline {
    pub title: String,
    pub page: Option<i32>,
    pub children: Vec<Outline>,
}

pub struct Camera {
    pub offset_x: f64,
    pub offset_y: f64,
    pub zoom: f64,
}

impl Default for Camera {
    fn default() -> Self {
        Self {
            offset_x: 0.0,
            offset_y: 0.0,
            zoom: 1.0,
        }
    }
}

pub struct TabState {
    pub id: TabId,
    pub title: String,
    pub file_path: String,
    pub doc: mupdf::Document,
    pub current_page: i32,
    pub page_count: i32,
    pub svg_dom: Option<skia_safe::svg::Dom>,
    pub page_width: f64,
    pub page_height: f64,
    pub camera: Camera,
    pub rail: RailNav,
    pub debug_overlay: bool,
    pub loading: bool,
    pub pending_page_load: Option<i32>,
    pub outline: Vec<Outline>,
    pub minimap_texture: Option<egui::TextureHandle>,
    pub minimap_dirty: bool,
    pub analysis_cache: HashMap<i32, PageAnalysis>,
    pub pending_analysis: VecDeque<i32>,
    /// True when analysis for the current page has been submitted to the worker
    /// but results haven't arrived yet. Rail mode activation is deferred.
    pub pending_rail_setup: bool,
    /// Cached GPU texture of the page content (white bg + SVG + colour effect)
    /// used during rail-mode scrolling to avoid re-rendering the SVG DOM every frame.
    pub rail_cache: Option<RailCache>,
}

/// Pre-rendered page texture for fast rail-mode scrolling.
pub struct RailCache {
    pub image: skia_safe::Image,
    pub zoom: f64,
    pub page: i32,
    pub effect_key: u8,
}

impl TabState {
    pub fn new(file_path: String, doc: mupdf::Document, config: &Config) -> anyhow::Result<Self> {
        let page_count = doc.page_count()?;
        let title = std::path::Path::new(&file_path)
            .file_name()
            .map(|n| n.to_string_lossy().to_string())
            .unwrap_or_else(|| file_path.clone());

        let outline = load_outline(&doc);

        Ok(Self {
            id: next_tab_id(),
            title,
            file_path,
            doc,
            current_page: 0,
            page_count,
            svg_dom: None,
            page_width: 0.0,
            page_height: 0.0,
            camera: Camera::default(),
            rail: RailNav::new(config.clone()),
            debug_overlay: false,
            loading: false,
            pending_page_load: None,
            outline,
            minimap_texture: None,
            minimap_dirty: true,
            analysis_cache: HashMap::new(),
            pending_analysis: VecDeque::new(),
            pending_rail_setup: false,
            rail_cache: None,
        })
    }

    pub fn load_page(
        &mut self,
        worker: &mut Option<AnalysisWorker>,
        navigable: &HashSet<usize>,
    ) {
        match crate::render_page_svg(&self.doc, self.current_page) {
            Ok((svg_string, w, h)) => {
                self.page_width = w;
                self.page_height = h;
                let font_mgr = skia_safe::FontMgr::default();
                self.svg_dom = skia_safe::svg::Dom::from_str(svg_string, font_mgr).ok();
            }
            Err(e) => {
                log::error!("Failed to render page {}: {}", self.current_page, e);
                self.svg_dom = None;
            }
        }

        self.submit_analysis(worker, navigable);
        self.minimap_dirty = true;
        self.rail_cache = None;
    }

    /// Submit analysis for the current page. Uses cache if available,
    /// otherwise prepares input on the main thread and sends to the worker.
    pub fn submit_analysis(
        &mut self,
        worker: &mut Option<AnalysisWorker>,
        navigable: &HashSet<usize>,
    ) {
        // Check cache first
        if let Some(cached) = self.analysis_cache.get(&self.current_page) {
            log::info!(
                "Using cached analysis for page {} ({} blocks)",
                self.current_page + 1,
                cached.blocks.len()
            );
            self.rail.set_analysis(cached.clone(), navigable);
            self.pending_rail_setup = false;
            return;
        }

        let worker = match worker {
            Some(w) => w,
            None => {
                log::info!("No ONNX model loaded, using fallback layout");
                let fallback = layout::fallback_analysis(self.page_width, self.page_height);
                self.analysis_cache
                    .insert(self.current_page, fallback.clone());
                self.rail.set_analysis(fallback, navigable);
                self.pending_rail_setup = false;
                return;
            }
        };

        // Already in flight — don't re-submit
        if worker.is_in_flight(self.current_page) {
            self.pending_rail_setup = true;
            return;
        }

        match layout::prepare_analysis_input(&self.doc, self.current_page) {
            Ok(input) => {
                let page = self.current_page;
                worker.submit(AnalysisRequest { page, input });
                self.pending_rail_setup = true;
                log::info!("Submitted analysis for page {} to worker", page + 1);
            }
            Err(e) => {
                log::warn!("Failed to prepare analysis input: {}, using fallback", e);
                let fallback = layout::fallback_analysis(self.page_width, self.page_height);
                self.analysis_cache
                    .insert(self.current_page, fallback.clone());
                self.rail.set_analysis(fallback, navigable);
                self.pending_rail_setup = false;
            }
        }
    }

    /// Re-apply navigable class filter from cached analysis (no ONNX re-run).
    pub fn reapply_navigable_classes(&mut self, navigable: &HashSet<usize>) {
        if let Some(cached) = self.analysis_cache.get(&self.current_page) {
            self.rail.set_analysis(cached.clone(), navigable);
        }
    }

    /// Queue lookahead pages for background analysis.
    pub fn queue_lookahead(&mut self, lookahead_count: usize) {
        self.pending_analysis.clear();
        for i in 1..=lookahead_count {
            let page = self.current_page + i as i32;
            if page < self.page_count && !self.analysis_cache.contains_key(&page) {
                self.pending_analysis.push_back(page);
            }
        }
    }

    /// Submit one pending lookahead page to the worker. Returns true if a request was submitted.
    pub fn submit_pending_lookahead(
        &mut self,
        worker: &mut Option<AnalysisWorker>,
    ) -> bool {
        let worker = match worker {
            Some(w) => w,
            None => return false,
        };

        // Only submit if the worker is idle (one at a time)
        if !worker.is_idle() {
            return false;
        }

        while let Some(page) = self.pending_analysis.pop_front() {
            // Skip if already cached or in flight
            if self.analysis_cache.contains_key(&page) || worker.is_in_flight(page) {
                continue;
            }

            match layout::prepare_analysis_input(&self.doc, page) {
                Ok(input) => {
                    worker.submit(AnalysisRequest { page, input });
                    log::info!("Submitted lookahead analysis for page {} to worker", page + 1);
                    return true;
                }
                Err(e) => {
                    log::warn!("Lookahead prepare failed for page {}: {}", page + 1, e);
                }
            }
        }

        false
    }

    pub fn update_rail_zoom(&mut self, window_width: f64, window_height: f64) {
        self.rail.update_zoom(
            self.camera.zoom,
            self.camera.offset_x,
            self.camera.offset_y,
            window_width,
            window_height,
        );
    }

    pub fn apply_zoom(&mut self, new_zoom: f64, window_width: f64, window_height: f64) {
        self.camera.zoom = new_zoom.clamp(ZOOM_MIN, ZOOM_MAX);
        self.update_rail_zoom(window_width, window_height);
        if self.rail.active {
            self.start_snap(window_width, window_height);
        }
        self.clamp_camera(window_width, window_height);
    }

    pub fn go_to_page(
        &mut self,
        page: i32,
        worker: &mut Option<AnalysisWorker>,
        navigable: &HashSet<usize>,
        window_width: f64,
        window_height: f64,
    ) {
        let page = page.clamp(0, self.page_count - 1);
        if page != self.current_page {
            self.current_page = page;
            let old_zoom = self.camera.zoom;
            self.load_page(worker, navigable);
            self.camera.zoom = old_zoom;
            self.clamp_camera(window_width, window_height);
        }
    }

    pub fn start_snap(&mut self, window_width: f64, window_height: f64) {
        self.rail.start_snap_to_current(
            self.camera.offset_x,
            self.camera.offset_y,
            self.camera.zoom,
            window_width,
            window_height,
        );
    }

    pub fn center_page(&mut self, window_width: f64, window_height: f64) {
        if self.page_width <= 0.0
            || self.page_height <= 0.0
            || window_width <= 0.0
            || window_height <= 0.0
        {
            return;
        }
        let scale_x = window_width / self.page_width;
        let scale_y = window_height / self.page_height;
        self.camera.zoom = scale_x.min(scale_y);
        let scaled_w = self.page_width * self.camera.zoom;
        let scaled_h = self.page_height * self.camera.zoom;
        self.camera.offset_x = (window_width - scaled_w) / 2.0;
        self.camera.offset_y = (window_height - scaled_h) / 2.0;
    }

    pub fn clamp_camera(&mut self, window_width: f64, window_height: f64) {
        let scaled_w = self.page_width * self.camera.zoom;
        let scaled_h = self.page_height * self.camera.zoom;

        if scaled_w <= window_width {
            self.camera.offset_x = (window_width - scaled_w) / 2.0;
        } else {
            let min_x = window_width - scaled_w;
            let max_x = 0.0;
            self.camera.offset_x = self.camera.offset_x.clamp(min_x, max_x);
        }

        if scaled_h <= window_height {
            self.camera.offset_y = (window_height - scaled_h) / 2.0;
        } else {
            let min_y = window_height - scaled_h;
            let max_y = 0.0;
            self.camera.offset_y = self.camera.offset_y.clamp(min_y, max_y);
        }
    }
}

fn load_outline(doc: &mupdf::Document) -> Vec<Outline> {
    match doc.outlines() {
        Ok(outlines) => convert_outlines(outlines),
        Err(e) => {
            log::warn!("Failed to load outlines: {}", e);
            Vec::new()
        }
    }
}

fn convert_outlines(outlines: Vec<mupdf::Outline>) -> Vec<Outline> {
    outlines
        .into_iter()
        .map(|o| Outline {
            title: o.title,
            page: o.dest.map(|d| d.loc.page_number as i32),
            children: convert_outlines(o.down),
        })
        .collect()
}
