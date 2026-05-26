namespace RailReader2.Services;

/// <summary>
/// Finds the Docling Heron ONNX file on disk. The Heron model is *not* shipped
/// with installers (~164 MB, Apache-2.0) — users download it separately. See
/// <c>docs/heron-layout-model.md</c> for the recommended install locations.
/// </summary>
public static class HeronModelLocator
{
    public const string FileName = "docling-layout-heron.onnx";

    public static string? FindModelPath() => ModelLocator.FindModelPath(FileName);
}
