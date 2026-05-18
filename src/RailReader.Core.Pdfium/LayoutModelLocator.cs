namespace RailReader.Core.Services;

/// <summary>
/// Locates the PP-DocLayoutV3 ONNX model file on disk by probing well-known
/// install locations. Returns null if not found anywhere; the caller should
/// fall back to layout-less behaviour.
/// </summary>
public static class LayoutModelLocator
{
    public static string? FindModelPath()
    {
        const string filename = "PP-DocLayoutV3.onnx";
        var candidates = new List<string?>
        {
            Path.Combine(AppContext.BaseDirectory, "models", filename),
            Environment.GetEnvironmentVariable("APPDIR") is { } appDir
                ? Path.Combine(appDir, "models", filename) : null,
            // Same base directory as AppConfig.ConfigDir so the model is found
            // wherever the app stored it (%APPDATA% on Windows, ~/.config on
            // Linux, ~/Library/Application Support on macOS).
            Path.Combine(AppConfig.ConfigDir, "models", filename),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "railreader2", "models", filename),
            Path.Combine("models", filename),
        };

        // Walk up from CWD
        for (int i = 1; i <= 3; i++)
        {
            var walkUp = string.Concat(Enumerable.Repeat("../", i));
            candidates.Add(Path.Combine(walkUp, "models", filename));
        }

        foreach (var path in candidates)
        {
            if (path is not null && File.Exists(path))
                return Path.GetFullPath(path);
        }
        return null;
    }
}
