namespace RailReader.Core;

/// <summary>
/// Internal tuning constants not exposed to users via AppConfig.
/// User-tunable values live in AppConfig; ONNX-specific values live in LayoutConstants.
/// This file collects internal thresholds that control UX feel but aren't meant to be
/// end-user configurable.
/// </summary>
internal static class CoreTuning
{
    // Navigation
    public const double PanStep = 50.0;
    public const double DestMarginTop = 0.1;
    public const double DestMarginLeft = 0.05;

    // Rail centering
    public const double CenterBlockThreshold = 0.75;

    // Edge-hold state machine
    public const double EdgeHoldMs = 400.0;
    public const double EdgeCooldownMs = 300.0;
}
