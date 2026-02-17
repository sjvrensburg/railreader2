use crate::colour_effect::ColourEffect;
use crate::config::Config;
use crate::layout::{default_navigable_classes, LAYOUT_CLASSES};
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

            ui.add_space(16.0);
            ui.heading("Colour Effects");
            ui.separator();

            ui.horizontal(|ui| {
                ui.label("Effect:");
                let current_label = config.colour_effect.to_string();
                egui::ComboBox::from_id_salt("colour_effect_combo")
                    .selected_text(current_label)
                    .show_ui(ui, |ui| {
                        let effects = [
                            ColourEffect::None,
                            ColourEffect::HighContrast,
                            ColourEffect::HighVisibility,
                            ColourEffect::Amber,
                            ColourEffect::Invert,
                        ];
                        for effect in effects {
                            if ui
                                .selectable_label(
                                    config.colour_effect == effect,
                                    effect.to_string(),
                                )
                                .clicked()
                            {
                                config.colour_effect = effect;
                                changed = true;
                            }
                        }
                    });
            });

            ui.horizontal(|ui| {
                ui.label("Intensity:");
                changed |= ui
                    .add(egui::Slider::new(
                        &mut config.colour_effect_intensity,
                        0.0..=1.0,
                    ))
                    .changed();
            });

            ui.add_space(16.0);
            egui::CollapsingHeader::new("Advanced: Navigable Block Types")
                .default_open(false)
                .show(ui, |ui| {
                    ui.label("Select which block types are navigable in rail mode:");
                    ui.add_space(4.0);
                    for (id, &name) in LAYOUT_CLASSES.iter().enumerate() {
                        let mut enabled = config.navigable_classes.contains(&id);
                        if ui.checkbox(&mut enabled, name).changed() {
                            if enabled {
                                config.navigable_classes.insert(id);
                            } else {
                                config.navigable_classes.remove(&id);
                            }
                            changed = true;
                        }
                    }
                    ui.add_space(4.0);
                    if ui.small_button("Reset to Defaults").clicked() {
                        config.navigable_classes = default_navigable_classes();
                        changed = true;
                    }
                });

            ui.add_space(8.0);
            if ui.button("Reset All to Defaults").clicked() {
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
