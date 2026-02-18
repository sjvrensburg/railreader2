pub mod about;
pub mod loading;
pub mod menu;
pub mod minimap;
pub mod outline;
pub mod settings;
pub mod shortcuts;
pub mod status_bar;
pub mod tab_bar;

use crate::tab::TabState;

#[derive(Debug, Clone)]
pub enum UiAction {
    OpenFile,
    CloseTab(usize),
    SelectTab(usize),
    DuplicateTab,
    GoToPage(i32),
    SetZoom(f64),
    SetCamera(f64, f64),
    FitPage,
    ToggleDebug,
    ToggleOutline,
    ToggleMinimap,
    SetColourEffect(crate::colour_effect::ColourEffect),
    ConfigChanged,
    RunCleanup,
    Quit,
}

pub struct UiState {
    pub show_outline: bool,
    pub show_minimap: bool,
    pub show_settings: bool,
    pub show_about: bool,
    pub show_shortcuts: bool,
    pub cleanup_message: Option<String>,
    pub content_rect: egui::Rect,
}

impl Default for UiState {
    fn default() -> Self {
        Self {
            show_outline: false,
            show_minimap: false,
            show_settings: false,
            show_about: false,
            show_shortcuts: false,
            cleanup_message: None,
            content_rect: egui::Rect::EVERYTHING,
        }
    }
}

/// Build the entire egui UI. Returns a list of actions to process.
pub fn build_ui(
    ctx: &egui::Context,
    ui_state: &mut UiState,
    tabs: &[TabState],
    active_tab: usize,
    config: &mut crate::config::Config,
) -> Vec<UiAction> {
    let mut actions = Vec::new();

    // Menu bar
    actions.extend(menu::show_menu_bar(ctx, ui_state, tabs, active_tab, config));

    // Tab bar
    actions.extend(tab_bar::show_tab_bar(ctx, tabs, active_tab));

    // Status bar
    status_bar::show_status_bar(ctx, tabs, active_tab);

    // Outline panel (left side)
    if let Some(tab) = tabs.get(active_tab) {
        actions.extend(outline::show_outline_panel(
            ctx,
            &mut ui_state.show_outline,
            tab,
        ));
    }

    // Settings window
    if let Some(tab) = tabs.get(active_tab) {
        actions.extend(settings::show_settings_window(
            ctx,
            &mut ui_state.show_settings,
            config,
            tab,
        ));
    }

    // Minimap
    if let Some(tab) = tabs.get(active_tab) {
        actions.extend(minimap::show_minimap(ctx, &mut ui_state.show_minimap, tab));
    }

    // About window
    about::show_about_window(ctx, &mut ui_state.show_about);

    // Shortcuts window
    shortcuts::show_shortcuts_window(ctx, &mut ui_state.show_shortcuts);

    // Cleanup notification toast
    if let Some(msg) = &ui_state.cleanup_message {
        let mut dismiss = false;
        egui::Window::new("Cleanup")
            .collapsible(false)
            .resizable(false)
            .anchor(egui::Align2::CENTER_CENTER, [0.0, 0.0])
            .show(ctx, |ui| {
                ui.label(msg.as_str());
                if ui.button("OK").clicked() {
                    dismiss = true;
                }
            });
        if dismiss {
            ui_state.cleanup_message = None;
        }
    }

    // Loading overlay
    if let Some(tab) = tabs.get(active_tab) {
        if tab.pending_page_load.is_some() {
            loading::show_loading_overlay(ctx);
        }
    }

    // Capture the remaining content rect after all panels
    ui_state.content_rect = ctx.available_rect();

    actions
}
