use crate::config::Config;
use crate::tab::TabState;

pub fn show_settings_window(ctx: &egui::Context, show: &mut bool, config: &Config, tab: &TabState) {
    // We need a mutable copy to edit — actual saving is handled by the caller
    // For now we show the current values as read-only info
    egui::Window::new("Settings")
        .open(show)
        .resizable(false)
        .default_width(300.0)
        .show(ctx, |ui| {
            ui.heading("Rail Reading");
            ui.separator();

            ui.label(format!(
                "Zoom threshold: {:.1}x",
                config.rail_zoom_threshold
            ));
            ui.label(format!("Snap duration: {:.0}ms", config.snap_duration_ms));
            ui.label(format!(
                "Scroll speed: {:.0} - {:.0}",
                config.scroll_speed_start, config.scroll_speed_max
            ));
            ui.label(format!("Ramp time: {:.1}s", config.scroll_ramp_time));

            ui.separator();
            ui.heading("Document Info");
            ui.label(format!("File: {}", tab.file_path));
            ui.label(format!("Pages: {}", tab.page_count));
            ui.label(format!(
                "Page size: {:.0} x {:.0} pts",
                tab.page_width, tab.page_height
            ));
        });
}
