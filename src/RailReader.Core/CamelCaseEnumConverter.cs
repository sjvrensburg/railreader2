using System.Text.Json;
using System.Text.Json.Serialization;

namespace RailReader.Core;

/// <summary>
/// Generic-typed string-enum converter that serialises enum values as
/// camelCase strings. Wraps <see cref="JsonStringEnumConverter{TEnum}"/> so it
/// can be applied via <c>[JsonConverter(typeof(CamelCaseEnumConverter&lt;TEnum&gt;))]</c>
/// — required for AOT/source-gen compatibility (the non-generic
/// <see cref="JsonStringEnumConverter"/> needs runtime code generation).
/// </summary>
internal sealed class CamelCaseEnumConverter<T>()
    : JsonStringEnumConverter<T>(JsonNamingPolicy.CamelCase)
    where T : struct, Enum;
