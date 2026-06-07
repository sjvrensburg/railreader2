namespace RailReader.Demo;

/// <summary>
/// A parsed demo script: global settings plus the ordered list of steps to drive the running
/// app through. Produced by <see cref="DslParser.Parse"/>; consumed by <see cref="DemoSequencer"/>.
/// </summary>
/// <param name="Name">Optional demo name (the <c>demo:</c> key).</param>
/// <param name="Source">PDF to open (the <c>source:</c> key); resolved to an absolute path by the runner.</param>
/// <param name="Fps">Optional capture frame rate (used by the Phase C recorder).</param>
/// <param name="Cursor">Pointer mode: hidden | park | follow (Phase D; parsed now, applied later).</param>
/// <param name="Recorder">Recorder backend (Phase C; parsed now, applied later).</param>
/// <param name="Output">Output video path (Phase C).</param>
/// <param name="Steps">The ordered steps.</param>
public sealed record DemoScript(
    string? Name,
    string? Source,
    int? Fps,
    string? Cursor,
    string? Recorder,
    string? Output,
    IReadOnlyList<DemoStep> Steps);

/// <summary>
/// One demo step: a verb plus its arguments and an optional explicit wait. A scalar argument
/// (e.g. <c>goto_page: 1</c>) is stored under the key <see cref="ValueKey"/>; an inline map
/// (e.g. <c>frame_role: { role: figure, index: 0 }</c>) stores each key. The generic shape lets
/// new verbs be added without changing the model.
/// </summary>
/// <param name="Verb">The lower-cased verb (e.g. "frame_role").</param>
/// <param name="Args">Verb arguments by name; a bare scalar is under <see cref="ValueKey"/>.</param>
/// <param name="Wait">Explicit wait override ("settled" | "none" | a duration), or null for the verb default.</param>
/// <param name="Line">1-based source line, for diagnostics.</param>
public sealed record DemoStep(
    string Verb,
    IReadOnlyDictionary<string, string> Args,
    string? Wait,
    int Line)
{
    /// <summary>Args key holding a bare scalar argument (e.g. the page in <c>goto_page: 1</c>).</summary>
    public const string ValueKey = "value";
}

/// <summary>Thrown when a demo script can't be parsed; carries the 1-based source line.</summary>
public sealed class DslParseException(string message, int line)
    : Exception($"line {line}: {message}")
{
    public int Line { get; } = line;
}
