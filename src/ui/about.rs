pub fn show_about_window(ctx: &egui::Context, show: &mut bool) {
    egui::Window::new("About railreader2")
        .open(show)
        .resizable(false)
        .collapsible(false)
        .default_width(300.0)
        .anchor(egui::Align2::CENTER_CENTER, [0.0, 0.0])
        .show(ctx, |ui| {
            ui.vertical_centered(|ui| {
                ui.heading("railreader2");
                ui.label(format!("Version {}", env!("CARGO_PKG_VERSION")));
                ui.add_space(8.0);
                ui.label("AI-guided rail reading PDF viewer");
                ui.add_space(8.0);
                ui.separator();
                ui.add_space(4.0);
                ui.label("Built with MuPDF, Skia, ONNX Runtime, and egui");
                ui.label("Layout detection: PP-DocLayoutV3");
            });
        });
}
