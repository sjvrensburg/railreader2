namespace RailReader.Core.Models;

/// <summary>
/// Immutable snapshot of runtime tuning values that Core's services and
/// controllers consume. UI-only fields (font scale, dark mode, minimap
/// dimensions, recent files) live in the consumer's persistent config and
/// are not part of this contract.
///
/// When settings change at the UI layer, the UI rebuilds a new
/// <see cref="CoreSettings"/> and pushes it via the controller's update path
/// (e.g. <c>RailNav.UpdateConfig</c>).
/// </summary>
public sealed record CoreSettings
{
    // Rail / zoom
    public double RailZoomThreshold { get; init; } = 3.0;
    public double SnapDurationMs { get; init; } = 300.0;
    public double LinePadding { get; init; } = 0.2;
    public double JumpPercentage { get; init; } = 25.0;

    // Hold-to-scroll
    public double ScrollSpeedStart { get; init; } = 14.0;
    public double ScrollSpeedMax { get; init; } = 42.0;
    public double ScrollRampTime { get; init; } = 1.5;
    public double DefaultAutoScrollSpeed => (ScrollSpeedStart + ScrollSpeedMax) / 2.0;

    // Auto-scroll
    public double AutoScrollLinePauseMs { get; init; } = 400.0;
    public double AutoScrollBlockPauseMs { get; init; } = 600.0;
    public double AutoScrollEquationPauseMs { get; init; } = 600.0;
    public double AutoScrollHeaderPauseMs { get; init; } = 600.0;
    public bool AutoScrollTriggerEnabled { get; init; }
    public double AutoScrollTriggerDelayMs { get; init; } = 2000.0;

    // Analysis
    public int AnalysisLookaheadPages { get; init; } = 2;
    public IReadOnlySet<int> NavigableClasses { get; init; } = Services.LayoutConstants.DefaultNavigableClasses();
    public IReadOnlySet<int> CenteringClasses { get; init; } = Services.LayoutConstants.DefaultCenteringClasses();

    // Visual effects (per-document defaults — UI may override per doc)
    public ColourEffect ColourEffect { get; init; } = ColourEffect.None;
    public double ColourEffectIntensity { get; init; } = 1.0;
    public bool MotionBlur { get; init; } = true;
    public double MotionBlurIntensity { get; init; } = 0.33;
    public bool PixelSnapping { get; init; } = true;
    public bool LineFocusBlur { get; init; }
    public double LineFocusBlurIntensity { get; init; } = 0.5;
    public bool LineHighlightEnabled { get; init; } = true;
    public LineHighlightTint LineHighlightTint { get; init; } = LineHighlightTint.Auto;
    public double LineHighlightOpacity { get; init; } = 0.25;
    public bool MarginCropping { get; init; }

    // VLM (vision-language model) — empty endpoint disables
    public string? VlmEndpoint { get; init; }
    public string? VlmModel { get; init; }
    public string? VlmApiKey { get; init; }
    public bool VlmStructuredOutput { get; init; }
}
