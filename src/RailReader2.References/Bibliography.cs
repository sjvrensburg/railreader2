using System.Text.RegularExpressions;
using RailReader.Core.Models;

namespace RailReader2.References;

/// <summary>One reference-list entry: where it is and the keys a citation can reach it by.</summary>
/// <param name="Label">Numeric or alphanumeric label ("12", "ik99"), or null for an unlabelled entry.</param>
/// <param name="FirstAuthor">Normalised first-author surname, or null when none could be read.</param>
/// <param name="SecondAuthor">Normalised second-author surname, or null.</param>
/// <param name="AuthorCount">Authors read from the entry ("et al." counts as one more).</param>
/// <param name="Year">Year with its suffix letter ("2024a"), or null.</param>
internal sealed record BibEntry(int Page, int Block, string? Label, string? FirstAuthor, string? SecondAuthor,
    int AuthorCount, string? Year);

/// <summary>One reference list: from a "References" heading to the next heading.</summary>
internal sealed record BibRegion(int StartPage, List<BibEntry> Entries)
{
    public int EndPage => Entries.Count == 0 ? StartPage : Entries[^1].Page;
}

internal static partial class BibliographyParser
{
    [GeneratedRegex(@"^\s*(?:\d+\.?|[IVX]+\.?|[A-Z]\.)?\s*(?:references?(?:\s+and\s+notes)?|bibliography|literature\s+cited|works\s+cited|reference\s+list|sources)\s*:?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ReferenceHeadingRegex();

    /// <summary>True when a block's text is a short "References"-style title.</summary>
    internal static bool IsReferenceHeading(string text) => text.Length <= 48 && ReferenceHeadingRegex().IsMatch(text);

    [GeneratedRegex(@"^\s*\[\s*(?<n>\d{1,3})\s*\]")]
    private static partial Regex BracketNumberRegex();

    [GeneratedRegex(@"^\s*(?<n>\d{1,3})\s*[.)]\s+\p{L}")]
    private static partial Regex PlainNumberRegex();

    [GeneratedRegex(@"^\s*\[\s*(?<k>\p{Lu}\p{L}{0,6}(?:\+\p{L}{0,3})?\d{2}[a-z]?)\s*\]")]
    private static partial Regex KeyLabelRegex();

    private const string Initials = @"(?:\p{Lu}\.[\s\-]*)+";
    private const string Word = @"\p{Lu}[\p{L}\p{M}\p{Sk}'’\-]+";
    private const string Particle = @"(?:(?:[vV]an|[vV]on|[dD]e|[dD]el|[dD]er|[dD]u|[dD]i|[lL]a|[lL]e|[tT]er|[tT]en|[bB]in|[Aa]l|[Mm]ac)\s+)*";
    private const string Surname = @"(?<![\p{L}\p{M}\d])" + Particle + Word + @"(?:\s+" + Word + @")?";

    [GeneratedRegex(@"(?<![\p{L}\d])(?:19|20)\d\d[a-z]?(?![\d])")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"(?<s>" + Surname + @")\s*,\s*" + Initials)]
    private static partial Regex SurnameCommaInitialsRegex();

    [GeneratedRegex(@"(?:,|\band\b|&)\s*" + Initials + @"\s*(?<s>" + Surname + ")")]
    private static partial Regex InitialsSurnameRegex();

    [GeneratedRegex(Initials)]
    private static partial Regex InitialsGroupRegex();

    [GeneratedRegex(@"(?<![\p{L}])(?:et\s+al|and\s+co-?authors|and\s+others)", RegexOptions.IgnoreCase)]
    private static partial Regex EtAlRegex();

    /// <summary>Parse a block of the reference list into entries. A block usually holds one; a block
    /// whose lines open with consecutive labels holds several.</summary>
    internal static List<BibEntry> ParseBlock(int page, int block, string text, IReadOnlyList<string>? lines)
    {
        text = text.Trim();
        if (text.Length < 8) return [];
        if (lines is { Count: > 1 } && SplitByLabels(lines) is { Count: > 1 } pieces)
            return [.. pieces.Select(p => ParseOne(page, block, p)).OfType<BibEntry>()];
        return ParseOne(page, block, text) is { } one ? [one] : [];
    }

    private static List<string>? SplitByLabels(IReadOnlyList<string> lines)
    {
        List<string> pieces = [];
        int labelled = 0;
        foreach (string line in lines)
        {
            bool isLabel = BracketNumberRegex().IsMatch(line) || KeyLabelRegex().IsMatch(line);
            if (isLabel) { pieces.Add(line); labelled++; }
            else if (pieces.Count > 0) pieces[^1] += " " + line;
            else pieces.Add(line);
        }
        return labelled >= 2 ? pieces : null;
    }

    private static BibEntry? ParseOne(int page, int block, string text)
    {
        string? label = null;
        string body = text;
        if (BracketNumberRegex().Match(text) is { Success: true } b) { label = b.Groups["n"].Value.TrimStart('0'); body = text[b.Length..]; }
        else if (KeyLabelRegex().Match(text) is { Success: true } k) { label = CitationMentions.NormaliseLabel(k.Groups["k"].Value); body = text[k.Length..]; }
        else if (PlainNumberRegex().Match(text) is { Success: true } n) { label = n.Groups["n"].Value.TrimStart('0'); body = text[(n.Groups["n"].Index + n.Groups["n"].Length)..].TrimStart(".) ".ToCharArray()); }
        if (label is { Length: 0 }) label = null;

        // authors and year, when the entry is written author-year (also read for labelled entries)
        string? first = null, second = null, year = null;
        int count = 0;
        string head = body.Length > 320 ? body[..320] : body;
        var ym = YearRegex().Match(head);
        if (ym.Success)
        {
            string authors = head[..ym.Index];
            var groups = InitialsGroupRegex().Matches(authors);
            if (groups.Count > 0 && SurnameCommaInitialsRegex().Match(authors) is { Success: true } f && f.Index < 6)
            {
                first = CitationMentions.NormaliseSurname(f.Groups["s"].Value);
                count = groups.Count + (EtAlRegex().IsMatch(authors) ? 1 : 0);
                year = ym.Value;
                var after = authors[(f.Index + f.Length)..];
                second = SecondAuthor(after);
            }
        }
        if (label is null && first is null) return null;
        return new BibEntry(page, block, label, first, second, count, year);
    }

    /// <summary>The second author's surname from the text after the first author's initials:
    /// "Cho, K., and Bengio, Y." (surname first) or "and W. B. Rossow" (initials first).</summary>
    private static string? SecondAuthor(string after)
    {
        var apa = Regex.Match(after, @"(?:,|\band\b|&)\s*(?<s>" + Surname + @")\s*,\s*" + Initials);
        var ams = InitialsSurnameRegex().Match(after);
        Match? best = (apa.Success, ams.Success) switch
        {
            (true, true) => apa.Index <= ams.Index ? apa : ams,
            (true, false) => apa,
            (false, true) => ams,
            _ => null,
        };
        return best is null ? null : CitationMentions.NormaliseSurname(best.Groups["s"].Value);
    }
}
