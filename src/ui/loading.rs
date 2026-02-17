pub fn show_loading_overlay(ctx: &egui::Context) {
    egui::Area::new(egui::Id::from("loading_overlay"))
        .fixed_pos(egui::pos2(0.0, 0.0))
        .interactable(false)
        .show(ctx, |ui| {
            let screen = ui.ctx().screen_rect();
            let painter = ui.painter();

            // Semi-transparent background
            painter.rect_filled(
                screen,
                0.0,
                egui::Color32::from_rgba_unmultiplied(0, 0, 0, 120),
            );

            // Center spinner + text
            ui.allocate_new_ui(egui::UiBuilder::new().max_rect(screen), |ui| {
                ui.vertical_centered(|ui| {
                    let center_y = screen.height() / 2.0 - 20.0;
                    ui.add_space(center_y);
                    ui.spinner();
                    ui.label("Analyzing layout...");
                });
            });
        });
}
