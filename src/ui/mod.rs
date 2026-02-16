pub mod icons;
pub mod loading;
pub mod menu;
pub mod minimap;
pub mod outline;
pub mod settings;
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
    FitPage,
    ToggleDebug,
    ToggleOutline,
    ToggleMinimap,
    Quit,
    None,
}

pub struct UiState {
    pub show_outline: bool,
    pub show_minimap: bool,
    pub show_settings: bool,
    pub content_rect: egui::Rect,
}

impl Default for UiState {
    fn default() -> Self {
        Self {
            show_outline: false,
            show_minimap: false,
            show_settings: false,
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
    config: &crate::config::Config,
) -> Vec<UiAction> {
    let mut actions = Vec::new();

    // Menu bar
    actions.extend(menu::show_menu_bar(ctx, ui_state, tabs, active_tab));

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
        settings::show_settings_window(ctx, &mut ui_state.show_settings, config, tab);
    }

    // Minimap
    if let Some(tab) = tabs.get(active_tab) {
        minimap::show_minimap(ctx, &mut ui_state.show_minimap, tab);
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
