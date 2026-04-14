using System.Text.Json.Serialization;

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
internal partial class CliJsonContext : JsonSerializerContext;
