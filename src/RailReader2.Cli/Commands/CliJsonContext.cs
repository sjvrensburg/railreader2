using System.Text.Json.Serialization;
using RailReader2.References;

namespace RailReader.Cli.Commands;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StructureOutput))]
[JsonSerializable(typeof(AnnotationExportOutput))]
[JsonSerializable(typeof(VlmOutput))]
[JsonSerializable(typeof(OutlineEntryOutput))]
[JsonSerializable(typeof(BBoxOutput))]
[JsonSerializable(typeof(ReferenceReport))]
[JsonSerializable(typeof(GroundTruth))]
internal partial class CliJsonContext : JsonSerializerContext;
