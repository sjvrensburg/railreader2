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

fn draw_status_bar(
    canvas: &skia_safe::Canvas,
    width: u32,
    height: u32,
    font: &Font,
    camera: &Camera,
    current_page: i32,
    page_count: i32,
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
    let status = format!(
        "Page {}/{} | Zoom: {}%",
        current_page + 1,
        page_count,
        zoom_pct
    );

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
                let factor = 1.0 + scroll_y * 0.003;
                self.camera.zoom = (self.camera.zoom * factor).clamp(0.1, 20.0);
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
                        self.camera.offset_x += dx;
                        self.camera.offset_y += dy;
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
                        self.env.window.request_redraw();
                    }
                    Key::Character(c) if c.as_str() == "-" => {
                        self.camera.zoom = (self.camera.zoom / 1.25).clamp(0.1, 20.0);
                        self.env.window.request_redraw();
                    }
                    Key::Character(c) if c.as_str() == "0" => {
                        self.camera.zoom = 1.0;
                        self.camera.offset_x = 0.0;
                        self.camera.offset_y = 0.0;
                        self.env.window.request_redraw();
                    }
                    Key::Named(NamedKey::ArrowUp) => {
                        self.camera.offset_y += 50.0;
                        self.env.window.request_redraw();
                    }
                    Key::Named(NamedKey::ArrowDown) => {
                        self.camera.offset_y -= 50.0;
                        self.env.window.request_redraw();
                    }
                    Key::Named(NamedKey::ArrowLeft) => {
                        self.camera.offset_x += 50.0;
                        self.env.window.request_redraw();
                    }
                    Key::Named(NamedKey::ArrowRight) => {
                        self.camera.offset_x -= 50.0;
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
                );

                self.env.gr_context.flush_and_submit();
                self.env
                    .gl_surface
                    .swap_buffers(&self.env.gl_context)
                    .unwrap();
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

    let (svg_string, page_width, page_height) = railreader2::render_page_svg(&doc, 0)?;
    log::info!("Page dimensions: {}x{}", page_width, page_height);

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
    };

    el.run_app(&mut app).expect("Couldn't run event loop");

    Ok(())
}
