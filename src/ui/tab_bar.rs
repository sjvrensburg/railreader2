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

                // Close button
                if ui.small_button("\u{2715}").clicked() {
                    actions.push(UiAction::CloseTab(i));
                }

                ui.separator();
            }
        });
    });

    actions
}
