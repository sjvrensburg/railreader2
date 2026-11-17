using System.Text;
using System.Text.RegularExpressions;

namespace RailReader2.Library;

/// <summary>A parsed library query. Reader input is never handed to FTS5 as is (its syntax errors
/// throw, and "self-attention" or "C++" would be operators): <see cref="Parse"/> turns it into quoted
/// terms. Supported: words (all required; a word also finds its siblings, <see cref="Family"/>), <c>"exact phrases"</c>, <c>prefix*</c> and <c>-excluded</c>
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
            // Explicit AND: FTS5 only joins plain phrases implicitly, not a parenthesised OR group.
            var sb = new StringBuilder(string.Join(" AND ", terms));
            if (excluded.Count > 0)
                sb.Insert(0, '(').Append(')').Append(" NOT (").Append(string.Join(" OR ", excluded)).Append(')');
            MatchExpression = sb.ToString();
        }
    }

    public bool IsEmpty => MatchExpression is null;

    /// <summary>A regular expression (to be used case-insensitively) for the in-document search (Core's
    /// <c>SearchService</c>) that marks the query's words on the opened page, or null when there are none.
    /// Approximate by design: the index stems words ("estimators" finds "estimation"), a regex can't, so a
    /// long word matches from its stem-ish start (suffixes such as -s, -ing, -ion dropped) onwards. Used
    /// when the index can't say which words it matched (<see cref="HighlightPatternFor"/>).</summary>
    public string? HighlightPattern
    {
        get
        {
            var parts = new List<string>();
            foreach (var t in PlainTerms)
            {
                bool prefix = t.EndsWith('*');
                string raw = prefix ? t[..^1] : t;
                var words = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0) continue;
                if (words.Length > 1)
                    parts.Add(string.Join(@"[\s\-]+", words.Select(Regex.Escape)));
                else
                    parts.Add(Regex.Escape(prefix ? raw : Stem(raw)) + (prefix || Stem(raw) != raw ? @"\w*" : ""));
            }
            // Shown in the Search pane's box, so kept short; the pane searches case-insensitively.
            return parts.Count == 0 ? null : @"\b(" + string.Join("|", parts) + ")";
        }
    }

    /// <summary>The most matched forms <see cref="HighlightPatternFor"/> lists (the pattern is shown in the
    /// Search pane's box).</summary>
    public const int MaxHighlightForms = 12;

    /// <summary>A regular expression marking exactly the <paramref name="forms"/> the index matched on a
    /// page (<see cref="LibraryIndex.MatchedForms"/>), so every word a search found by stemming is
    /// highlighted, not only the ones <see cref="HighlightPattern"/>'s suffix strip guesses. Whitespace in a
    /// matched phrase may be any whitespace or a hyphen (a line break in the PDF). Forms are kept in the
    /// order given (page order, from <see cref="LibraryIndex.MatchedForms"/>); the closing <c>\b</c> makes
    /// the order irrelevant to matching. Null when there are none.</summary>
    public static string? HighlightPatternFor(IEnumerable<string> forms)
    {
        var parts = forms
            .Select(f => f.Split((char[])[' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries))
            .Where(w => w.Length > 0)
            .Select(w => string.Join(@"[\s\-]+", w.Select(Regex.Escape)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxHighlightForms) // in page order, so the cap drops forms found late, not short ones
            .ToList();
        return parts.Count == 0 ? null : @"\b(" + string.Join("|", parts) + @")\b";
    }

    static readonly string[] Suffixes = ["ations", "ation", "ings", "ing", "ions", "ion", "ors", "ers", "ies", "es", "ed", "or", "er", "s"];

    static string Stem(string word)
    {
        if (word.Length < 5 || !word.All(char.IsLetter)) return word;
        foreach (var suf in Suffixes)
            if (word.Length - suf.Length >= 4 && word.EndsWith(suf, StringComparison.OrdinalIgnoreCase))
                return word[..^suf.Length];
        return word;
    }

    // Endings that tie a word to its siblings where the Porter stemmer doesn't: "diagnosing" is stemmed
    // to "diagnos", "diagnosis" to "diagnosi"; "estimator" to "estim", "estimate" to "estimat".
    // Longest first, so the longest matching ending is the one removed.
    static readonly string[] FamilyEndings =
        ["ations", "ation", "ings", "ions", "ing", "ion", "ors", "ers", "ies", "ses", "sis", "es", "ed", "or", "er", "is", "e", "s", "y"];

    /// <summary>The shortest root <see cref="Family"/> keeps (shorter roots join unrelated words).</summary>
    const int MinFamilyRoot = 5;

    /// <summary>A plain word and the forms built from its root with each of <see cref="FamilyEndings"/>,
    /// for an OR in the MATCH expression: "diagnosing" also finds "diagnosis" and "diagnose". Words the
    /// tokenizer would split, words with none of the endings, and words whose root would be shorter than
    /// <see cref="MinFamilyRoot"/> are left alone. Forms that aren't words just find no rows.</summary>
    internal static IReadOnlyList<string> Family(string word)
    {
        if (!word.All(char.IsLetter)) return [word];
        string root = word;
        foreach (var end in FamilyEndings)
            if (word.Length - end.Length >= MinFamilyRoot && word.EndsWith(end, StringComparison.OrdinalIgnoreCase))
            {
                root = word[..^end.Length];
                // "-sis"/"-ses" keep their s: "analysis" → "analys" (analyse, analysing).
                if (end is "sis" or "ses") root += "s";
                break;
            }
        // No ending removed: the stemmer already covers its inflections ("kernel", "kernels").
        if (root.Length < MinFamilyRoot || root.Equals(word, StringComparison.OrdinalIgnoreCase)) return [word];
        var forms = new List<string> { word, root };
        foreach (var end in FamilyEndings)
        {
            string f = root + end;
            if (!forms.Contains(f, StringComparer.OrdinalIgnoreCase)) forms.Add(f);
        }
        return forms;
    }

    static string Quote(string raw) => "\"" + raw.Replace("\"", "\"\"") + "\"";

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
            string fts = Quote(raw) + (prefix ? "*" : "");
            if (negate) excluded.Add(fts);
            else
            {
                // A single word also matches its siblings; phrases, prefixes and exclusions stay exact.
                if (!prefix && !raw.Contains(' ') && Family(raw) is { Count: > 1 } family)
                    fts = "(" + string.Join(" OR ", family.Select(Quote)) + ")";
                terms.Add(fts);
                plain.Add(prefix ? raw + "*" : raw);
            }
        }
        return new LibraryQuery(terms, excluded, plain);
    }

    public override string ToString() => MatchExpression ?? "";
}
