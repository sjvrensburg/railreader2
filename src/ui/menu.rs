use crate::colour_effect::COLOUR_EFFECTS;
use crate::config::Config;
use crate::tab::TabState;
use crate::ui::{UiAction, UiState};

pub fn show_menu_bar(
    ctx: &egui::Context,
    ui_state: &mut UiState,
    tabs: &[TabState],
    active_tab: usize,
    config: &Config,
) -> Vec<UiAction> {
    let mut actions = Vec::new();

    egui::TopBottomPanel::top("menu_bar").show(ctx, |ui| {
        egui::menu::bar(ui, |ui| {
            // File menu
            ui.menu_button("File", |ui| {
                if ui
                    .add(egui::Button::new("Open...").shortcut_text("Ctrl+O"))
                    .clicked()
                {
                    actions.push(UiAction::OpenFile);
                    ui.close_menu();
                }
                if ui.add(egui::Button::new("Duplicate Tab")).clicked() {
                    actions.push(UiAction::DuplicateTab);
                    ui.close_menu();
                }
                ui.separator();
                let has_tab = !tabs.is_empty();
                if ui
                    .add_enabled(
                        has_tab,
                        egui::Button::new("Close Tab").shortcut_text("Ctrl+W"),
                    )
                    .clicked()
                {
                    actions.push(UiAction::CloseTab(active_tab));
                    ui.close_menu();
                }
                ui.separator();
                if ui
                    .add(egui::Button::new("Quit").shortcut_text("Ctrl+Q"))
                    .clicked()
                {
                    actions.push(UiAction::Quit);
                    ui.close_menu();
                }
            });

            // View menu
            ui.menu_button("View", |ui| {
                if let Some(tab) = tabs.get(active_tab) {
                    if ui
                        .add(egui::Button::new("Zoom In").shortcut_text("+"))
                        .clicked()
                    {
                        actions.push(UiAction::SetZoom(tab.camera.zoom * 1.25));
                        ui.close_menu();
                    }
                    if ui
                        .add(egui::Button::new("Zoom Out").shortcut_text("-"))
                        .clicked()
                    {
                        actions.push(UiAction::SetZoom(tab.camera.zoom / 1.25));
                        ui.close_menu();
                    }
                    if ui.button("Fit Page").clicked() {
                        actions.push(UiAction::FitPage);
                        ui.close_menu();
                    }
                }
                ui.separator();
                if ui.checkbox(&mut ui_state.show_outline, "Outline").clicked() {
                    ui.close_menu();
                }
                if ui.checkbox(&mut ui_state.show_minimap, "Minimap").clicked() {
                    ui.close_menu();
                }
                ui.separator();
                let mut debug = tabs
                    .get(active_tab)
                    .map(|t| t.debug_overlay)
                    .unwrap_or(false);
                if ui.checkbox(&mut debug, "Debug Overlay").clicked() {
                    actions.push(UiAction::ToggleDebug);
                    ui.close_menu();
                }
                ui.separator();
                ui.menu_button("Colour Effects", |ui| {
                    for &(effect, hover) in COLOUR_EFFECTS {
                        let selected = config.colour_effect == effect;
                        let resp = ui.selectable_label(selected, effect.to_string());
                        if resp.on_hover_text(hover).clicked() {
                            actions.push(UiAction::SetColourEffect(effect));
                            ui.close_menu();
                        }
                    }
                });
            });

            // Navigation menu
            ui.menu_button("Navigation", |ui| {
                if let Some(tab) = tabs.get(active_tab) {
                    if ui
                        .add_enabled(
                            tab.current_page > 0,
                            egui::Button::new("Previous Page").shortcut_text("PgUp"),
                        )
                        .clicked()
                    {
                        actions.push(UiAction::GoToPage(tab.current_page - 1));
                        ui.close_menu();
                    }
                    if ui
                        .add_enabled(
                            tab.current_page < tab.page_count - 1,
                            egui::Button::new("Next Page").shortcut_text("PgDn"),
                        )
                        .clicked()
                    {
                        actions.push(UiAction::GoToPage(tab.current_page + 1));
                        ui.close_menu();
                    }
                    ui.separator();
                    if ui.button("First Page").clicked() {
                        actions.push(UiAction::GoToPage(0));
                        ui.close_menu();
                    }
                    if ui.button("Last Page").clicked() {
                        actions.push(UiAction::GoToPage(tab.page_count - 1));
                        ui.close_menu();
                    }
                }
            });

            // Help menu
            ui.menu_button("Help", |ui| {
                if ui
                    .add(egui::Button::new("Keyboard Shortcuts").shortcut_text("F1"))
                    .clicked()
                {
                    ui_state.show_shortcuts = true;
                    ui.close_menu();
                }
                if ui.button("About").clicked() {
                    ui_state.show_about = true;
                    ui.close_menu();
                }
                ui.separator();
                if ui.button("Clean Up Temp Files...").clicked() {
                    actions.push(UiAction::RunCleanup);
                    ui.close_menu();
                }
            });

            // Settings button (right side)
            ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                if ui.button("\u{2699}").clicked() {
                    ui_state.show_settings = !ui_state.show_settings;
                }
            });
        });
    });

    actions
}
