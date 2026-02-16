use crate::tab::TabState;

pub fn show_status_bar(ctx: &egui::Context, tabs: &[TabState], active_tab: usize) {
    egui::TopBottomPanel::bottom("status_bar").show(ctx, |ui| {
        ui.horizontal(|ui| {
            if let Some(tab) = tabs.get(active_tab) {
                let zoom_pct = (tab.camera.zoom * 100.0).round() as i32;
                ui.label(format!("Page {}/{}", tab.current_page + 1, tab.page_count));
                ui.separator();
                ui.label(format!("Zoom: {}%", zoom_pct));

                if tab.rail.active {
                    ui.separator();
                    ui.label(format!(
                        "Block {}/{} | Line {}/{}",
                        tab.rail.current_block + 1,
                        tab.rail.navigable_count(),
                        tab.rail.current_line + 1,
                        tab.rail.current_line_count(),
                    ));
                    ui.separator();
                    ui.colored_label(egui::Color32::from_rgb(66, 133, 244), "Rail Mode");
                }
            } else {
                ui.label("No document open");
            }
        });
    });
}
