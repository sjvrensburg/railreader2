use crate::tab::{Outline, TabState};
use crate::ui::UiAction;

pub fn show_outline_panel(
    ctx: &egui::Context,
    show_outline: &mut bool,
    tab: &TabState,
) -> Vec<UiAction> {
    let mut actions = Vec::new();

    egui::SidePanel::left("outline")
        .resizable(true)
        .default_width(200.0)
        .min_width(150.0)
        .show_animated(ctx, *show_outline, |ui| {
            ui.heading("Outline");
            ui.separator();

            if tab.outline.is_empty() {
                ui.label("No outline available");
            } else {
                egui::ScrollArea::vertical().show(ui, |ui| {
                    render_outline_entries(ui, &tab.outline, &mut actions);
                });
            }
        });

    actions
}

fn outline_label(entry: &Outline) -> String {
    if let Some(page) = entry.page {
        format!("{} (p.{})", entry.title, page + 1)
    } else {
        entry.title.clone()
    }
}

fn outline_link(ui: &mut egui::Ui, entry: &Outline, actions: &mut Vec<UiAction>) {
    if ui.link(outline_label(entry)).clicked() {
        if let Some(page) = entry.page {
            actions.push(UiAction::GoToPage(page));
        }
    }
}

fn render_outline_entries(ui: &mut egui::Ui, entries: &[Outline], actions: &mut Vec<UiAction>) {
    for entry in entries {
        if entry.children.is_empty() {
            outline_link(ui, entry, actions);
        } else {
            let id = ui.make_persistent_id(&entry.title);
            egui::collapsing_header::CollapsingState::load_with_default_open(ui.ctx(), id, false)
                .show_header(ui, |ui| {
                    outline_link(ui, entry, actions);
                })
                .body(|ui| {
                    render_outline_entries(ui, &entry.children, actions);
                });
        }
    }
}
