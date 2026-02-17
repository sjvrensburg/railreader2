use crate::tab::TabState;
use crate::ui::UiAction;

pub fn show_minimap(ctx: &egui::Context, show: &mut bool, tab: &TabState) -> Vec<UiAction> {
    let mut actions = Vec::new();

    if !*show {
        return actions;
    }

    egui::Window::new("Minimap")
        .open(show)
        .resizable(false)
        .default_width(160.0)
        .anchor(egui::Align2::RIGHT_TOP, [-10.0, 40.0])
        .show(ctx, |ui| {
            if let Some(tex) = &tab.minimap_texture {
                let size = tex.size_vec2();
                // Scale to fit in minimap window
                let max_width = 150.0;
                let scale = max_width / size.x;
                let display_size = egui::vec2(size.x * scale, size.y * scale);

                let (response, painter) =
                    ui.allocate_painter(display_size, egui::Sense::click_and_drag());

                // Draw the page thumbnail
                painter.image(
                    tex.id(),
                    response.rect,
                    egui::Rect::from_min_max(egui::pos2(0.0, 0.0), egui::pos2(1.0, 1.0)),
                    egui::Color32::WHITE,
                );

                // Handle click/drag on minimap
                if tab.page_width > 0.0 && tab.page_height > 0.0 {
                    if let Some(pos) = response.interact_pointer_pos() {
                        let rect = response.rect;
                        // Convert minimap position to normalized page coordinates (0.0-1.0)
                        let norm_x = ((pos.x - rect.min.x) / rect.width()).clamp(0.0, 1.0) as f64;
                        let norm_y = ((pos.y - rect.min.y) / rect.height()).clamp(0.0, 1.0) as f64;

                        // Compute target camera offset to center the clicked point
                        // We use content_rect width/height as approximation of viewport
                        let screen = ctx.screen_rect();
                        let vp_w = screen.width() as f64;
                        let vp_h = screen.height() as f64;

                        let offset_x = -(norm_x * tab.page_width * tab.camera.zoom) + vp_w / 2.0;
                        let offset_y = -(norm_y * tab.page_height * tab.camera.zoom) + vp_h / 2.0;

                        actions.push(UiAction::SetCamera(offset_x, offset_y));
                    }

                    // Draw viewport rectangle
                    let vp_x = -tab.camera.offset_x / (tab.page_width * tab.camera.zoom);
                    let vp_y = -tab.camera.offset_y / (tab.page_height * tab.camera.zoom);
                    let screen = ctx.screen_rect();
                    let vp_w = screen.width() as f64 / (tab.page_width * tab.camera.zoom);
                    let vp_h = screen.height() as f64 / (tab.page_height * tab.camera.zoom);

                    let rect = response.rect;
                    let vp_rect = egui::Rect::from_min_size(
                        egui::pos2(
                            rect.min.x + vp_x as f32 * rect.width(),
                            rect.min.y + vp_y as f32 * rect.height(),
                        ),
                        egui::vec2(
                            (vp_w as f32 * rect.width()).min(rect.width()),
                            (vp_h as f32 * rect.height()).min(rect.height()),
                        ),
                    );

                    painter.rect_stroke(
                        vp_rect,
                        0.0,
                        egui::Stroke::new(
                            2.0,
                            egui::Color32::from_rgba_unmultiplied(66, 133, 244, 180),
                        ),
                        egui::StrokeKind::Outside,
                    );
                }
            } else {
                ui.label("Loading...");
            }
        });

    actions
}
