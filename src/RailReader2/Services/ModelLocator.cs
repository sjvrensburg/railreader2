using RailReader.Core;
using RailReader.Core.Services;

namespace RailReader2.Services;

/// <summary>
/// Shared probe-path logic for ONNX layout models. Mirrors the upstream
/// <c>LayoutModelLocator.FindModelPath()</c> probe order, parameterized
/// by filename so per-model wrappers (<see cref="HeronModelLocator"/>,
/// <see cref="PPDocLayoutSModelLocator"/>) only carry the filename const.
/// </summary>
internal static class ModelLocator
{
    /// <summary>Probe locations in priority order. The first that exists wins.</summary>
    public static IEnumerable<string> ProbePaths(string fileName)
    {
        yield return Path.Combine(AppContext.BaseDirectory, "models", fileName);

        var appDir = Environment.GetEnvironmentVariable("APPDIR");
        if (!string.IsNullOrEmpty(appDir))
            yield return Path.Combine(appDir, "models", fileName);

        yield return Path.Combine(AppConfig.ConfigDir, "models", fileName);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
            yield return Path.Combine(localAppData, "railreader2", "models", fileName);

        yield return Path.Combine(Directory.GetCurrentDirectory(), "models", fileName);

        var cwd = Directory.GetCurrentDirectory();
        for (int up = 1; up <= 3; up++)
        {
            var parent = Directory.GetParent(cwd)?.FullName;
            if (parent is null) break;
            yield return Path.Combine(parent, "models", fileName);
            cwd = parent;
        }
    }

    /// <summary>Returns the first existing probe path for <paramref name="fileName"/>, or <c>null</c>.</summary>
    public static string? FindModelPath(string fileName)
    {
        foreach (var p in ProbePaths(fileName))
        {
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
