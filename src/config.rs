use serde::{Deserialize, Serialize};
use std::path::PathBuf;

/// User-configurable parameters for rail reading.
/// Loaded from `config.json` next to the executable or in the working directory.
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
}

impl Default for Config {
    fn default() -> Self {
        Self {
            rail_zoom_threshold: 3.0,
            snap_duration_ms: 300.0,
            scroll_speed_start: 60.0,
            scroll_speed_max: 400.0,
            scroll_ramp_time: 1.5,
            analysis_lookahead_pages: 2,
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
    PathBuf::from("config.json")
}
