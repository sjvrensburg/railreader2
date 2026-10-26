using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RailReader2.References;

/// <summary>
/// Citation mentions in running text and the keys they resolve by.
///
/// Key grammar (also the <c>number</c> of a <see cref="ReferenceIndex.Reference"/> of kind Citation):
/// <list type="bullet">
/// <item><c>12</c> — a numeric entry label ("[12]", "[3, 7–9]", "[12, p. 165]")</item>
/// <item><c>ik99</c> — an alphanumeric label ("[IK99]", "[Bis06]")</item>
/// <item><c>smith:2019</c> / <c>smith&amp;jones:2019a</c> / <c>smith+:2019</c> — author-year: first
/// surname, then "&amp;second surname" for two authors or "+" for "et al.", then the year with its
/// optional suffix letter</item>
/// </list>
/// </summary>
internal static partial class CitationMentions
{
    // --- numeric and keyed brackets ---------------------------------------------------------

    [GeneratedRegex(@"\[(?<body>[^\[\]]{1,90})\]")]
    private static partial Regex BracketRegex();

    // item = 12 or 12–15; separator = comma, semicolon, ampersand or "and" ("[7, 8, and 19]", "[6, 15 and 16]")
    [GeneratedRegex(@"^\s*\d{1,3}(?:\s*[–\-]\s*\d{1,3})?(?:(?:\s*[,;&]\s*(?:and\s+)?|\s+and\s+)\d{1,3}(?:\s*[–\-]\s*\d{1,3})?)*")]
    private static partial Regex NumberListRegex();

    [GeneratedRegex(@"\d{1,3}")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"^\p{Lu}\p{L}{0,6}(?:\+\p{L}{0,3})?\d{2}[a-z]?$")]
    private static partial Regex LabelKeyRegex();

    // A bracket that follows these is an interval or a set, not a citation ("x ∈ [1, 2]", "on [0, 1]").
    private static readonly HashSet<string> IntervalWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "on", "over", "within", "interval", "intervals", "set", "domain", "range", "between", "into", "than", "at",
    };
    private const string MathBefore = "∈∉=⊂⊆≤≥<>+-*/×∪∩({|^_\\";

    /// <summary>Numeric and keyed bracket citations in a text run, as (index, key).</summary>
    internal static IEnumerable<(int Index, string Key)> Brackets(string text, int startLimit)
    {
        foreach (Match m in BracketRegex().Matches(text))
        {
            if (m.Index >= startLimit) yield break;
            string body = m.Groups["body"].Value;
            var numbers = NumberListRegex().Match(body);
            if (numbers.Success && numbers.Length > 0)
            {
                string rest = body[numbers.Length..].Trim();
                // "[12, p. 165, eq. (4.21)]": numbers, then a locator that carries a letter.
                if (rest.Length > 0 && !(rest[0] == ',' && rest.Any(char.IsLetter))) continue;
                string listPart = numbers.Value;
                if (rest.Length > 0) listPart = NumberRegex().Match(listPart).Value;   // only the entry number
                var values = NumberRegex().Matches(listPart).Select(n => int.Parse(n.Value, CultureInfo.InvariantCulture)).ToList();
                if (values.Count == 0 || values.Any(v => v < 1)) continue;
                bool range = listPart.Contains('–') || (listPart.Contains('-') && values.Count >= 2);
                if (LooksLikeSetOrInterval(text, m.Index, values.Count, range, rest.Length > 0)) continue;
                foreach (var v in values.Distinct()) yield return (m.Index, v.ToString(CultureInfo.InvariantCulture));
                continue;
            }
            // keyed: "[IK99]", "[Bis06, Mur12]", "[IK99, Lemma 1.15]"
            List<string> keys = [];
            foreach (string token in Regex.Split(body, @"\s*[,;]\s*"))
            {
                string t = token.Trim();
                if (!LabelKeyRegex().IsMatch(t)) break;
                keys.Add(NormaliseLabel(t));
            }
            foreach (var k in keys.Distinct()) yield return (m.Index, k);
        }
    }

    private static bool LooksLikeSetOrInterval(string text, int index, int count, bool range, bool hasLocator)
    {
        int i = index - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i])) i--;
        if (i >= 0 && MathBefore.Contains(text[i])) return true;
        // a bare pair "[1, 2]" is an interval unless something marks it as a citation
        if (count == 2 && !range && !hasLocator)
        {
            int end = i + 1, start = end;
            while (start > 0 && char.IsLetter(text[start - 1])) start--;
            if (start < end && IntervalWords.Contains(text[start..end])) return true;
        }
        return false;
    }

    // --- author-year --------------------------------------------------------------------------

    private const string Particle = @"(?:(?:[vV]an|[vV]on|[dD]e|[dD]el|[dD]er|[dD]u|[dD]i|[lL]a|[lL]e|[tT]er|[tT]en|[bB]in|[Aa]l|[Mm]ac)\s+)*";
    private const string Word = @"\p{Lu}[\p{L}\p{M}\p{Sk}'’\-]+";
    private const string Name = @"(?<![\p{L}\p{M}\d])" + Particle + Word + @"(?:\s+" + Word + @")?";
    private const string Year = @"(?:19|20)\d\d[a-z]?";
    private const string YearList = Year + @"(?:\s*,\s*(?:" + Year + @"|[a-z](?![a-z])))*";
    private const string Page = @"(?:,\s*pp?\.\s*\d+(?:\s*[–\-]\s*\d+)?)?";

    [GeneratedRegex(@"(?<a>" + Name + @")(?:\s+(?<etal>et\s+al\.?)|\s+(?:and|&)\s+(?<b>" + Name + @"))?\s*\(\s*(?<y>" + YearList + ")" + Page + @"\s*\)")]
    private static partial Regex NarrativeRegex();

    [GeneratedRegex(@"\((?<c>[^()]{3,260})\)")]
    private static partial Regex ParentheticalRegex();

    [GeneratedRegex(@"^(?i:(?:see|e\.g\.|i\.e\.|cf\.|also|for example|for instance)[,\s]+)*(?<a>" + Name + @")(?:\s+(?<etal>et\s+al\.?)|\s*(?:and|&)\s*(?<b>" + Name + @"))?\s*,?\s*(?<y>" + YearList + ")" + Page + "$")]
    private static partial Regex ClusterRegex();

    // Words that start a sentence and so get captured as the first word of a two-word surname.
    private static readonly HashSet<string> SentenceStarters = new(StringComparer.Ordinal)
    {
        "Following", "See", "In", "Both", "Since", "While", "As", "Among", "Under", "Although", "However",
        "Thus", "Recent", "Also", "Finally", "Notably", "Similarly", "Moreover", "Indeed", "Here", "Our",
        "The", "This", "That", "These", "Using", "Given", "By", "From", "With", "For", "At", "On", "Of",
        "Consider", "Note", "Recall", "Applying", "Unlike", "Like", "Compare", "Per", "After", "Before",
    };

    /// <summary>Author-year citations in a text run, as (index, key).</summary>
    internal static IEnumerable<(int Index, string Key)> AuthorYear(string text, int startLimit)
    {
        foreach (Match m in NarrativeRegex().Matches(text))
        {
            if (m.Index >= startLimit) break;
            // "(Smith et al. (2019))" would double count; a narrative citation is not inside a parenthesis
            foreach (var key in Keys(m, m.Groups["etal"].Success)) yield return (m.Index, key);
        }
        foreach (Match p in ParentheticalRegex().Matches(text))
        {
            if (p.Index >= startLimit) break;
            foreach (string cluster in p.Groups["c"].Value.Split(';'))
            {
                var m = ClusterRegex().Match(cluster.Trim());
                if (!m.Success) continue;
                foreach (var key in Keys(m, m.Groups["etal"].Success)) yield return (p.Index, key);
            }
        }
    }

    private static IEnumerable<string> Keys(Match m, bool etal)
    {
        string a = StripSentenceStarter(m.Groups["a"].Value);
        string first = NormaliseSurname(a);
        if (first.Length == 0) yield break;
        string who = etal ? first + "+"
            : m.Groups["b"].Success ? first + "&" + NormaliseSurname(StripSentenceStarter(m.Groups["b"].Value))
            : first;
        string last = null!;
        foreach (string part in Regex.Split(m.Groups["y"].Value, @"\s*,\s*"))
        {
            // "2024a,c" lists suffix letters of the same year
            if (part.Length == 1 && last is { Length: >= 4 } ) { yield return who + ":" + last[..4] + part; continue; }
            last = part;
            yield return who + ":" + part;
        }
    }

    private static string StripSentenceStarter(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int skip = 0;
        while (words.Length - skip > 1 && SentenceStarters.Contains(words[skip])) skip++;
        return string.Join(' ', words.Skip(skip));
    }

    // --- normalisation shared with the bibliography ------------------------------------------

    /// <summary>A surname as a comparison key: letters only, lower-case, diacritics removed, so that
    /// "Goulet Coulombe", "Fein-Ashley" and "Schölkopf" compare across mention and entry.</summary>
    internal static string NormaliseSurname(string surname)
    {
        var sb = new StringBuilder();
        foreach (char c in surname.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetter(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    internal static string NormaliseLabel(string label) => label.Trim().ToLowerInvariant();
}
