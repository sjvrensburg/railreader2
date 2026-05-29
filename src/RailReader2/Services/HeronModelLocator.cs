using RailReader.Core.Services;

namespace RailReader2.Services;

/// <summary>
/// Finds the Docling Heron ONNX file on disk. The Heron model is the default
/// layout detector (~66 MB INT8, Apache-2.0) — users download it separately.
/// See <c>docs/heron-layout-model.md</c> for the recommended install locations.
/// </summary>
public static class HeronModelLocator
{
    public const string FileName = "docling-layout-heron-int8.onnx";

    /// <summary>
    /// Legacy filename used before v3.14. Probed as a fallback so existing
    /// users who downloaded the FP32 model aren't silently downgraded.
    /// </summary>
    private const string LegacyFileName = "docling-layout-heron.onnx";

    public static string? FindModelPath()
    {
        var path = LayoutModelLocator.FindModelPath(FileName);
        if (path != null) return path;

        // Backward compat: probe for the old FP32 filename
        return LayoutModelLocator.FindModelPath(LegacyFileName);
    }
}
