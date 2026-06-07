using System.Globalization;

namespace RailReader.Demo;

/// <summary>
/// Pure parser for the demo DSL — a deliberately small, hand-rolled YAML subset (no external YAML
/// dependency, so it stays AOT-safe and fully unit-testable). The grammar:
///
/// <code>
/// demo: my-demo            # optional header scalars: demo, source, fps, cursor, recorder, output
/// source: papers/x.pdf
/// steps:
///   - open                 # bare verb
///   - goto_page: 1         # verb + scalar
///   - hold: 800ms
///   - frame_role: { role: figure, index: 0, zoom: 2.5 }   # verb + inline map
///     wait: settled        # continuation line: a key indented under the step item
/// </code>
///
/// Comments start with <c>#</c> (line-leading or after whitespace, outside <c>{ }</c>).
/// </summary>
public static class DslParser
{
    private static readonly HashSet<string> HeaderKeys =
        new(StringComparer.OrdinalIgnoreCase) { "demo", "source", "fps", "cursor", "recorder", "output", "fullscreen", "navigable" };

    public static DemoScript Parse(string text)
    {
        var lines = SplitLines(text);

        string? name = null, source = null, cursor = null, recorder = null, output = null, navigable = null;
        int? fps = null;
        bool fullscreen = false;
        var steps = new List<DemoStep>();

        int i = 0;
        // --- Header: key: value lines until "steps:" ---
        for (; i < lines.Count; i++)
        {
            var (no, indent, content) = lines[i];
            if (content == "steps:" || content == "steps")
            {
                i++;
                break;
            }
            var (key, value) = SplitKeyValue(content, no);
            if (key.Equals("steps", StringComparison.OrdinalIgnoreCase))
                throw new DslParseException("'steps:' must have no inline value; list its items on following lines", no);
            if (!HeaderKeys.Contains(key))
                throw new DslParseException($"unknown setting '{key}' (expected one of: demo, source, fps, cursor, recorder, output, steps)", no);
            switch (key.ToLowerInvariant())
            {
                case "demo": name = value; break;
                case "source": source = value; break;
                case "cursor": cursor = value; break;
                case "recorder": recorder = value; break;
                case "output": output = value; break;
                case "navigable": navigable = value; break;
                case "fps":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f))
                        throw new DslParseException($"fps must be an integer, got '{value}'", no);
                    fps = f;
                    break;
                case "fullscreen":
                    fullscreen = value.ToLowerInvariant() switch
                    {
                        "true" or "yes" or "on" or "1" => true,
                        "false" or "no" or "off" or "0" or "" => false,
                        _ => throw new DslParseException($"fullscreen must be true/false, got '{value}'", no),
                    };
                    break;
            }
        }

        // --- Steps: '- ' items, each with optional more-indented continuation lines ---
        while (i < lines.Count)
        {
            var (no, indent, content) = lines[i];
            if (!content.StartsWith("- ", StringComparison.Ordinal) && content != "-")
                throw new DslParseException($"expected a step item starting with '- ', got '{content}'", no);

            string body = content.Length > 1 ? content[1..].Trim() : "";
            if (body.Length == 0)
                throw new DslParseException("empty step item", no);

            var (verb, args, wait) = ParseStepBody(body, no);
            int itemIndent = indent;
            i++;

            // Continuation lines: indented deeper than the '-' and not a new item.
            while (i < lines.Count && lines[i].indent > itemIndent
                   && !lines[i].content.StartsWith("- ", StringComparison.Ordinal))
            {
                var (cno, _, ccontent) = lines[i];
                var (k, v) = SplitKeyValue(ccontent, cno);
                if (k.Equals("wait", StringComparison.OrdinalIgnoreCase))
                    wait = v;
                else
                    args[k] = v;
                i++;
            }

            steps.Add(new DemoStep(verb.ToLowerInvariant(), args, wait, no));
        }

        return new DemoScript(name, source, fps, cursor, recorder, output, fullscreen, navigable, steps);
    }

    private static (string verb, Dictionary<string, string> args, string? wait) ParseStepBody(string body, int line)
    {
        int colon = body.IndexOf(':');
        if (colon < 0)
        {
            // Bare verb, e.g. "open".
            ValidateVerb(body, line);
            return (body, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), null);
        }

        string verb = body[..colon].Trim();
        string rest = body[(colon + 1)..].Trim();
        ValidateVerb(verb, line);

        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? wait = null;

        if (rest.Length == 0)
            return (verb, args, wait);

        if (rest.StartsWith('{'))
        {
            if (!rest.EndsWith('}'))
                throw new DslParseException("unterminated inline map (missing '}')", line);
            string inner = rest[1..^1].Trim();
            if (inner.Length > 0)
            {
                foreach (var pair in inner.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var (k, v) = SplitKeyValue(pair, line);
                    if (k.Equals("wait", StringComparison.OrdinalIgnoreCase)) wait = v;
                    else args[k] = v;
                }
            }
        }
        else
        {
            // Scalar argument, e.g. "goto_page: 1".
            args[DemoStep.ValueKey] = rest;
        }

        return (verb, args, wait);
    }

    private static void ValidateVerb(string verb, int line)
    {
        if (verb.Length == 0)
            throw new DslParseException("missing verb", line);
        foreach (var c in verb)
            if (!char.IsLetterOrDigit(c) && c != '_')
                throw new DslParseException($"invalid verb '{verb}' (letters, digits, underscore only)", line);
    }

    private static (string key, string value) SplitKeyValue(string content, int line)
    {
        int colon = content.IndexOf(':');
        if (colon < 0)
            throw new DslParseException($"expected 'key: value', got '{content}'", line);
        string key = content[..colon].Trim();
        string value = content[(colon + 1)..].Trim();
        if (key.Length == 0)
            throw new DslParseException("missing key before ':'", line);
        return (key, Unquote(value));
    }

    private static string Unquote(string s)
    {
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
            return s[1..^1];
        return s;
    }

    /// <summary>Split into non-blank, comment-stripped logical lines: (1-based number, leading-space indent, trimmed content).</summary>
    private static List<(int no, int indent, string content)> SplitLines(string text)
    {
        var result = new List<(int, int, string)>();
        var raw = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int idx = 0; idx < raw.Length; idx++)
        {
            string line = StripComment(raw[idx]);
            if (string.IsNullOrWhiteSpace(line)) continue;
            int indent = 0;
            while (indent < line.Length && line[indent] == ' ') indent++;
            result.Add((idx + 1, indent, line.Trim()));
        }
        return result;
    }

    /// <summary>Strip a <c>#</c> comment: at line start, or after whitespace, and only outside <c>{ }</c>.</summary>
    private static string StripComment(string line)
    {
        int depth = 0;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '{') depth++;
            else if (c == '}') { if (depth > 0) depth--; }
            else if (c == '#' && depth == 0 && (i == 0 || line[i - 1] == ' ' || line[i - 1] == '\t'))
                return line[..i];
        }
        return line;
    }
}
