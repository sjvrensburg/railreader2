use anyhow::Result;
use std::ffi::CString;
use std::num::NonZeroU32;

use gl::types::*;
use glutin::{
    config::{ConfigTemplateBuilder, GlConfig},
    context::{ContextApi, ContextAttributesBuilder, PossiblyCurrentContext},
    display::{GetGlDisplay, GlDisplay},
    prelude::{GlSurface, NotCurrentGlContext},
    surface::{Surface as GlutinSurface, SurfaceAttributesBuilder, WindowSurface},
};
use glutin_winit::DisplayBuilder;
use raw_window_handle::HasWindowHandle;
use skia_safe::{
    gpu::{self, backend_render_targets, gl::FramebufferInfo, SurfaceOrigin},
    Color, ColorType, Font, FontStyle, Paint, Rect, Surface,
};
use winit::{
    application::ApplicationHandler,
    dpi::LogicalSize,
    event::{ElementState, Modifiers, MouseButton, MouseScrollDelta, WindowEvent},
    event_loop::EventLoop,
    keyboard::{Key, NamedKey},
    window::Window,
};

use railreader2::cleanup;
use railreader2::colour_effect::ColourEffectState;
use railreader2::config::Config;
use railreader2::egui_integration::EguiIntegration;
use railreader2::layout::{self, LAYOUT_CLASSES};
use railreader2::rail::{NavResult, RailNav, ScrollDir};
use railreader2::tab::TabState;
use railreader2::ui::{self, UiAction, UiState};
use railreader2::worker::AnalysisWorker;

use railreader2::tab::{ZOOM_MAX, ZOOM_MIN};

const ZOOM_STEP: f64 = 1.25;
const SCROLL_PIXELS_PER_LINE: f64 = 30.0;
const ZOOM_SCROLL_SENSITIVITY: f64 = 0.003;
const PAN_STEP: f64 = 50.0;
const DEFAULT_WINDOW_WIDTH: f64 = 1200.0;
const DEFAULT_WINDOW_HEIGHT: f64 = 900.0;

enum Direction {
    Up,
    Down,
    Left,
    Right,
}

/// Ensures DirectContext drops before Window (prevents AMD GPU segfaults).
struct Env {
    surface: Surface,
    gl_surface: GlutinSurface<WindowSurface>,
    gr_context: gpu::DirectContext,
    gl_context: PossiblyCurrentContext,
    window: Window,
    fb_info: FramebufferInfo,
    num_samples: usize,
    stencil_size: usize,
}

impl Drop for Env {
    fn drop(&mut self) {
        self.gr_context.release_resources_and_abandon();
    }
}

struct App {
    env: Env,
    tabs: Vec<TabState>,
    active_tab: usize,
    worker: Option<AnalysisWorker>,
    config: Config,
    egui_integration: Option<EguiIntegration>,
    ui_state: UiState,
    colour_effect_state: ColourEffectState,
    modifiers: Modifiers,
    dragging: bool,
    press_pos: Option<(f64, f64)>,
    last_cursor: Option<(f64, f64)>,
    cursor_pos: (f64, f64),
    quit_requested: bool,
    last_frame: std::time::Instant,
    status_font: Font,
}

impl App {
    fn window_size(&self) -> (f64, f64) {
        let size = self.env.window.inner_size();
        (size.width as f64, size.height as f64)
    }

    fn active_tab(&self) -> Option<&TabState> {
        self.tabs.get(self.active_tab)
    }

    fn active_tab_mut(&mut self) -> Option<&mut TabState> {
        self.tabs.get_mut(self.active_tab)
    }

    fn open_document(&mut self, path: String) {
        let doc = match mupdf::Document::open(&path) {
            Ok(d) => d,
            Err(e) => {
                log::error!("Failed to open {}: {}", path, e);
                return;
            }
        };

        let mut tab = match TabState::new(path, doc, &self.config) {
            Ok(t) => t,
            Err(e) => {
                log::error!("Failed to create tab: {}", e);
                return;
            }
        };

        tab.load_page(&mut self.worker, &self.config.navigable_classes);
        let (ww, wh) = self.window_size();
        tab.center_page(ww, wh);
        tab.update_rail_zoom(ww, wh);

        if let Some(egui_int) = &self.egui_integration {
            update_minimap_texture(&mut tab, &egui_int.ctx);
        }

        let lookahead = self.config.analysis_lookahead_pages;
        tab.queue_lookahead(lookahead);

        self.tabs.push(tab);
        self.active_tab = self.tabs.len() - 1;
    }

    fn close_tab(&mut self, index: usize) {
        if index >= self.tabs.len() {
            return;
        }
        self.tabs.remove(index);
        if self.tabs.is_empty() {
            self.active_tab = 0;
        } else if self.active_tab >= self.tabs.len() {
            self.active_tab = self.tabs.len() - 1;
        }
    }

    fn select_tab(&mut self, index: usize) {
        if index < self.tabs.len() {
            self.active_tab = index;
        }
    }

    fn duplicate_tab(&mut self) {
        if let Some(tab) = self.active_tab() {
            let path = tab.file_path.clone();
            self.open_document(path);
        }
    }

    fn process_actions(
        &mut self,
        actions: Vec<UiAction>,
        event_loop: Option<&winit::event_loop::ActiveEventLoop>,
    ) {
        for action in actions {
            match action {
                UiAction::OpenFile => {
                    if let Some(path) = rfd::FileDialog::new()
                        .add_filter("PDF", &["pdf"])
                        .set_parent(&self.env.window)
                        .pick_file()
                    {
                        self.open_document(path.to_string_lossy().to_string());
                    }
                }
                UiAction::CloseTab(idx) => self.close_tab(idx),
                UiAction::SelectTab(idx) => self.select_tab(idx),
                UiAction::DuplicateTab => self.duplicate_tab(),
                UiAction::GoToPage(page) => {
                    let (ww, wh) = self.window_size();
                    let idx = self.active_tab;
                    if idx < self.tabs.len() {
                        self.tabs[idx].go_to_page(
                            page,
                            &mut self.worker,
                            &self.config.navigable_classes,
                            ww,
                            wh,
                        );
                        self.tabs[idx].minimap_dirty = true;
                        self.tabs[idx].queue_lookahead(self.config.analysis_lookahead_pages);
                    }
                    self.update_minimap_for_active_tab();
                }
                UiAction::SetZoom(zoom) => {
                    let (ww, wh) = self.window_size();
                    if let Some(tab) = self.active_tab_mut() {
                        tab.apply_zoom(zoom, ww, wh);
                    }
                }
                UiAction::FitPage => {
                    let (ww, wh) = self.window_size();
                    if let Some(tab) = self.active_tab_mut() {
                        tab.center_page(ww, wh);
                        tab.update_rail_zoom(ww, wh);
                    }
                }
                UiAction::ToggleDebug => {
                    if let Some(tab) = self.active_tab_mut() {
                        tab.debug_overlay = !tab.debug_overlay;
                    }
                }
                UiAction::ToggleOutline => {
                    self.ui_state.show_outline = !self.ui_state.show_outline;
                }
                UiAction::ToggleMinimap => {
                    self.ui_state.show_minimap = !self.ui_state.show_minimap;
                }
                UiAction::SetCamera(ox, oy) => {
                    let (ww, wh) = self.window_size();
                    if let Some(tab) = self.active_tab_mut() {
                        tab.camera.offset_x = ox;
                        tab.camera.offset_y = oy;
                        tab.clamp_camera(ww, wh);
                        if tab.rail.active {
                            tab.rail.find_nearest_block(
                                tab.camera.offset_x,
                                tab.camera.offset_y,
                                tab.camera.zoom,
                                ww,
                                wh,
                            );
                            tab.start_snap(ww, wh);
                        }
                    }
                    self.env.window.request_redraw();
                }
                UiAction::SetColourEffect(effect) => {
                    self.config.colour_effect = effect;
                    self.config.save();
                    self.colour_effect_state.effect = effect;
                    self.env.window.request_redraw();
                }
                UiAction::ConfigChanged => {
                    self.colour_effect_state.effect = self.config.colour_effect;
                    self.colour_effect_state.intensity = self.config.colour_effect_intensity as f32;
                    if let Some(egui_int) = &self.egui_integration {
                        egui_int.set_font_scale(self.config.ui_font_scale);
                    }
                    for tab in &mut self.tabs {
                        tab.rail.update_config(self.config.clone());
                        tab.reapply_navigable_classes(&self.config.navigable_classes);
                    }
                }
                UiAction::RunCleanup => {
                    let report = cleanup::run_cleanup();
                    self.ui_state.cleanup_message = Some(report.to_string());
                }
                UiAction::Quit => {
                    if let Some(el) = event_loop {
                        el.exit();
                    } else {
                        self.quit_requested = true;
                    }
                }
            }
        }
    }

    fn update_minimap_for_active_tab(&mut self) {
        let active = self.active_tab;
        if let (Some(egui_int), Some(tab)) = (&self.egui_integration, self.tabs.get_mut(active)) {
            if tab.minimap_dirty {
                update_minimap_texture(tab, &egui_int.ctx);
            }
        }
    }
}

fn create_surface(
    window: &Window,
    fb_info: FramebufferInfo,
    gr_context: &mut gpu::DirectContext,
    num_samples: usize,
    stencil_size: usize,
) -> Surface {
    let size = window.inner_size();
    let size = (
        size.width.try_into().expect("Could not convert width"),
        size.height.try_into().expect("Could not convert height"),
    );
    let backend_render_target =
        backend_render_targets::make_gl(size, num_samples, stencil_size, fb_info);

    gpu::surfaces::wrap_backend_render_target(
        gr_context,
        &backend_render_target,
        SurfaceOrigin::BottomLeft,
        ColorType::RGBA8888,
        None,
        None,
    )
    .expect("Could not create skia surface")
}

fn draw_rail_overlays(
    canvas: &skia_safe::Canvas,
    rail: &RailNav,
    page_width: f64,
    page_height: f64,
    debug_overlay: bool,
    palette: &railreader2::colour_effect::OverlayPalette,
) {
    if !rail.active || !rail.has_analysis() {
        if debug_overlay {
            draw_debug_overlay(canvas, rail);
        }
        return;
    }

    let block = rail.current_navigable_block();
    let margin = 4.0f32;
    let block_rect = Rect::from_xywh(
        block.bbox.x - margin,
        block.bbox.y - margin,
        block.bbox.w + margin * 2.0,
        block.bbox.h + margin * 2.0,
    );

    // Dim the page (optionally excluding the active block so it stays at full brightness)
    let page_rect = Rect::from_wh(page_width as f32, page_height as f32);
    let mut dim_paint = Paint::default();
    dim_paint.set_color(palette.dim);
    if palette.dim_excludes_block {
        canvas.save();
        canvas.clip_rect(block_rect, skia_safe::ClipOp::Difference, false);
        canvas.draw_rect(page_rect, &dim_paint);
        canvas.restore();
    } else {
        canvas.draw_rect(page_rect, &dim_paint);
    }

    // Reveal the active block (skipped for dark-background effects or exclude-dim mode)
    if !palette.dim_excludes_block {
        if let Some((color, blend_mode)) = palette.block_reveal {
            canvas.save();
            canvas.clip_rect(block_rect, skia_safe::ClipOp::Intersect, false);
            let mut clear_paint = Paint::default();
            clear_paint.set_color(color);
            clear_paint.set_blend_mode(blend_mode);
            canvas.draw_rect(block_rect, &clear_paint);
            canvas.restore();
        }
    }

    // Block outline
    let mut outline_paint = Paint::default();
    outline_paint.set_color(palette.block_outline);
    outline_paint.set_style(skia_safe::paint::Style::Stroke);
    outline_paint.set_stroke_width(palette.block_outline_width);
    outline_paint.set_anti_alias(true);
    canvas.draw_rect(
        Rect::from_xywh(block.bbox.x, block.bbox.y, block.bbox.w, block.bbox.h),
        &outline_paint,
    );

    // Current line highlight
    let line = rail.current_line_info();
    let mut line_paint = Paint::default();
    line_paint.set_color(palette.line_highlight);
    canvas.draw_rect(
        Rect::from_xywh(
            block.bbox.x,
            line.y - line.height / 2.0,
            block.bbox.w,
            line.height,
        ),
        &line_paint,
    );

    if debug_overlay {
        draw_debug_overlay(canvas, rail);
    }
}

fn draw_debug_overlay(canvas: &skia_safe::Canvas, rail: &RailNav) {
    let Some(analysis) = rail.analysis() else {
        return;
    };

    let colors = [
        (244, 67, 54),
        (33, 150, 243),
        (76, 175, 80),
        (255, 152, 0),
        (156, 39, 176),
        (0, 188, 212),
    ];

    let font_mgr = skia_safe::FontMgr::default();
    let typeface = font_mgr
        .match_family_style("monospace", FontStyle::default())
        .or_else(|| font_mgr.match_family_style("DejaVu Sans Mono", FontStyle::default()));
    let debug_font = Font::from_typeface(
        typeface.unwrap_or_else(|| {
            font_mgr
                .legacy_make_typeface(None, FontStyle::default())
                .unwrap()
        }),
        8.0,
    );

    for block in &analysis.blocks {
        let color = colors[block.class_id % colors.len()];

        let mut rect_paint = Paint::default();
        rect_paint.set_color(Color::from_argb(50, color.0, color.1, color.2));
        canvas.draw_rect(
            Rect::from_xywh(block.bbox.x, block.bbox.y, block.bbox.w, block.bbox.h),
            &rect_paint,
        );

        let mut stroke_paint = Paint::default();
        stroke_paint.set_color(Color::from_argb(180, color.0, color.1, color.2));
        stroke_paint.set_style(skia_safe::paint::Style::Stroke);
        stroke_paint.set_stroke_width(1.0);
        canvas.draw_rect(
            Rect::from_xywh(block.bbox.x, block.bbox.y, block.bbox.w, block.bbox.h),
            &stroke_paint,
        );

        let class_name = if block.class_id < LAYOUT_CLASSES.len() {
            LAYOUT_CLASSES[block.class_id]
        } else {
            "unknown"
        };
        let label = format!(
            "#{} {} ({:.0}%)",
            block.order,
            class_name,
            block.confidence * 100.0
        );

        let mut bg_paint = Paint::default();
        bg_paint.set_color(Color::from_argb(200, 0, 0, 0));
        canvas.draw_rect(
            Rect::from_xywh(
                block.bbox.x,
                block.bbox.y - 10.0,
                label.len() as f32 * 5.0,
                11.0,
            ),
            &bg_paint,
        );

        let mut text_paint = Paint::default();
        text_paint.set_color(Color::from_argb(255, color.0, color.1, color.2));
        text_paint.set_anti_alias(true);
        canvas.draw_str(
            &label,
            (block.bbox.x + 1.0, block.bbox.y - 1.0),
            &debug_font,
            &text_paint,
        );
    }
}

impl App {
    fn handle_resize(&mut self, physical_size: winit::dpi::PhysicalSize<u32>) {
        self.env.surface = create_surface(
            &self.env.window,
            self.env.fb_info,
            &mut self.env.gr_context,
            self.env.num_samples,
            self.env.stencil_size,
        );
        let (width, height): (u32, u32) = physical_size.into();
        self.env.gl_surface.resize(
            &self.env.gl_context,
            NonZeroU32::new(width.max(1)).unwrap(),
            NonZeroU32::new(height.max(1)).unwrap(),
        );
        let (ww, wh) = self.window_size();
        if let Some(tab) = self.active_tab_mut() {
            tab.clamp_camera(ww, wh);
        }
        self.env.window.request_redraw();
    }

    fn handle_mouse_wheel(&mut self, delta: MouseScrollDelta) {
        let scroll_y = match delta {
            MouseScrollDelta::LineDelta(_, y) => y as f64 * SCROLL_PIXELS_PER_LINE,
            MouseScrollDelta::PixelDelta(pos) => pos.y,
        };

        let idx = self.active_tab;
        if idx >= self.tabs.len() {
            return;
        }

        let ctrl_held = self.modifiers.state().control_key();
        let (ww, wh) = self.window_size();
        let cursor_pos = self.cursor_pos;

        let tab = &mut self.tabs[idx];
        if ctrl_held && tab.rail.active {
            let step = scroll_y * 2.0 * tab.camera.zoom;
            tab.camera.offset_x += step;
            tab.clamp_camera(ww, wh);
        } else {
            let old_zoom = tab.camera.zoom;
            let factor = 1.0 + scroll_y * ZOOM_SCROLL_SENSITIVITY;
            let new_zoom = (old_zoom * factor).clamp(ZOOM_MIN, ZOOM_MAX);

            let (cx, cy) = cursor_pos;
            tab.camera.offset_x = cx - (cx - tab.camera.offset_x) * (new_zoom / old_zoom);
            tab.camera.offset_y = cy - (cy - tab.camera.offset_y) * (new_zoom / old_zoom);
            tab.camera.zoom = new_zoom;

            tab.update_rail_zoom(ww, wh);
            if tab.rail.active {
                tab.start_snap(ww, wh);
            }
            tab.clamp_camera(ww, wh);
        }
        self.env.window.request_redraw();
    }

    fn handle_mouse_input(&mut self, state: ElementState, button: MouseButton) {
        if button == MouseButton::Left {
            if state == ElementState::Pressed {
                self.dragging = true;
                self.press_pos = Some(self.cursor_pos);
            } else {
                // On release, check if this was a click (not a drag)
                if let Some((px, py)) = self.press_pos {
                    let (cx, cy) = self.cursor_pos;
                    let dist = ((cx - px).powi(2) + (cy - py).powi(2)).sqrt();
                    if dist < 5.0 {
                        self.handle_click(cx, cy);
                    }
                }
                self.dragging = false;
                self.press_pos = None;
                self.last_cursor = None;
            }
        }
    }

    fn handle_click(&mut self, cursor_x: f64, cursor_y: f64) {
        let content_rect = self.ui_state.content_rect;
        let (ww, wh) = self.window_size();

        let Some(tab) = self.active_tab_mut() else {
            return;
        };

        if !tab.rail.active || !tab.rail.has_analysis() {
            return;
        }

        let page_x =
            (cursor_x - content_rect.min.x as f64 - tab.camera.offset_x) / tab.camera.zoom;
        let page_y =
            (cursor_y - content_rect.min.y as f64 - tab.camera.offset_y) / tab.camera.zoom;

        if let Some(nav_idx) = tab.rail.find_block_at_point(page_x, page_y) {
            tab.rail.current_block = nav_idx;
            tab.rail.current_line = 0;
            tab.start_snap(ww, wh);
            self.env.window.request_redraw();
        }
    }

    fn handle_cursor_moved(&mut self, position: winit::dpi::PhysicalPosition<f64>) {
        self.cursor_pos = (position.x, position.y);
        if self.dragging {
            if let Some((lx, ly)) = self.last_cursor {
                let dx = position.x - lx;
                let dy = position.y - ly;

                let (ww, wh) = self.window_size();
                if let Some(tab) = self.active_tab_mut() {
                    tab.camera.offset_x += dx;
                    tab.camera.offset_y += dy;
                    tab.clamp_camera(ww, wh);
                }
                self.env.window.request_redraw();
            }
            self.last_cursor = Some((position.x, position.y));
        }
    }

    fn handle_key_release(&mut self, event: &winit::event::KeyEvent) {
        let direction = key_to_direction(&event.logical_key);
        if let Some(Direction::Left | Direction::Right) = direction {
            if let Some(tab) = self.active_tab_mut() {
                tab.rail.stop_scroll();
            }
            self.env.window.request_redraw();
        }
    }

    fn handle_key_press(
        &mut self,
        event: &winit::event::KeyEvent,
        event_loop: &winit::event_loop::ActiveEventLoop,
    ) {
        let ctrl = self.modifiers.state().control_key();

        // Ctrl shortcuts
        if ctrl {
            match &event.logical_key {
                Key::Character(c) if c.as_str() == "o" => {
                    self.process_actions(vec![UiAction::OpenFile], Some(event_loop));
                    return;
                }
                Key::Character(c) if c.as_str() == "w" => {
                    let idx = self.active_tab;
                    self.process_actions(vec![UiAction::CloseTab(idx)], Some(event_loop));
                    return;
                }
                Key::Character(c) if c.as_str() == "q" => {
                    event_loop.exit();
                    return;
                }
                Key::Named(NamedKey::Tab) => {
                    // Ctrl+Tab: next tab
                    if !self.tabs.is_empty() {
                        let next = (self.active_tab + 1) % self.tabs.len();
                        self.select_tab(next);
                        self.env.window.request_redraw();
                    }
                    return;
                }
                _ => {}
            }
        }

        let (ww, wh) = self.window_size();
        let idx = self.active_tab;

        if let Some(dir) = key_to_direction(&event.logical_key) {
            if idx >= self.tabs.len() {
                return;
            }
            let lookahead = self.config.analysis_lookahead_pages;
            let navigable = &self.config.navigable_classes;
            let ort = &mut self.worker;
            let tab = &mut self.tabs[idx];
            match dir {
                Direction::Down => {
                    if tab.rail.active {
                        let current_page = tab.current_page;
                        match tab.rail.next_line() {
                            NavResult::PageBoundaryNext => {
                                tab.go_to_page(current_page + 1, ort, navigable, ww, wh);
                                tab.queue_lookahead(lookahead);
                                if tab.rail.active {
                                    tab.start_snap(ww, wh);
                                }
                            }
                            NavResult::Ok => tab.start_snap(ww, wh),
                            _ => {}
                        }
                    } else {
                        tab.camera.offset_y -= PAN_STEP;
                        tab.clamp_camera(ww, wh);
                    }
                }
                Direction::Up => {
                    if tab.rail.active {
                        let current_page = tab.current_page;
                        match tab.rail.prev_line() {
                            NavResult::PageBoundaryPrev => {
                                tab.go_to_page(current_page - 1, ort, navigable, ww, wh);
                                tab.queue_lookahead(lookahead);
                                if tab.rail.active {
                                    tab.rail.jump_to_end();
                                    tab.start_snap(ww, wh);
                                }
                            }
                            NavResult::Ok => tab.start_snap(ww, wh),
                            _ => {}
                        }
                    } else {
                        tab.camera.offset_y += PAN_STEP;
                        tab.clamp_camera(ww, wh);
                    }
                }
                Direction::Right => {
                    if tab.rail.active {
                        tab.rail.start_scroll(ScrollDir::Forward);
                    } else {
                        tab.camera.offset_x -= PAN_STEP;
                        tab.clamp_camera(ww, wh);
                    }
                }
                Direction::Left => {
                    if tab.rail.active {
                        tab.rail.start_scroll(ScrollDir::Backward);
                    } else {
                        tab.camera.offset_x += PAN_STEP;
                        tab.clamp_camera(ww, wh);
                    }
                }
            }
            self.env.window.request_redraw();
            return;
        }

        // Page navigation keys
        let nav_page = match &event.logical_key {
            Key::Named(NamedKey::PageDown) if idx < self.tabs.len() => {
                Some(self.tabs[idx].current_page + 1)
            }
            Key::Named(NamedKey::PageUp) if idx < self.tabs.len() => {
                Some(self.tabs[idx].current_page - 1)
            }
            Key::Named(NamedKey::Home) if idx < self.tabs.len() => Some(0),
            Key::Named(NamedKey::End) if idx < self.tabs.len() => {
                Some(self.tabs[idx].page_count - 1)
            }
            _ => None,
        };
        if let Some(page) = nav_page {
            self.tabs[idx].go_to_page(
                page,
                &mut self.worker,
                &self.config.navigable_classes,
                ww,
                wh,
            );
            self.tabs[idx].queue_lookahead(self.config.analysis_lookahead_pages);
            self.env.window.request_redraw();
            return;
        }

        match &event.logical_key {
            Key::Character(c) if c.as_str() == "+" || c.as_str() == "=" => {
                if let Some(tab) = self.active_tab_mut() {
                    let new_zoom = tab.camera.zoom * ZOOM_STEP;
                    tab.apply_zoom(new_zoom, ww, wh);
                }
                self.env.window.request_redraw();
            }
            Key::Character(c) if c.as_str() == "-" => {
                if let Some(tab) = self.active_tab_mut() {
                    let new_zoom = tab.camera.zoom / ZOOM_STEP;
                    tab.apply_zoom(new_zoom, ww, wh);
                }
                self.env.window.request_redraw();
            }
            Key::Character(c) if c.as_str() == "0" => {
                if let Some(tab) = self.active_tab_mut() {
                    tab.center_page(ww, wh);
                    tab.update_rail_zoom(ww, wh);
                }
                self.env.window.request_redraw();
            }
            Key::Character(c) if c.as_str() == "D" => {
                if let Some(tab) = self.active_tab_mut() {
                    tab.debug_overlay = !tab.debug_overlay;
                    log::info!("Debug overlay: {}", tab.debug_overlay);
                }
                self.env.window.request_redraw();
            }
            Key::Named(NamedKey::F1) => {
                self.ui_state.show_shortcuts = !self.ui_state.show_shortcuts;
                self.env.window.request_redraw();
            }
            Key::Named(NamedKey::Escape) => event_loop.exit(),
            _ => {}
        }
    }

    fn handle_redraw(&mut self) {
        let size = self.env.window.inner_size();
        if size.width == 0 || size.height == 0 {
            return;
        }

        let now = std::time::Instant::now();
        let dt_secs = now.duration_since(self.last_frame).as_secs_f64().min(0.1);
        self.last_frame = now;

        // Advance animations for active tab
        let animating = if let Some(tab) = self.active_tab_mut() {
            tab.rail.tick(
                &mut tab.camera.offset_x,
                &mut tab.camera.offset_y,
                dt_secs,
                tab.camera.zoom,
                size.width as f64,
            )
        } else {
            false
        };

        // Defer minimap rendering to non-animating frames to avoid stacking
        // with other expensive operations
        if !animating {
            self.update_minimap_for_active_tab();
        }

        // --- Phase 2: Reordered rendering pipeline ---
        // 1. Reset Skia's cached GL state
        self.env.gr_context.reset(None);

        // egui: begin_frame, build_ui, end_frame
        // The Arc-clone of egui::Context releases the mutable borrow on
        // self.egui_integration so build_ui can borrow other App fields.
        let ctx = self
            .egui_integration
            .as_mut()
            .map(|e| e.begin_frame(&self.env.window).clone());

        let actions = if let Some(ctx) = &ctx {
            ui::build_ui(
                ctx,
                &mut self.ui_state,
                &self.tabs,
                self.active_tab,
                &mut self.config,
            )
        } else {
            self.ui_state.content_rect = egui::Rect::from_min_size(
                egui::pos2(0.0, 0.0),
                egui::vec2(size.width as f32, size.height as f32),
            );
            Vec::new()
        };

        if let Some(egui_int) = &mut self.egui_integration {
            egui_int.end_frame(&self.env.window);
        }

        // 5. Skia renders PDF into content_rect area
        let content_rect = self.ui_state.content_rect;

        // Pre-render rail cache if needed (before borrowing main canvas)
        let effect_key = self.colour_effect_state.effect as u8;
        if let Some(tab) = self.tabs.get_mut(self.active_tab) {
            let use_rail_cache = tab.rail.active && tab.svg_dom.is_some();
            if use_rail_cache {
                let cache_valid = tab.rail_cache.as_ref().is_some_and(|c| {
                    c.page == tab.current_page
                        && (c.zoom - tab.camera.zoom).abs() < 1e-6
                        && c.effect_key == effect_key
                });
                if !cache_valid {
                    let pw = (tab.page_width * tab.camera.zoom).ceil() as i32;
                    let ph = (tab.page_height * tab.camera.zoom).ceil() as i32;
                    if pw > 0 && ph > 0 {
                        let image_info = skia_safe::ImageInfo::new(
                            (pw, ph),
                            ColorType::RGBA8888,
                            skia_safe::AlphaType::Premul,
                            None,
                        );
                        if let Some(ref mut off) =
                            self.env.surface.new_surface(&image_info)
                        {
                            let oc = off.canvas();
                            oc.scale((tab.camera.zoom as f32, tab.camera.zoom as f32));

                            let eff_layer =
                                if let Some(paint) = self.colour_effect_state.create_paint() {
                                    oc.save_layer(
                                        &skia_safe::canvas::SaveLayerRec::default().paint(&paint),
                                    );
                                    true
                                } else {
                                    false
                                };

                            let mut white_paint = Paint::default();
                            white_paint.set_color(Color::WHITE);
                            oc.draw_rect(
                                Rect::from_wh(tab.page_width as f32, tab.page_height as f32),
                                &white_paint,
                            );

                            if let Some(dom) = &mut tab.svg_dom {
                                dom.render(oc);
                            }

                            if eff_layer {
                                oc.restore();
                            }

                            let image = off.image_snapshot();
                            tab.rail_cache = Some(railreader2::tab::RailCache {
                                image,
                                zoom: tab.camera.zoom,
                                page: tab.current_page,
                                effect_key,
                            });
                        }
                    }
                }
            }
        }

        // Now draw to the main canvas
        let canvas = self.env.surface.canvas();
        canvas.clear(Color::from_argb(255, 128, 128, 128));

        if let Some(tab) = self.tabs.get_mut(self.active_tab) {
            let scale = self.env.window.scale_factor() as f32;
            let scissor_x = (content_rect.min.x * scale) as i32;
            let scissor_y = (size.height as f32 - content_rect.max.y * scale) as i32;
            let scissor_w = (content_rect.width() * scale) as i32;
            let scissor_h = (content_rect.height() * scale) as i32;
            unsafe {
                gl::Enable(gl::SCISSOR_TEST);
                gl::Scissor(scissor_x, scissor_y, scissor_w, scissor_h);
            }

            let use_rail_cache = tab.rail.active && tab.rail_cache.is_some();

            canvas.save();
            canvas.translate((content_rect.min.x, content_rect.min.y));
            canvas.translate((tab.camera.offset_x as f32, tab.camera.offset_y as f32));

            if use_rail_cache {
                // Fast path: blit cached texture (zoom is baked into the image)
                let image = &tab.rail_cache.as_ref().unwrap().image;
                canvas.draw_image(image, (0.0, 0.0), None);
            } else {
                // Normal path: full SVG render
                canvas.scale((tab.camera.zoom as f32, tab.camera.zoom as f32));

                let effect_layer =
                    if let Some(paint) = self.colour_effect_state.create_paint() {
                        canvas.save_layer(
                            &skia_safe::canvas::SaveLayerRec::default().paint(&paint),
                        );
                        true
                    } else {
                        false
                    };

                let mut white_paint = Paint::default();
                white_paint.set_color(Color::WHITE);
                canvas.draw_rect(
                    Rect::from_wh(tab.page_width as f32, tab.page_height as f32),
                    &white_paint,
                );

                if let Some(dom) = &mut tab.svg_dom {
                    dom.render(canvas);
                }

                if effect_layer {
                    canvas.restore();
                }
            }

            // Rail overlays in page coordinate space — apply zoom in cached path
            if use_rail_cache {
                canvas.scale((tab.camera.zoom as f32, tab.camera.zoom as f32));
            }

            let palette = self.colour_effect_state.effect.overlay_palette();
            draw_rail_overlays(
                canvas,
                &tab.rail,
                tab.page_width,
                tab.page_height,
                tab.debug_overlay,
                &palette,
            );

            canvas.restore();

            unsafe {
                gl::Disable(gl::SCISSOR_TEST);
            }
        } else {
            draw_welcome(canvas, size.width, size.height, &self.status_font);
        }

        // 6. Flush Skia
        self.env.gr_context.flush_and_submit();

        // 7. egui paint (overlays panels/menus on top)
        if let Some(egui_int) = &mut self.egui_integration {
            egui_int.paint(&self.env.window);
        }

        // 8. Swap buffers
        self.env
            .gl_surface
            .swap_buffers(&self.env.gl_context)
            .unwrap();

        // Process UI actions
        self.process_actions(actions, None);

        // Poll for completed analysis results from the worker
        let mut got_results = false;
        let (ww, wh) = self.window_size();
        if let Some(worker) = &mut self.worker {
            while let Some(result) = worker.poll() {
                got_results = true;
                log::info!(
                    "Received analysis result for page {} ({} blocks)",
                    result.page + 1,
                    result.analysis.blocks.len()
                );
                for tab in &mut self.tabs {
                    tab.analysis_cache
                        .insert(result.page, result.analysis.clone());
                    if tab.current_page == result.page && tab.pending_rail_setup {
                        tab.rail
                            .set_analysis(result.analysis.clone(), &self.config.navigable_classes);
                        tab.pending_rail_setup = false;
                        if tab.rail.active {
                            tab.start_snap(ww, wh);
                        }
                    }
                }
            }
        }

        // Submit one pending lookahead per frame (non-blocking)
        let has_pending = if !animating {
            let idx = self.active_tab;
            if idx < self.tabs.len() {
                self.tabs[idx].submit_pending_lookahead(&mut self.worker)
            } else {
                false
            }
        } else {
            false
        };

        // Track if worker has in-flight requests (need to keep polling)
        let worker_busy = self.worker.as_ref().is_some_and(|w| !w.is_idle());

        // Request another frame if animating or worker has pending work
        let egui_wants_repaint = self
            .egui_integration
            .as_ref()
            .is_some_and(|e| e.wants_continuous_repaint());
        if animating || egui_wants_repaint || has_pending || worker_busy || got_results {
            self.env.window.request_redraw();
        }
    }
}

fn draw_welcome(canvas: &skia_safe::Canvas, width: u32, height: u32, font: &Font) {
    let mut paint = Paint::default();
    paint.set_color(Color::WHITE);
    paint.set_anti_alias(true);

    let text = "Open a PDF file (Ctrl+O)";
    canvas.draw_str(
        text,
        (width as f32 / 2.0 - 80.0, height as f32 / 2.0),
        font,
        &paint,
    );
}

fn update_minimap_texture(tab: &mut TabState, ctx: &egui::Context) {
    match railreader2::render_page_pixmap(&tab.doc, tab.current_page, 200) {
        Ok((rgb_bytes, px_w, px_h, _, _)) => {
            let pixels: Vec<egui::Color32> = rgb_bytes
                .chunks_exact(3)
                .map(|c| egui::Color32::from_rgb(c[0], c[1], c[2]))
                .collect();
            let image = egui::ColorImage {
                size: [px_w as usize, px_h as usize],
                pixels,
            };
            tab.minimap_texture = Some(ctx.load_texture(
                format!("minimap_{}_{}", tab.file_path, tab.current_page),
                image,
                egui::TextureOptions::LINEAR,
            ));
            tab.minimap_dirty = false;
        }
        Err(e) => {
            log::warn!("Failed to render minimap: {}", e);
        }
    }
}

fn key_to_direction(key: &Key) -> Option<Direction> {
    match key {
        Key::Named(NamedKey::ArrowDown) => Some(Direction::Down),
        Key::Named(NamedKey::ArrowUp) => Some(Direction::Up),
        Key::Named(NamedKey::ArrowRight) => Some(Direction::Right),
        Key::Named(NamedKey::ArrowLeft) => Some(Direction::Left),
        Key::Character(s) => match s.as_str() {
            "s" => Some(Direction::Down),
            "w" => Some(Direction::Up),
            "d" => Some(Direction::Right),
            "a" => Some(Direction::Left),
            _ => None,
        },
        _ => None,
    }
}

impl ApplicationHandler for App {
    fn resumed(&mut self, _event_loop: &winit::event_loop::ActiveEventLoop) {}

    fn window_event(
        &mut self,
        event_loop: &winit::event_loop::ActiveEventLoop,
        _window_id: winit::window::WindowId,
        event: WindowEvent,
    ) {
        if self.quit_requested {
            event_loop.exit();
            return;
        }

        if let WindowEvent::ModifiersChanged(mods) = &event {
            self.modifiers = *mods;
        }

        // Handle pending page loads (deferred loading for spinner visibility)
        let idx = self.active_tab;
        if idx < self.tabs.len() {
            if let Some(page) = self.tabs[idx].pending_page_load.take() {
                let (ww, wh) = self.window_size();
                self.tabs[idx].go_to_page(
                    page,
                    &mut self.worker,
                    &self.config.navigable_classes,
                    ww,
                    wh,
                );
                self.env.window.request_redraw();
                return;
            }
        }

        // Pass all events (except RedrawRequested) to egui first
        let egui_response = if !matches!(event, WindowEvent::RedrawRequested) {
            self.egui_integration
                .as_mut()
                .map(|e| e.handle_event(&self.env.window, &event))
        } else {
            None
        };
        let egui_consumed = egui_response.as_ref().is_some_and(|r| r.consumed);
        let egui_repaint = egui_response.as_ref().is_some_and(|r| r.repaint);

        match event {
            WindowEvent::CloseRequested => event_loop.exit(),
            WindowEvent::ModifiersChanged(_) => {}
            WindowEvent::Resized(physical_size) => {
                self.handle_resize(physical_size);
            }
            WindowEvent::MouseWheel { delta, .. } => {
                if !egui_consumed {
                    self.handle_mouse_wheel(delta);
                }
            }
            WindowEvent::MouseInput { state, button, .. } => {
                if !egui_consumed {
                    self.handle_mouse_input(state, button);
                }
            }
            WindowEvent::CursorMoved { position, .. } => {
                self.handle_cursor_moved(position);
            }
            WindowEvent::KeyboardInput { ref event, .. } => {
                let consumed = egui_consumed
                    || self
                        .egui_integration
                        .as_ref()
                        .is_some_and(|e| e.ctx.wants_keyboard_input());
                if !consumed {
                    if event.state == ElementState::Released {
                        self.handle_key_release(event);
                    } else {
                        self.handle_key_press(event, event_loop);
                    }
                }
            }
            WindowEvent::RedrawRequested => self.handle_redraw(),
            _ => {}
        }

        // Request redraw when egui needs it (e.g., after mouse clicks on menus)
        if egui_repaint {
            self.env.window.request_redraw();
        }
    }
}

const MODEL_FILENAME: &str = "PP-DocLayoutV3.onnx";

/// Search for the ONNX model at runtime in order of priority:
/// 1. `models/` next to the executable (packaged/portable installs)
/// 2. `$APPDIR/models/` (AppImage)
/// 3. `$XDG_DATA_HOME/railreader2/models/` or `%APPDATA%\railreader2\models\` (user data dir)
/// 4. `models/` in the current working directory (development)
fn find_model_path() -> Option<std::path::PathBuf> {
    let candidates: Vec<std::path::PathBuf> = [
        // Next to executable
        std::env::current_exe()
            .ok()
            .and_then(|p| p.parent().map(|d| d.join("models").join(MODEL_FILENAME))),
        // AppImage: $APPDIR/models/
        std::env::var_os("APPDIR")
            .map(|d| std::path::PathBuf::from(d).join("models").join(MODEL_FILENAME)),
        // Platform user data directory
        dirs::data_dir().map(|d| d.join("railreader2").join("models").join(MODEL_FILENAME)),
        // Current working directory (dev builds)
        Some(std::path::PathBuf::from("models").join(MODEL_FILENAME)),
    ]
    .into_iter()
    .flatten()
    .collect();

    for path in &candidates {
        log::debug!("Checking for model at {}", path.display());
        if path.exists() {
            return Some(path.clone());
        }
    }

    log::info!(
        "Model not found in any of: {:?}",
        candidates.iter().map(|p| p.display().to_string()).collect::<Vec<_>>()
    );
    None
}

fn main() -> Result<()> {
    env_logger::init();

    let args: Vec<String> = std::env::args().collect();
    let pdf_path = args.get(1).cloned();

    // Try to load ONNX model
    let worker = if let Some(model_path) = find_model_path() {
        match layout::load_model(model_path.to_str().unwrap()) {
            Ok(session) => {
                log::info!("Loaded ONNX model from {}", model_path.display());
                Some(AnalysisWorker::new(session))
            }
            Err(e) => {
                log::warn!("Failed to load ONNX model: {}", e);
                None
            }
        }
    } else {
        log::info!(
            "ONNX model not found. Run scripts/download-model.sh to enable AI layout analysis."
        );
        None
    };

    // Set up winit + glutin + skia
    let el = EventLoop::new()?;

    let window_attributes = Window::default_attributes()
        .with_inner_size(LogicalSize::new(
            DEFAULT_WINDOW_WIDTH,
            DEFAULT_WINDOW_HEIGHT,
        ))
        .with_resizable(true)
        .with_title("railreader2");

    let template = ConfigTemplateBuilder::new()
        .with_alpha_size(8)
        .with_transparency(true);

    let display_builder = DisplayBuilder::new().with_window_attributes(Some(window_attributes));
    let (window, gl_config) = display_builder
        .build(&el, template, |configs| {
            configs
                .reduce(|accum, config| {
                    let transparency_check = config.supports_transparency().unwrap_or(false)
                        & !accum.supports_transparency().unwrap_or(false);
                    if transparency_check || config.num_samples() < accum.num_samples() {
                        config
                    } else {
                        accum
                    }
                })
                .unwrap()
        })
        .unwrap();
    let window = window.expect("Could not create window with OpenGL context");
    let window_handle = window
        .window_handle()
        .expect("Failed to retrieve window handle");
    let raw_window_handle = window_handle.as_raw();

    let context_attributes = ContextAttributesBuilder::new().build(Some(raw_window_handle));
    let fallback_context_attributes = ContextAttributesBuilder::new()
        .with_context_api(ContextApi::Gles(None))
        .build(Some(raw_window_handle));

    let not_current_gl_context = unsafe {
        gl_config
            .display()
            .create_context(&gl_config, &context_attributes)
            .unwrap_or_else(|_| {
                gl_config
                    .display()
                    .create_context(&gl_config, &fallback_context_attributes)
                    .expect("failed to create context")
            })
    };

    let (width, height): (u32, u32) = window.inner_size().into();
    let attrs = SurfaceAttributesBuilder::<WindowSurface>::new().build(
        raw_window_handle,
        NonZeroU32::new(width).unwrap(),
        NonZeroU32::new(height).unwrap(),
    );

    let gl_surface = unsafe {
        gl_config
            .display()
            .create_window_surface(&gl_config, &attrs)
            .expect("Could not create gl window surface")
    };

    let gl_context = not_current_gl_context
        .make_current(&gl_surface)
        .expect("Could not make GL context current");

    // Enable VSync to eliminate tearing during rail-mode scrolling
    gl_surface
        .set_swap_interval(&gl_context, glutin::surface::SwapInterval::Wait(NonZeroU32::new(1).unwrap()))
        .unwrap_or_else(|e| log::warn!("Failed to set VSync: {}", e));

    gl::load_with(|s| {
        gl_config
            .display()
            .get_proc_address(CString::new(s).unwrap().as_c_str())
    });
    let interface = skia_safe::gpu::gl::Interface::new_load_with(|name| {
        if name == "eglGetCurrentDisplay" {
            return std::ptr::null();
        }
        gl_config
            .display()
            .get_proc_address(CString::new(name).unwrap().as_c_str())
    })
    .expect("Could not create interface");

    let mut gr_context = skia_safe::gpu::direct_contexts::make_gl(interface, None)
        .expect("Could not create direct context");

    let fb_info = {
        let mut fboid: GLint = 0;
        unsafe { gl::GetIntegerv(gl::FRAMEBUFFER_BINDING, &mut fboid) };
        FramebufferInfo {
            fboid: fboid.try_into().unwrap(),
            format: skia_safe::gpu::gl::Format::RGBA8.into(),
            ..Default::default()
        }
    };

    let num_samples = gl_config.num_samples() as usize;
    let stencil_size = gl_config.stencil_size() as usize;

    let surface = create_surface(&window, fb_info, &mut gr_context, num_samples, stencil_size);

    // Status bar font
    let font_mgr_for_font = skia_safe::FontMgr::default();
    let typeface = font_mgr_for_font
        .match_family_style("DejaVu Sans", FontStyle::default())
        .or_else(|| font_mgr_for_font.match_family_style("sans-serif", FontStyle::default()))
        .expect("Could not find any sans-serif font");
    let status_font = Font::from_typeface(typeface, 14.0);

    let env = Env {
        surface,
        gl_surface,
        gr_context,
        gl_context,
        window,
        fb_info,
        num_samples,
        stencil_size,
    };

    // Initialize egui integration
    let egui_integration = match EguiIntegration::new(&env.window, &env.gl_context) {
        Ok(e) => Some(e),
        Err(e) => {
            log::warn!("Failed to initialize egui: {}", e);
            None
        }
    };

    let config = Config::load();

    // Apply initial UI font scale
    if let Some(egui_int) = &egui_integration {
        egui_int.set_font_scale(config.ui_font_scale);
    }

    // Run cleanup on startup
    let report = cleanup::run_cleanup();
    if report.files_removed > 0 {
        log::info!("Startup cleanup: {}", report);
    }

    let mut colour_effect_state = ColourEffectState::new();
    colour_effect_state.effect = config.colour_effect;
    colour_effect_state.intensity = config.colour_effect_intensity as f32;

    let mut app = App {
        env,
        tabs: Vec::new(),
        active_tab: 0,
        worker,
        config,
        egui_integration,
        ui_state: UiState::default(),
        colour_effect_state,
        modifiers: Modifiers::default(),
        dragging: false,
        press_pos: None,
        last_cursor: None,
        cursor_pos: (0.0, 0.0),
        quit_requested: false,
        last_frame: std::time::Instant::now(),
        status_font,
    };

    // Open initial PDF if provided
    if let Some(path) = pdf_path {
        log::info!("Loading PDF: {}", path);
        app.open_document(path);
    }

    el.run_app(&mut app).expect("Couldn't run event loop");

    Ok(())
}
