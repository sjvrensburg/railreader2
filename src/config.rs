use crate::colour_effect::ColourEffect;
use crate::layout::{default_navigable_classes, LAYOUT_CLASSES};
use serde::{Deserialize, Serialize};
use std::collections::HashSet;
use std::path::PathBuf;

/// User-configurable parameters for rail reading.
/// Stored in the platform config directory (`$XDG_CONFIG_HOME/railreader2/` or `%APPDATA%\railreader2\`).
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default)]
pub struct Config {
    /// Zoom level at which rail mode activates.
    pub rail_zoom_threshold: f64,
    /// Duration of snap animations in milliseconds.
    pub snap_duration_ms: f64,
    /// Horizontal scroll speed at start of hold (page-coord points per second).
    pub scroll_speed_start: f64,
    /// Maximum horizontal scroll speed after holding (page-coord points per second).
    pub scroll_speed_max: f64,
    /// Time in seconds to reach max scroll speed from start.
    pub scroll_ramp_time: f64,
    /// Number of pages ahead to pre-analyze for layout (0 = disabled).
    pub analysis_lookahead_pages: usize,
    /// Colour effect applied to PDF content for visual impairment accessibility.
    pub colour_effect: ColourEffect,
    /// Intensity of the colour effect (0.0–1.0).
    pub colour_effect_intensity: f64,
    /// UI font scale multiplier (0.75–2.0). Scales all egui text sizes.
    pub ui_font_scale: f32,
    /// Which layout block classes are navigable in rail mode.
    #[serde(
        serialize_with = "serialize_navigable_classes",
        deserialize_with = "deserialize_navigable_classes"
    )]
    pub navigable_classes: HashSet<usize>,
}

fn serialize_navigable_classes<S: serde::Serializer>(
    classes: &HashSet<usize>,
    serializer: S,
) -> Result<S::Ok, S::Error> {
    let mut names: Vec<&str> = classes
        .iter()
        .filter_map(|&id| LAYOUT_CLASSES.get(id).copied())
        .collect();
    names.sort();
    names.serialize(serializer)
}

fn deserialize_navigable_classes<'de, D: serde::Deserializer<'de>>(
    deserializer: D,
) -> Result<HashSet<usize>, D::Error> {
    let names: Vec<String> = Vec::deserialize(deserializer)?;
    Ok(names
        .iter()
        .filter_map(|name| LAYOUT_CLASSES.iter().position(|&c| c == name.as_str()))
        .collect())
}

impl Default for Config {
    fn default() -> Self {
        Self {
            rail_zoom_threshold: 3.0,
            snap_duration_ms: 300.0,
            scroll_speed_start: 10.0,
            scroll_speed_max: 50.0,
            scroll_ramp_time: 1.5,
            analysis_lookahead_pages: 2,
            ui_font_scale: 1.0,
            colour_effect: ColourEffect::None,
            colour_effect_intensity: 1.0,
            navigable_classes: default_navigable_classes(),
        }
    }
}

impl Config {
    /// Load config from `config.json` in the working directory, or return defaults.
    pub fn load() -> Self {
        let path = config_path();
        match std::fs::read_to_string(&path) {
            Ok(contents) => match serde_json::from_str(&contents) {
                Ok(config) => {
                    log::info!("Loaded config from {}", path.display());
                    config
                }
                Err(e) => {
                    log::warn!("Failed to parse {}: {}, using defaults", path.display(), e);
                    Self::default()
                }
            },
            Err(_) => {
                log::info!(
                    "No config file at {}, using defaults. Creating default config.",
                    path.display()
                );
                let config = Self::default();
                config.save();
                config
            }
        }
    }

    /// Save current config to `config.json`.
    pub fn save(&self) {
        let path = config_path();
        match serde_json::to_string_pretty(self) {
            Ok(json) => {
                if let Err(e) = std::fs::write(&path, json) {
                    log::warn!("Failed to write config to {}: {}", path.display(), e);
                }
            }
            Err(e) => {
                log::warn!("Failed to serialize config: {}", e);
            }
        }
    }
}

fn config_path() -> PathBuf {
    let dir = dirs::config_dir()
        .unwrap_or_else(|| PathBuf::from("."))
        .join("railreader2");
    if !dir.exists() {
        std::fs::create_dir_all(&dir).ok();
    }
    dir.join("config.json")
}
