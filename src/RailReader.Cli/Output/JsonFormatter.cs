using System.Text.Json;
using System.Text.Json.Serialization;

namespace RailReader.Cli.Output;

public sealed class JsonFormatter : IOutputFormatter
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public void WriteResult(object result)
    {
        var envelope = new { ok = true, data = result };
        Console.WriteLine(JsonSerializer.Serialize(envelope, s_options));
    }

    public void WriteError(string message)
    {
        var envelope = new { ok = false, error = message };
        Console.Error.WriteLine(JsonSerializer.Serialize(envelope, s_options));
    }

    public void WriteMessage(string message)
    {
        // In JSON mode, messages are suppressed (only structured output matters)
    }
}
