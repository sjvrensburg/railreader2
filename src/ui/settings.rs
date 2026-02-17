use crate::config::Config;
use crate::tab::TabState;
use crate::ui::UiAction;

pub fn show_settings_window(
    ctx: &egui::Context,
    show: &mut bool,
    config: &mut Config,
    tab: &TabState,
) -> Vec<UiAction> {
    let mut actions = Vec::new();

    egui::Window::new("Settings")
        .open(show)
        .resizable(false)
        .default_width(300.0)
        .show(ctx, |ui| {
            ui.heading("Rail Reading");
            ui.separator();

            let mut changed = false;

            ui.horizontal(|ui| {
                ui.label("Zoom threshold:");
                changed |= ui
                    .add(
                        egui::DragValue::new(&mut config.rail_zoom_threshold)
                            .range(1.0..=15.0)
                            .speed(0.1)
                            .suffix("x"),
                    )
                    .changed();
            });

            ui.horizontal(|ui| {
                ui.label("Snap duration:");
                changed |= ui
                    .add(
                        egui::DragValue::new(&mut config.snap_duration_ms)
                            .range(50.0..=1000.0)
                            .speed(10.0)
                            .suffix(" ms"),
                    )
                    .changed();
            });

            ui.horizontal(|ui| {
                ui.label("Scroll speed start:");
                changed |= ui
                    .add(
                        egui::DragValue::new(&mut config.scroll_speed_start)
                            .range(10.0..=500.0)
                            .speed(5.0),
                    )
                    .changed();
            });

            ui.horizontal(|ui| {
                ui.label("Scroll speed max:");
                changed |= ui
                    .add(
                        egui::DragValue::new(&mut config.scroll_speed_max)
                            .range(10.0..=500.0)
                            .speed(5.0),
                    )
                    .changed();
            });

            ui.horizontal(|ui| {
                ui.label("Ramp time:");
                changed |= ui
                    .add(
                        egui::DragValue::new(&mut config.scroll_ramp_time)
                            .range(0.1..=5.0)
                            .speed(0.1)
                            .suffix(" s"),
                    )
                    .changed();
            });

            ui.horizontal(|ui| {
                ui.label("Lookahead pages:");
                let mut val = config.analysis_lookahead_pages as i32;
                if ui
                    .add(egui::DragValue::new(&mut val).range(0..=10))
                    .changed()
                {
                    config.analysis_lookahead_pages = val as usize;
                    changed = true;
                }
            });

            ui.add_space(8.0);
            if ui.button("Reset to Defaults").clicked() {
                *config = Config::default();
                changed = true;
            }

            if changed {
                config.save();
                actions.push(UiAction::ConfigChanged);
            }

            ui.separator();
            ui.heading("Document Info");
            ui.label(format!("File: {}", tab.file_path));
            ui.label(format!("Pages: {}", tab.page_count));
            ui.label(format!(
                "Page size: {:.0} x {:.0} pts",
                tab.page_width, tab.page_height
            ));
        });

    actions
}
