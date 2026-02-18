use crate::tab::TabState;
use crate::ui::UiAction;

pub fn show_tab_bar(ctx: &egui::Context, tabs: &[TabState], active_tab: usize) -> Vec<UiAction> {
    let mut actions = Vec::new();

    if tabs.is_empty() {
        return actions;
    }

    egui::TopBottomPanel::top("tab_bar").show(ctx, |ui| {
        ui.horizontal(|ui| {
            for (i, tab) in tabs.iter().enumerate() {
                let selected = i == active_tab;

                let response = ui.selectable_label(selected, &tab.title);
                if response.clicked() && !selected {
                    actions.push(UiAction::SelectTab(i));
                }

                ui.add_space(2.0);

                // Styled close button: custom-painted × with hover highlight
                let btn_size = egui::vec2(16.0, 16.0);
                let (rect, close_btn) =
                    ui.allocate_exact_size(btn_size, egui::Sense::click());
                if close_btn.hovered() {
                    ui.painter().circle_filled(
                        rect.center(),
                        8.0,
                        egui::Color32::from_rgba_unmultiplied(128, 128, 128, 60),
                    );
                }
                let stroke_color = if close_btn.hovered() {
                    egui::Color32::from_gray(200)
                } else {
                    egui::Color32::from_gray(140)
                };
                let stroke = egui::Stroke::new(1.5, stroke_color);
                let m = 4.0; // margin from edge
                ui.painter().line_segment(
                    [rect.left_top() + egui::vec2(m, m), rect.right_bottom() - egui::vec2(m, m)],
                    stroke,
                );
                ui.painter().line_segment(
                    [rect.right_top() + egui::vec2(-m, m), rect.left_bottom() + egui::vec2(m, -m)],
                    stroke,
                );
                if close_btn.clicked() {
                    actions.push(UiAction::CloseTab(i));
                }

                ui.separator();
            }
        });
    });

    actions
}
