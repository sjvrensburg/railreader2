use anyhow::Result;
use egui_winit::EventResponse;
use glutin::display::{GetGlDisplay, GlDisplay};
use std::sync::Arc;

/// egui integration state, managing the UI context, winit platform, and glow painter.
pub struct EguiIntegration {
    pub ctx: egui::Context,
    pub winit_state: egui_winit::State,
    pub painter: egui_glow::Painter,
    shapes: Vec<egui::epaint::ClippedShape>,
    textures_delta: egui::TexturesDelta,
    pixels_per_point: f32,
}

impl EguiIntegration {
    /// Create a new egui integration instance.
    ///
    /// # Safety
    /// The OpenGL context must be current and the GL loader must be initialized.
    pub fn new(
        window: &winit::window::Window,
        gl_context: &glutin::context::PossiblyCurrentContext,
    ) -> Result<Self> {
        // Get the display from the GL context
        let display = gl_context.display();

        // Create a glow context
        let glow_context = unsafe {
            glow::Context::from_loader_function(|s| {
                let s = std::ffi::CString::new(s)
                    .expect("failed to construct CString for GL function pointer");
                display.get_proc_address(s.as_c_str()).cast()
            })
        };
        let glow_context = Arc::new(glow_context);

        // Determine the framebuffer info for the glow painter
        let mut fboid: i32 = 0;
        unsafe { gl::GetIntegerv(gl::FRAMEBUFFER_BINDING, &mut fboid) };

        // Create painter with correct signature for egui_glow 0.31
        let painter = egui_glow::Painter::new(
            glow_context.clone(),
            "",
            None,  // shader_version
            false, // srgb
        )?;

        // Create egui context
        let ctx = egui::Context::default();

        // Use ROOT viewport ID for the main window
        let viewport_id = egui::ViewportId::ROOT;

        // Create winit state with correct signature for egui-winit 0.31
        let winit_state = egui_winit::State::new(
            ctx.clone(),
            viewport_id,
            window,
            None, // theme
            None, // max_texture_side
            None, // icon_scale
        );

        Ok(Self {
            ctx,
            winit_state,
            painter,
            shapes: Default::default(),
            textures_delta: Default::default(),
            pixels_per_point: window.scale_factor() as f32,
        })
    }

    /// Handle a winit window event.
    ///
    /// Returns `true` if egui consumed the event (it should not be processed further).
    pub fn handle_event(
        &mut self,
        window: &winit::window::Window,
        event: &winit::event::WindowEvent,
    ) -> EventResponse {
        self.winit_state.on_window_event(window, event)
    }

    /// Begin a new frame, returning the egui context for UI building.
    ///
    /// Call this before building your UI. Returns a reference to the egui Context
    /// that you can use to build UI panels and widgets.
    pub fn begin_frame(&mut self, window: &winit::window::Window) -> &egui::Context {
        let raw_input = self.winit_state.take_egui_input(window);
        self.ctx.begin_pass(raw_input);
        &self.ctx
    }

    /// End the UI frame and prepare for rendering.
    ///
    /// Call this after building your UI but before calling `paint()`.
    pub fn end_frame(&mut self, window: &winit::window::Window) {
        // Store pixels_per_point BEFORE end_pass() clears the input state
        self.pixels_per_point = self.ctx.input(|i| i.pixels_per_point);

        let egui_output = self.ctx.end_pass();
        self.winit_state
            .handle_platform_output(window, egui_output.platform_output);
        self.shapes = egui_output.shapes;
        self.textures_delta = egui_output.textures_delta;
    }

    /// Paint the UI to the current OpenGL framebuffer.
    ///
    /// Call this after `end_frame()`. This will render the UI on top of whatever
    /// is currently in the framebuffer (your Skia-rendered PDF).
    pub fn paint(&mut self, window: &winit::window::Window) {
        // Use the stored pixels_per_point (captured in end_frame before end_pass cleared it)
        let pixels_per_point = self.pixels_per_point;

        // Collect shapes and textures
        let shapes = std::mem::take(&mut self.shapes);
        let textures_delta = std::mem::take(&mut self.textures_delta);

        // Tessellate shapes with correct DPI scale
        let meshes = self.ctx.tessellate(shapes, pixels_per_point);

        // Get the window size for the painter
        let size = window.inner_size();
        let max_texture_size = [size.width, size.height];

        // Paint the meshes with correct pixels_per_point
        self.painter.paint_and_update_textures(
            max_texture_size,
            pixels_per_point,
            &meshes,
            &textures_delta,
        );
    }

    /// Get the current pixels per point (DPI scaling factor).
    pub fn pixels_per_point(&self) -> f32 {
        // Use the input state to get pixels_per_point
        self.ctx.input(|i| i.pixels_per_point)
    }

    /// Request a redraw from egui (e.g., when animations are running).
    pub fn request_redraw(&self) {
        self.ctx.request_repaint();
    }

    /// Check if egui wants to continuously repaint (e.g., due to animations).
    pub fn wants_continuous_repaint(&self) -> bool {
        self.ctx.has_requested_repaint()
    }
}

impl Drop for EguiIntegration {
    fn drop(&mut self) {
        self.painter.destroy();
    }
}
