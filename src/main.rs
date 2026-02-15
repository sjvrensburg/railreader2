use anyhow::Result;
use std::sync::Arc;
use vello::kurbo::Affine;
use vello::peniko::color::palette;
use vello::util::{RenderContext, RenderSurface};
use vello::wgpu;
use vello::{AaConfig, Renderer, RendererOptions, Scene};
use winit::application::ApplicationHandler;
use winit::dpi::LogicalSize;
use winit::event::{ElementState, MouseButton, MouseScrollDelta, WindowEvent};
use winit::event_loop::{ActiveEventLoop, EventLoop};
use winit::window::Window;

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

    fn transform(&self) -> Affine {
        Affine::translate((self.offset_x, self.offset_y)) * Affine::scale(self.zoom)
    }
}

#[derive(Debug)]
enum RenderState {
    Active {
        surface: Box<RenderSurface<'static>>,
        valid_surface: bool,
        window: Arc<Window>,
    },
    Suspended(Option<Arc<Window>>),
}

struct App {
    context: RenderContext,
    renderers: Vec<Option<Renderer>>,
    state: RenderState,
    pdf_scene: Scene,
    page_width: f64,
    page_height: f64,
    camera: Camera,
    dragging: bool,
    last_cursor: Option<(f64, f64)>,
}

impl ApplicationHandler for App {
    fn resumed(&mut self, event_loop: &ActiveEventLoop) {
        let RenderState::Suspended(cached_window) = &mut self.state else {
            return;
        };

        let window = cached_window
            .take()
            .unwrap_or_else(|| create_window(event_loop, self.page_width, self.page_height));

        let size = window.inner_size();
        let surface_future = self.context.create_surface(
            window.clone(),
            size.width,
            size.height,
            wgpu::PresentMode::AutoVsync,
        );
        let surface = pollster::block_on(surface_future).expect("Error creating surface");

        self.renderers
            .resize_with(self.context.devices.len(), || None);
        self.renderers[surface.dev_id]
            .get_or_insert_with(|| create_renderer(&self.context, &surface));

        self.state = RenderState::Active {
            surface: Box::new(surface),
            valid_surface: true,
            window,
        };
    }

    fn suspended(&mut self, _event_loop: &ActiveEventLoop) {
        if let RenderState::Active { window, .. } = &self.state {
            self.state = RenderState::Suspended(Some(window.clone()));
        }
    }

    fn window_event(
        &mut self,
        event_loop: &ActiveEventLoop,
        window_id: winit::window::WindowId,
        event: WindowEvent,
    ) {
        let (surface, valid_surface, window) = match &mut self.state {
            RenderState::Active {
                surface,
                valid_surface,
                window,
            } if window.id() == window_id => (surface, valid_surface, window.clone()),
            _ => return,
        };

        match event {
            WindowEvent::CloseRequested => event_loop.exit(),

            WindowEvent::Resized(size) => {
                if size.width != 0 && size.height != 0 {
                    self.context
                        .resize_surface(surface, size.width, size.height);
                    *valid_surface = true;
                } else {
                    *valid_surface = false;
                }
            }

            WindowEvent::MouseWheel { delta, .. } => {
                let scroll_y = match delta {
                    MouseScrollDelta::LineDelta(_, y) => y as f64 * 30.0,
                    MouseScrollDelta::PixelDelta(pos) => pos.y,
                };
                let factor = 1.0 + scroll_y * 0.003;
                self.camera.zoom = (self.camera.zoom * factor).clamp(0.1, 20.0);
                window.request_redraw();
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
                        window.request_redraw();
                    }
                    self.last_cursor = Some((position.x, position.y));
                }
            }

            WindowEvent::RedrawRequested => {
                if !*valid_surface {
                    return;
                }

                let width = surface.config.width;
                let height = surface.config.height;

                // Build final scene: white background rect + PDF scene with camera transform
                let mut scene = Scene::new();
                // Draw white page background
                let page_rect =
                    vello::kurbo::Rect::new(0.0, 0.0, self.page_width, self.page_height);
                let camera_transform = self.camera.transform();
                scene.fill(
                    vello::peniko::Fill::NonZero,
                    camera_transform,
                    vello::peniko::Color::new([1.0, 1.0, 1.0, 1.0]),
                    None,
                    &page_rect,
                );
                scene.append(&self.pdf_scene, Some(camera_transform));

                let device_handle = &self.context.devices[surface.dev_id];

                self.renderers[surface.dev_id]
                    .as_mut()
                    .unwrap()
                    .render_to_texture(
                        &device_handle.device,
                        &device_handle.queue,
                        &scene,
                        &surface.target_view,
                        &vello::RenderParams {
                            base_color: palette::css::GRAY,
                            width,
                            height,
                            antialiasing_method: AaConfig::Msaa16,
                        },
                    )
                    .expect("failed to render");

                let surface_texture = surface
                    .surface
                    .get_current_texture()
                    .expect("failed to get surface texture");

                let mut encoder =
                    device_handle
                        .device
                        .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                            label: Some("Surface Blit"),
                        });
                surface.blitter.copy(
                    &device_handle.device,
                    &mut encoder,
                    &surface.target_view,
                    &surface_texture
                        .texture
                        .create_view(&wgpu::TextureViewDescriptor::default()),
                );
                device_handle.queue.submit([encoder.finish()]);
                surface_texture.present();
                device_handle.device.poll(wgpu::PollType::Poll).unwrap();
            }

            _ => {}
        }
    }
}

fn create_window(event_loop: &ActiveEventLoop, page_width: f64, page_height: f64) -> Arc<Window> {
    let attr = Window::default_attributes()
        .with_inner_size(LogicalSize::new(
            page_width.min(1200.0),
            page_height.min(900.0),
        ))
        .with_resizable(true)
        .with_title("railreader2");
    Arc::new(event_loop.create_window(attr).unwrap())
}

fn create_renderer(render_cx: &RenderContext, surface: &RenderSurface<'_>) -> Renderer {
    Renderer::new(
        &render_cx.devices[surface.dev_id].device,
        RendererOptions::default(),
    )
    .expect("Couldn't create renderer")
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

    let doc = lopdf::Document::load(pdf_path)
        .map_err(|e| anyhow::anyhow!("Failed to load PDF '{}': {}", pdf_path, e))?;

    let page_count = doc.get_pages().len();
    log::info!("PDF has {} page(s)", page_count);

    let (pdf_scene, page_width, page_height) = railreader2::render_page(&doc, 1)?;
    log::info!("Page dimensions: {}x{}", page_width, page_height);

    let mut app = App {
        context: RenderContext::new(),
        renderers: vec![],
        state: RenderState::Suspended(None),
        pdf_scene,
        page_width,
        page_height,
        camera: Camera::new(),
        dragging: false,
        last_cursor: None,
    };

    let event_loop = EventLoop::new()?;
    event_loop
        .run_app(&mut app)
        .expect("Couldn't run event loop");

    Ok(())
}
