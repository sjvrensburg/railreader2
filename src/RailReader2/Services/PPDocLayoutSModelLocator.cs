using RailReader.Core.Services;

namespace RailReader2.Services;

/// <summary>
/// Finds the PP-DocLayout-S ONNX file on disk. The PP-S model is *not* shipped
/// with installers (~4.7 MB, Apache-2.0) — users download it separately. See
/// <c>docs/pp-doclayout-s.md</c> for the recommended install locations and the
/// upstream HuggingFace URL.
/// </summary>
public static class PPDocLayoutSModelLocator
{
    public const string FileName = "pp_doclayout_s.onnx";

    public static string? FindModelPath() => LayoutModelLocator.FindModelPath(FileName);
}
