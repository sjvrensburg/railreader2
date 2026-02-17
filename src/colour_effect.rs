use serde::{Deserialize, Serialize};
use skia_safe::runtime_effect::RuntimeEffect;
use skia_safe::{Color, ColorFilter, Data, Paint};
use std::fmt;

#[derive(Debug, Clone, Copy, PartialEq, Default, Serialize, Deserialize)]
pub enum ColourEffect {
    #[default]
    None,
    HighContrast,
    HighVisibility,
    Amber,
    Invert,
}

impl fmt::Display for ColourEffect {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            ColourEffect::None => write!(f, "None"),
            ColourEffect::HighContrast => write!(f, "High Contrast"),
            ColourEffect::HighVisibility => write!(f, "High Visibility"),
            ColourEffect::Amber => write!(f, "Amber Filter"),
            ColourEffect::Invert => write!(f, "Invert"),
        }
    }
}

/// Canonical list of all colour effects with descriptions for UI display.
pub const COLOUR_EFFECTS: &[(ColourEffect, &str)] = &[
    (ColourEffect::None, "No colour effect"),
    (ColourEffect::HighContrast, "White on black for glare reduction"),
    (ColourEffect::HighVisibility, "Yellow on black for maximum legibility"),
    (ColourEffect::Amber, "Warm amber tint for haze reduction"),
    (ColourEffect::Invert, "Invert colours for eye strain relief"),
];

/// Colours used by rail-mode overlays, adapted per colour effect so they
/// complement rather than fight the filtered content.
pub struct OverlayPalette {
    /// Semi-transparent fill drawn over the entire page to de-emphasise non-active blocks.
    pub dim: Color,
    /// Additive/clear colour drawn over the active block to reveal it. When `None`,
    /// the block reveal step is skipped (outline-only).
    pub block_reveal: Option<(Color, skia_safe::BlendMode)>,
    /// Stroke colour for the active block outline.
    pub block_outline: Color,
    /// Stroke width for the active block outline.
    pub block_outline_width: f32,
    /// Fill colour for the current-line highlight band.
    pub line_highlight: Color,
}

impl ColourEffect {
    /// Return an overlay palette tuned for this colour effect.
    pub fn overlay_palette(&self) -> OverlayPalette {
        match self {
            ColourEffect::None => OverlayPalette {
                // Original behaviour: black dim, white additive reveal, soft blue accents
                dim: Color::from_argb(120, 0, 0, 0),
                block_reveal: Some((
                    Color::from_argb(120, 255, 255, 255),
                    skia_safe::BlendMode::Plus,
                )),
                block_outline: Color::from_argb(80, 66, 133, 244),
                block_outline_width: 1.5,
                line_highlight: Color::from_argb(40, 66, 133, 244),
            },
            ColourEffect::HighContrast => OverlayPalette {
                // Dark backgrounds: light dim to grey out surroundings, no additive reveal,
                // bright cyan outline + line highlight for maximum contrast
                dim: Color::from_argb(100, 60, 60, 60),
                block_reveal: None,
                block_outline: Color::from_argb(200, 0, 255, 255),
                block_outline_width: 2.5,
                line_highlight: Color::from_argb(50, 0, 255, 255),
            },
            ColourEffect::HighVisibility => OverlayPalette {
                // Yellow-on-black: dim with dark, bright yellow accents
                dim: Color::from_argb(100, 40, 40, 0),
                block_reveal: None,
                block_outline: Color::from_argb(200, 255, 230, 0),
                block_outline_width: 2.5,
                line_highlight: Color::from_argb(45, 255, 230, 0),
            },
            ColourEffect::Amber => OverlayPalette {
                // Warm tint: keep original structure with amber-shifted accents
                dim: Color::from_argb(110, 20, 10, 0),
                block_reveal: Some((
                    Color::from_argb(100, 255, 220, 160),
                    skia_safe::BlendMode::Plus,
                )),
                block_outline: Color::from_argb(120, 255, 180, 60),
                block_outline_width: 1.5,
                line_highlight: Color::from_argb(35, 255, 180, 60),
            },
            ColourEffect::Invert => OverlayPalette {
                // Inverted: light dim, no additive reveal, green accent (stands out on inverted)
                dim: Color::from_argb(100, 60, 60, 60),
                block_reveal: None,
                block_outline: Color::from_argb(180, 0, 220, 120),
                block_outline_width: 2.0,
                line_highlight: Color::from_argb(40, 0, 220, 120),
            },
        }
    }
}

const HIGH_CONTRAST_SKSL: &str = r#"
uniform float intensity;
half4 main(half4 color) {
    half lum = dot(color.rgb, half3(0.299, 0.587, 0.114));
    half inv = 1.0 - lum;
    half c = inv < 0.5 ? 2.0 * inv * inv : 1.0 - 2.0 * (1.0 - inv) * (1.0 - inv);
    half3 effect = half3(c, c, c);
    half3 result = mix(color.rgb, effect, half(intensity));
    return half4(result, color.a);
}
"#;

const HIGH_VISIBILITY_SKSL: &str = r#"
uniform float intensity;
half4 main(half4 color) {
    half lum = dot(color.rgb, half3(0.299, 0.587, 0.114));
    half inv = 1.0 - lum;
    half3 effect = half3(inv, inv, 0.0);
    half3 result = mix(color.rgb, effect, half(intensity));
    return half4(result, color.a);
}
"#;

const AMBER_SKSL: &str = r#"
uniform float intensity;
half4 main(half4 color) {
    half3 tinted = clamp(color.rgb * half3(1.15, 1.05, 0.75), half3(0.0), half3(1.0));
    half3 result = mix(color.rgb, tinted, half(intensity));
    return half4(result, color.a);
}
"#;

const INVERT_SKSL: &str = r#"
uniform float intensity;
half4 main(half4 color) {
    half3 effect = 1.0 - color.rgb;
    half3 result = mix(color.rgb, effect, half(intensity));
    return half4(result, color.a);
}
"#;

impl Default for ColourEffectState {
    fn default() -> Self {
        Self::new()
    }
}

pub struct ColourEffectState {
    pub effect: ColourEffect,
    pub intensity: f32,
    high_contrast: Option<RuntimeEffect>,
    high_visibility: Option<RuntimeEffect>,
    amber: Option<RuntimeEffect>,
    invert: Option<RuntimeEffect>,
}

impl ColourEffectState {
    pub fn new() -> Self {
        let compile = |name: &str, sksl: &str| -> Option<RuntimeEffect> {
            match RuntimeEffect::make_for_color_filter(sksl, None) {
                Ok(effect) => Some(effect),
                Err(e) => {
                    log::warn!("Failed to compile {} shader: {}", name, e);
                    None
                }
            }
        };

        Self {
            effect: ColourEffect::None,
            intensity: 1.0,
            high_contrast: compile("HighContrast", HIGH_CONTRAST_SKSL),
            high_visibility: compile("HighVisibility", HIGH_VISIBILITY_SKSL),
            amber: compile("Amber", AMBER_SKSL),
            invert: compile("Invert", INVERT_SKSL),
        }
    }

    fn runtime_effect(&self) -> Option<&RuntimeEffect> {
        match self.effect {
            ColourEffect::None => None,
            ColourEffect::HighContrast => self.high_contrast.as_ref(),
            ColourEffect::HighVisibility => self.high_visibility.as_ref(),
            ColourEffect::Amber => self.amber.as_ref(),
            ColourEffect::Invert => self.invert.as_ref(),
        }
    }

    pub fn has_active_effect(&self) -> bool {
        self.effect != ColourEffect::None && self.runtime_effect().is_some()
    }

    pub fn create_color_filter(&self) -> Option<ColorFilter> {
        let rt = self.runtime_effect()?;
        let data = Data::new_copy(&self.intensity.to_ne_bytes());
        rt.make_color_filter(data, None)
    }

    pub fn create_paint(&self) -> Option<Paint> {
        let filter = self.create_color_filter()?;
        let mut paint = Paint::default();
        paint.set_color_filter(filter);
        Some(paint)
    }
}
