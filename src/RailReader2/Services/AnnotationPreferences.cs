using System.Text.Json;
using System.Text.Json.Serialization;
using RailReader.Core;
using RailReader.Core.Services;

namespace RailReader2.Services;

/// <summary>
/// App-level annotation authoring preferences. Shell-managed sidecar (<c>ConfigDir/annotation_prefs.json</c>)
/// like <see cref="PortalPreferences"/>, since Core's <see cref="AppConfig"/> is a NuGet type we don't extend.
/// </summary>
public sealed class AnnotationPreferences
{
    /// <summary>When true, a newly drawn highlight/underline/strike-out/squiggle that overlaps an
    /// existing one of the same kind and colour is merged into it on release, instead of being
    /// added as a second, overlapping annotation (Core's
    /// <c>AnnotationInteractionHandler.AutoMergeTextMarkup</c>). Off by default: a merge gives the
    /// result a fresh identity, which matters for reviewers' highlights that live in the PDF.</summary>
    public bool AutoMergeTextMarkup { get; set; }

    public static string Path => System.IO.Path.Combine(AppConfig.ConfigDir, "annotation_prefs.json");

    public static AnnotationPreferences Load()
        => JsonSidecar.Load(Path, AnnotationPreferencesJsonContext.Default.AnnotationPreferences,
            static () => new AnnotationPreferences());

    public void Save()
        => JsonSidecar.Save(Path, this, AnnotationPreferencesJsonContext.Default.AnnotationPreferences);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
[JsonSerializable(typeof(AnnotationPreferences))]
internal partial class AnnotationPreferencesJsonContext : JsonSerializerContext;
