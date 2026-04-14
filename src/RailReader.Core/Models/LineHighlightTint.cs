using System.Text.Json.Serialization;
using RailReader.Core;

namespace RailReader.Core.Models;

[JsonConverter(typeof(CamelCaseEnumConverter<LineHighlightTint>))]
public enum LineHighlightTint
{
    Auto,
    Yellow,
    Cyan,
    Green,
    Pink,
    Orange,
    Blue
}
