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

                // Styled close button with hover highlight
                let close_btn = ui.add(
                    egui::Button::new(egui::RichText::new("\u{2715}").size(11.0))
                        .frame(false)
                        .min_size(egui::vec2(20.0, 20.0)),
                );
                if close_btn.hovered() {
                    let painter = ui.painter();
                    painter.circle_filled(
                        close_btn.rect.center(),
                        10.0,
                        egui::Color32::from_rgba_unmultiplied(128, 128, 128, 60),
                    );
                }
                if close_btn.clicked() {
                    actions.push(UiAction::CloseTab(i));
                }

                ui.separator();
            }
        });
    });

    actions
}
