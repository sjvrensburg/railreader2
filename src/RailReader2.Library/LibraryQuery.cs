using System.Text;

namespace RailReader2.Library;

/// <summary>A parsed library query. Reader input is never handed to FTS5 as is (its syntax errors
/// throw, and "self-attention" or "C++" would be operators): <see cref="Parse"/> turns it into quoted
/// terms. Supported: words (all required), <c>"exact phrases"</c>, <c>prefix*</c> and <c>-excluded</c>
/// words or phrases. Anything else is literal text.</summary>
public sealed class LibraryQuery
{
    public IReadOnlyList<string> Terms { get; }
    public IReadOnlyList<string> Excluded { get; }
    /// <summary>The FTS5 MATCH expression, or null when nothing searchable is left.</summary>
    public string? MatchExpression { get; }
    /// <summary>The plain words and phrases, for highlighting matches inside the opened document.</summary>
    public IReadOnlyList<string> PlainTerms { get; }

    LibraryQuery(List<string> terms, List<string> excluded, List<string> plain)
    {
        Terms = terms; Excluded = excluded; PlainTerms = plain;
        if (terms.Count > 0)
        {
            var sb = new StringBuilder(string.Join(" ", terms));
            if (excluded.Count > 0)
                sb.Insert(0, '(').Append(')').Append(" NOT (").Append(string.Join(" OR ", excluded)).Append(')');
            MatchExpression = sb.ToString();
        }
    }

    public bool IsEmpty => MatchExpression is null;

    public static LibraryQuery Parse(string? input)
    {
        var terms = new List<string>();
        var excluded = new List<string>();
        var plain = new List<string>();
        string s = TextNormalizer.Normalize(input ?? "");
        int i = 0;
        while (i < s.Length)
        {
            if (s[i] == ' ') { i++; continue; }
            bool negate = false;
            if (s[i] == '-' && i + 1 < s.Length && s[i + 1] != ' ') { negate = true; i++; }

            string raw;
            bool prefix = false;
            if (s[i] == '"')
            {
                int end = s.IndexOf('"', i + 1);
                if (end < 0) end = s.Length;
                raw = s[(i + 1)..end];
                i = Math.Min(end + 1, s.Length);
            }
            else
            {
                int end = s.IndexOf(' ', i);
                if (end < 0) end = s.Length;
                raw = s[i..end];
                i = end;
                if (raw.Length > 1 && raw[^1] == '*') { prefix = true; raw = raw[..^1]; }
            }

            // A term with nothing the tokenizer keeps (pure punctuation) would be an empty phrase.
            if (!raw.Any(char.IsLetterOrDigit)) continue;
            string fts = "\"" + raw.Replace("\"", "\"\"") + "\"" + (prefix ? "*" : "");
            if (negate) excluded.Add(fts);
            else { terms.Add(fts); plain.Add(raw); }
        }
        return new LibraryQuery(terms, excluded, plain);
    }

    public override string ToString() => MatchExpression ?? "";
}
