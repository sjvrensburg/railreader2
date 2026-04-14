using System.Text.Json;
using System.Text.Json.Serialization;
using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for railreader2's
/// persisted types. Required for AOT/trim-safe serialisation.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(RecentFileEntry))]
[JsonSerializable(typeof(List<RecentFileEntry>))]
[JsonSerializable(typeof(AnnotationFile))]
[JsonSerializable(typeof(Annotation))]
[JsonSerializable(typeof(HighlightAnnotation))]
[JsonSerializable(typeof(FreehandAnnotation))]
[JsonSerializable(typeof(TextNoteAnnotation))]
[JsonSerializable(typeof(RectAnnotation))]
internal partial class RailReaderJsonContext : JsonSerializerContext;
