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
    event::{ElementState, MouseButton, MouseScrollDelta, WindowEvent},
    event_loop::EventLoop,
    keyboard::{Key, NamedKey},
    window::Window,
};

use railreader2::layout::{self, LAYOUT_CLASSES};
use railreader2::rail::{NavResult, RailNav};

struct Camera {
    offset_x: f64,
    offset_y: f64,
    zoom: f64,
}

impl Camera {
    fn new() -> Self {
        Self {
            offset_x: 0.0,
            offset_y: 0.0,
            zoom: 1.0,
        }
    }
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
    doc: mupdf::Document,
    current_page: i32,
    page_count: i32,
    svg_dom: Option<skia_safe::svg::Dom>,
    page_width: f64,
    page_height: f64,
    camera: Camera,
    dragging: bool,
    last_cursor: Option<(f64, f64)>,
    status_font: Font,
    ort_session: Option<ort::session::Session>,
    rail: RailNav,
    debug_overlay: bool,
}

impl App {
    fn load_page(&mut self) {
        match railreader2::render_page_svg(&self.doc, self.current_page) {
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

        // Run layout analysis
        self.analyze_current_page();
    }

    fn analyze_current_page(&mut self) {
        let analysis = if let Some(session) = &mut self.ort_session {
            match layout::analyze_page(session, &self.doc, self.current_page) {
                Ok(a) => {
                    log::info!(
                        "Layout analysis: {} blocks detected on page {}",
                        a.blocks.len(),
                        self.current_page + 1
                    );
                    a
                }
                Err(e) => {
                    log::warn!("Layout analysis failed: {}, using fallback", e);
                    layout::fallback_analysis(self.page_width, self.page_height)
                }
            }
        } else {
            log::info!("No ONNX model loaded, using fallback layout");
            layout::fallback_analysis(self.page_width, self.page_height)
        };

        self.rail.set_analysis(analysis);

        // Update rail activation based on current zoom
        let size = self.env.window.inner_size();
        self.rail.update_zoom(
            self.camera.zoom,
            self.camera.offset_x,
            self.camera.offset_y,
            size.width as f64,
            size.height as f64,
        );
    }

    fn go_to_page(&mut self, page: i32) {
        let page = page.clamp(0, self.page_count - 1);
        if page != self.current_page {
            self.current_page = page;
            self.load_page();
            self.camera.offset_x = 0.0;
            self.camera.offset_y = 0.0;
        }
    }

    fn start_snap(&mut self) {
        let size = self.env.window.inner_size();
        self.rail.start_snap_to_current(
            self.camera.offset_x,
            self.camera.offset_y,
            self.camera.zoom,
            size.width as f64,
            size.height as f64,
        );
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

#[allow(clippy::too_many_arguments)]
fn draw_status_bar(
    canvas: &skia_safe::Canvas,
    width: u32,
    height: u32,
    font: &Font,
    camera: &Camera,
    current_page: i32,
    page_count: i32,
    rail: &RailNav,
) {
    let bar_height = 28.0_f32;
    let bar_y = height as f32 - bar_height;

    let mut bg_paint = Paint::default();
    bg_paint.set_color(Color::from_argb(180, 0, 0, 0));
    canvas.draw_rect(
        Rect::from_xywh(0.0, bar_y, width as f32, bar_height),
        &bg_paint,
    );

    let zoom_pct = (camera.zoom * 100.0).round() as i32;
    let status = if rail.active {
        format!(
            "Page {}/{} | Zoom: {}% | Block {}/{} | Line {}/{}",
            current_page + 1,
            page_count,
            zoom_pct,
            rail.current_block + 1,
            rail.navigable_count(),
            rail.current_line + 1,
            rail.current_line_count(),
        )
    } else {
        format!(
            "Page {}/{} | Zoom: {}%",
            current_page + 1,
            page_count,
            zoom_pct
        )
    };

    let mut text_paint = Paint::default();
    text_paint.set_color(Color::WHITE);
    text_paint.set_anti_alias(true);

    canvas.draw_str(
        &status,
        (10.0, bar_y + bar_height * 0.72),
        font,
        &text_paint,
    );
}

fn draw_rail_overlays(
    canvas: &skia_safe::Canvas,
    rail: &RailNav,
    page_width: f64,
    page_height: f64,
    debug_overlay: bool,
) {
    if !rail.active || !rail.has_analysis() {
        if debug_overlay {
            draw_debug_overlay(canvas, rail);
        }
        return;
    }

    // Dimming: semi-transparent overlay over entire page
    let mut dim_paint = Paint::default();
    dim_paint.set_color(Color::from_argb(120, 0, 0, 0));
    canvas.draw_rect(
        Rect::from_wh(page_width as f32, page_height as f32),
        &dim_paint,
    );

    // Draw current block area back to full brightness (clear the dimming)
    let block = rail.current_navigable_block();
    let margin = 4.0f32;
    let block_rect = Rect::from_xywh(
        block.bbox.x - margin,
        block.bbox.y - margin,
        block.bbox.w + margin * 2.0,
        block.bbox.h + margin * 2.0,
    );

    // Save, clip to block area, and draw white + restore effect
    canvas.save();
    canvas.clip_rect(block_rect, skia_safe::ClipOp::Intersect, false);
    // Undo the dimming by drawing the inverse
    let mut clear_paint = Paint::default();
    clear_paint.set_color(Color::from_argb(120, 255, 255, 255));
    clear_paint.set_blend_mode(skia_safe::BlendMode::Plus);
    canvas.draw_rect(block_rect, &clear_paint);
    canvas.restore();

    // Block outline
    let mut outline_paint = Paint::default();
    outline_paint.set_color(Color::from_argb(80, 66, 133, 244));
    outline_paint.set_style(skia_safe::paint::Style::Stroke);
    outline_paint.set_stroke_width(1.5);
    outline_paint.set_anti_alias(true);
    canvas.draw_rect(
        Rect::from_xywh(block.bbox.x, block.bbox.y, block.bbox.w, block.bbox.h),
        &outline_paint,
    );

    // Line highlight
    let line = rail.current_line_info();
    let mut line_paint = Paint::default();
    line_paint.set_color(Color::from_argb(40, 66, 133, 244));
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
    let analysis = match rail.analysis() {
        Some(a) => a,
        None => return,
    };

    let colors: [(u8, u8, u8); 6] = [
        (244, 67, 54),  // red
        (33, 150, 243), // blue
        (76, 175, 80),  // green
        (255, 152, 0),  // orange
        (156, 39, 176), // purple
        (0, 188, 212),  // cyan
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

        // Label
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

impl ApplicationHandler for App {
    fn resumed(&mut self, _event_loop: &winit::event_loop::ActiveEventLoop) {}

    fn window_event(
        &mut self,
        event_loop: &winit::event_loop::ActiveEventLoop,
        _window_id: winit::window::WindowId,
        event: WindowEvent,
    ) {
        match event {
            WindowEvent::CloseRequested => event_loop.exit(),

            WindowEvent::Resized(physical_size) => {
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
                self.env.window.request_redraw();
            }

            WindowEvent::MouseWheel { delta, .. } => {
                let scroll_y = match delta {
                    MouseScrollDelta::LineDelta(_, y) => y as f64 * 30.0,
                    MouseScrollDelta::PixelDelta(pos) => pos.y,
                };

                if self.rail.active {
                    // In rail mode, scroll triggers line navigation
                    if let Some(result) = self.rail.handle_scroll(scroll_y) {
                        match result {
                            NavResult::PageBoundaryNext => {
                                self.go_to_page(self.current_page + 1);
                            }
                            NavResult::PageBoundaryPrev => {
                                self.go_to_page(self.current_page - 1);
                            }
                            NavResult::Ok => {
                                self.start_snap();
                            }
                        }
                    }
                } else {
                    // Free zoom
                    let factor = 1.0 + scroll_y * 0.003;
                    self.camera.zoom = (self.camera.zoom * factor).clamp(0.1, 20.0);

                    // Check if we should activate rail mode
                    let size = self.env.window.inner_size();
                    self.rail.update_zoom(
                        self.camera.zoom,
                        self.camera.offset_x,
                        self.camera.offset_y,
                        size.width as f64,
                        size.height as f64,
                    );

                    if self.rail.active {
                        self.start_snap();
                    }
                }
                self.env.window.request_redraw();
            }

            WindowEvent::MouseInput { state, button, .. } => {
                if button == MouseButton::Left {
                    self.dragging = state == ElementState::Pressed;
                    if !self.dragging {
                        self.last_cursor = None;
                    }
                }
            }

            WindowEvent::CursorMoved { position, .. } => {
                if self.dragging {
                    if let Some((lx, ly)) = self.last_cursor {
                        let dx = position.x - lx;
                        let dy = position.y - ly;

                        if self.rail.active {
                            // Horizontal pan constrained to block
                            self.camera.offset_x += dx;
                            let size = self.env.window.inner_size();
                            self.rail.constrain_pan(
                                &mut self.camera.offset_x,
                                self.camera.zoom,
                                size.width as f64,
                            );
                            // Vertical drag triggers line navigation
                            if let Some(result) = self.rail.handle_scroll(dy) {
                                match result {
                                    NavResult::PageBoundaryNext => {
                                        self.go_to_page(self.current_page + 1);
                                    }
                                    NavResult::PageBoundaryPrev => {
                                        self.go_to_page(self.current_page - 1);
                                    }
                                    NavResult::Ok => {
                                        self.start_snap();
                                    }
                                }
                            }
                        } else {
                            self.camera.offset_x += dx;
                            self.camera.offset_y += dy;
                        }
                        self.env.window.request_redraw();
                    }
                    self.last_cursor = Some((position.x, position.y));
                }
            }

            WindowEvent::KeyboardInput { event, .. } => {
                if event.state != ElementState::Pressed {
                    return;
                }
                match &event.logical_key {
                    Key::Named(NamedKey::PageDown) => {
                        self.go_to_page(self.current_page + 1);
                        self.env.window.request_redraw();
                    }
                    Key::Named(NamedKey::PageUp) => {
                        self.go_to_page(self.current_page - 1);
                        self.env.window.request_redraw();
                    }
                    Key::Named(NamedKey::Home) => {
                        self.go_to_page(0);
                        self.env.window.request_redraw();
                    }
                    Key::Named(NamedKey::End) => {
                        self.go_to_page(self.page_count - 1);
                        self.env.window.request_redraw();
                    }
                    Key::Character(c) if c.as_str() == "+" || c.as_str() == "=" => {
                        self.camera.zoom = (self.camera.zoom * 1.25).clamp(0.1, 20.0);
                        let size = self.env.window.inner_size();
                        self.rail.update_zoom(
                            self.camera.zoom,
                            self.camera.offset_x,
                            self.camera.offset_y,
                            size.width as f64,
                            size.height as f64,
                        );
                        if self.rail.active {
                            self.start_snap();
                        }
                        self.env.window.request_redraw();
                    }
                    Key::Character(c) if c.as_str() == "-" => {
                        self.camera.zoom = (self.camera.zoom / 1.25).clamp(0.1, 20.0);
                        let size = self.env.window.inner_size();
                        self.rail.update_zoom(
                            self.camera.zoom,
                            self.camera.offset_x,
                            self.camera.offset_y,
                            size.width as f64,
                            size.height as f64,
                        );
                        self.env.window.request_redraw();
                    }
                    Key::Character(c) if c.as_str() == "0" => {
                        self.camera.zoom = 1.0;
                        self.camera.offset_x = 0.0;
                        self.camera.offset_y = 0.0;
                        let size = self.env.window.inner_size();
                        self.rail.update_zoom(
                            self.camera.zoom,
                            self.camera.offset_x,
                            self.camera.offset_y,
                            size.width as f64,
                            size.height as f64,
                        );
                        self.env.window.request_redraw();
                    }
                    Key::Named(NamedKey::ArrowDown) => {
                        if self.rail.active {
                            match self.rail.next_line() {
                                NavResult::PageBoundaryNext => {
                                    self.go_to_page(self.current_page + 1);
                                }
                                NavResult::Ok => {
                                    self.start_snap();
                                }
                                _ => {}
                            }
                        } else {
                            self.camera.offset_y -= 50.0;
                        }
                        self.env.window.request_redraw();
                    }
                    Key::Named(NamedKey::ArrowUp) => {
                        if self.rail.active {
                            match self.rail.prev_line() {
                                NavResult::PageBoundaryPrev => {
                                    self.go_to_page(self.current_page - 1);
                                }
                                NavResult::Ok => {
                                    self.start_snap();
                                }
                                _ => {}
                            }
                        } else {
                            self.camera.offset_y += 50.0;
                        }
                        self.env.window.request_redraw();
                    }
                    Key::Named(NamedKey::ArrowRight) => {
                        if self.rail.active {
                            match self.rail.next_block() {
                                NavResult::PageBoundaryNext => {
                                    self.go_to_page(self.current_page + 1);
                                }
                                NavResult::Ok => {
                                    self.start_snap();
                                }
                                _ => {}
                            }
                        } else {
                            self.camera.offset_x -= 50.0;
                        }
                        self.env.window.request_redraw();
                    }
                    Key::Named(NamedKey::ArrowLeft) => {
                        if self.rail.active {
                            match self.rail.prev_block() {
                                NavResult::PageBoundaryPrev => {
                                    self.go_to_page(self.current_page - 1);
                                }
                                NavResult::Ok => {
                                    self.start_snap();
                                }
                                _ => {}
                            }
                        } else {
                            self.camera.offset_x += 50.0;
                        }
                        self.env.window.request_redraw();
                    }
                    Key::Character(c) if c.as_str() == "d" || c.as_str() == "D" => {
                        self.debug_overlay = !self.debug_overlay;
                        log::info!("Debug overlay: {}", self.debug_overlay);
                        self.env.window.request_redraw();
                    }
                    Key::Named(NamedKey::Escape) => event_loop.exit(),
                    Key::Character(c) if c.as_str() == "q" => event_loop.exit(),
                    _ => {}
                }
            }

            WindowEvent::RedrawRequested => {
                let size = self.env.window.inner_size();
                if size.width == 0 || size.height == 0 {
                    return;
                }

                // Advance snap animation
                let animating = self
                    .rail
                    .tick(&mut self.camera.offset_x, &mut self.camera.offset_y);

                let canvas = self.env.surface.canvas();
                canvas.clear(Color::from_argb(255, 128, 128, 128));

                canvas.save();
                canvas.translate((self.camera.offset_x as f32, self.camera.offset_y as f32));
                canvas.scale((self.camera.zoom as f32, self.camera.zoom as f32));

                // White page background
                let mut white_paint = Paint::default();
                white_paint.set_color(Color::WHITE);
                canvas.draw_rect(
                    Rect::from_wh(self.page_width as f32, self.page_height as f32),
                    &white_paint,
                );

                // SVG content
                if let Some(dom) = &mut self.svg_dom {
                    dom.render(canvas);
                }

                // Rail overlays (drawn in page coordinate space)
                draw_rail_overlays(
                    canvas,
                    &self.rail,
                    self.page_width,
                    self.page_height,
                    self.debug_overlay,
                );

                canvas.restore();

                // Status bar (drawn in screen space)
                draw_status_bar(
                    canvas,
                    size.width,
                    size.height,
                    &self.status_font,
                    &self.camera,
                    self.current_page,
                    self.page_count,
                    &self.rail,
                );

                self.env.gr_context.flush_and_submit();
                self.env
                    .gl_surface
                    .swap_buffers(&self.env.gl_context)
                    .unwrap();

                // Request another frame if animating
                if animating {
                    self.env.window.request_redraw();
                }
            }

            _ => {}
        }
    }
}

fn main() -> Result<()> {
    env_logger::init();

    let args: Vec<String> = std::env::args().collect();
    if args.len() < 2 {
        eprintln!("Usage: {} <path-to-pdf>", args[0]);
        std::process::exit(1);
    }

    let pdf_path = &args[1];
    log::info!("Loading PDF: {}", pdf_path);

    let doc = mupdf::Document::open(pdf_path)?;
    let page_count = doc.page_count()?;
    log::info!("PDF has {} page(s)", page_count);

    // Try to load ONNX model
    let model_path =
        std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("models/PP-DocLayoutV3.onnx");
    let mut ort_session = if model_path.exists() {
        match layout::load_model(model_path.to_str().unwrap()) {
            Ok(session) => {
                log::info!("Loaded ONNX model from {}", model_path.display());
                Some(session)
            }
            Err(e) => {
                log::warn!("Failed to load ONNX model: {}", e);
                None
            }
        }
    } else {
        log::info!(
            "ONNX model not found at {}. Run scripts/download-model.sh to enable AI layout analysis.",
            model_path.display()
        );
        None
    };

    let (svg_string, page_width, page_height) = railreader2::render_page_svg(&doc, 0)?;
    log::info!("Page dimensions: {}x{}", page_width, page_height);

    // Run initial layout analysis
    let initial_analysis = if let Some(session) = &mut ort_session {
        match layout::analyze_page(session, &doc, 0) {
            Ok(a) => {
                log::info!("Initial layout: {} blocks detected", a.blocks.len());
                Some(a)
            }
            Err(e) => {
                log::warn!("Initial layout analysis failed: {}", e);
                None
            }
        }
    } else {
        None
    };

    // Set up winit + glutin + skia
    let el = EventLoop::new()?;

    let window_attributes = Window::default_attributes()
        .with_inner_size(LogicalSize::new(
            page_width.min(1200.0),
            page_height.min(900.0),
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

    // Parse SVG for page 0
    let font_mgr = skia_safe::FontMgr::default();
    let svg_dom = skia_safe::svg::Dom::from_str(svg_string, font_mgr).ok();

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

    let mut rail = RailNav::new();
    if let Some(analysis) = initial_analysis {
        rail.set_analysis(analysis);
    } else {
        rail.set_analysis(layout::fallback_analysis(page_width, page_height));
    }

    let mut app = App {
        env,
        doc,
        current_page: 0,
        page_count,
        svg_dom,
        page_width,
        page_height,
        camera: Camera::new(),
        dragging: false,
        last_cursor: None,
        status_font,
        ort_session,
        rail,
        debug_overlay: false,
    };

    el.run_app(&mut app).expect("Couldn't run event loop");

    Ok(())
}
