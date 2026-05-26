using RailReader.Core;
using RailReader.Core.Services;

namespace RailReader2.Services;

/// <summary>
/// Finds the PP-DocLayout-S ONNX file on disk. Mirrors the probe order of
/// <see cref="LayoutModelLocator"/> (PP-DocLayoutV3) and
/// <see cref="HeronModelLocator"/>, but looks for
/// <c>pp_doclayout_s.onnx</c>. The PP-S model is *not* shipped with installers
/// (~4.7 MB, Apache-2.0) — users download it separately. See
/// <c>docs/pp-doclayout-s.md</c> for the recommended install locations and the
/// upstream HuggingFace URL.
/// </summary>
public static class PPDocLayoutSModelLocator
{
    public const string FileName = "pp_doclayout_s.onnx";

    /// <summary>
    /// Probe locations in priority order. The first that exists wins.
    /// </summary>
    public static IEnumerable<string> ProbePaths()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "models", FileName);

        var appDir = Environment.GetEnvironmentVariable("APPDIR");
        if (!string.IsNullOrEmpty(appDir))
            yield return Path.Combine(appDir, "models", FileName);

        yield return Path.Combine(AppConfig.ConfigDir, "models", FileName);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
            yield return Path.Combine(localAppData, "railreader2", "models", FileName);

        yield return Path.Combine(Directory.GetCurrentDirectory(), "models", FileName);

        var cwd = Directory.GetCurrentDirectory();
        for (int up = 1; up <= 3; up++)
        {
            var parent = Directory.GetParent(cwd)?.FullName;
            if (parent is null) break;
            yield return Path.Combine(parent, "models", FileName);
            cwd = parent;
        }
    }

    /// <summary>Returns the first existing probe path, or <c>null</c> if none.</summary>
    public static string? FindModelPath()
    {
        foreach (var p in ProbePaths())
        {
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
