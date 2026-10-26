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

    [GeneratedRegex(@"^\s*(?:(?:1|I)\.?\s*\p{Lu}|(?i:introduction|abstract)\s*$)")]
    private static partial Regex PaperStartRegex();

    /// <summary>A heading that opens a paper ("1 Introduction", "Abstract"): in a volume it marks where one
    /// paper ends and the next begins.</summary>
    internal static bool IsPaperStart(string text) => text.Length <= 60 && PaperStartRegex().IsMatch(text);

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
        var authors = ParseAuthors(body);
        string? first = authors?.First, second = authors?.Second, year = null;
        int count = authors?.Count ?? 0;
        if (authors is not null) year = PickYear(body, authors.End);
        if (year is null) { first = null; second = null; count = 0; }
        if (label is null && first is null) return null;
        return new BibEntry(page, block, label, first, second, count, year);
    }

    private sealed record Authors(string First, string? Second, int Count, int End);

    [GeneratedRegex(@"^\s*(?:" + Initials + @")(?<s>" + Surname + ")")]
    private static partial Regex InitialsFirstStartRegex();

    [GeneratedRegex(@"\G(?:\s*,\s*(?:and\s+)?|\s*\band\s+|\s*&\s*)(?:" + Initials + @")(?<s>" + Surname + ")")]
    private static partial Regex NextInitialsFirstAuthorRegex();

    [GeneratedRegex(@"\G(?:\s*,\s*(?:and\s+)?|\s*\band\s+|\s*&\s*)(?:(?<s>" + Surname + @")\s*,\s*" + Initials + "|" + Initials + @"(?<s2>" + Surname + "))")]
    private static partial Regex NextAuthorRegex();

    [GeneratedRegex(@"(?i)(?:doi:?\s*|https?://|arxiv:?\s*)\S+|\b10\.\d{4,}/\S+")]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex(@"\(\s*((?:19|20)\d\d[a-z]?)\s*\)")]
    private static partial Regex ParenthesisedYearRegex();

    /// <summary>The entry's authors, whichever way it writes them: "Surname, I., Surname, I." (APA, AMS)
    /// or "I. Surname, I. Surname and I. Surname." (initials first).</summary>
    private static Authors? ParseAuthors(string body)
    {
        string head = body.Length > 400 ? body[..400] : body;
        // style A: the first author is "Surname, Initials"; the next ones follow in either order
        if (SurnameCommaInitialsRegex().Match(head) is { Success: true } f && head[..f.Index].Trim().Length == 0)
        {
            string? second = null;
            int count = 1, pos = f.Index + f.Length;
            while (NextAuthorRegex().Match(head, pos) is { Success: true } next)
            {
                count++;
                second ??= CitationMentions.NormaliseSurname(next.Groups["s"].Success ? next.Groups["s"].Value : next.Groups["s2"].Value);
                pos = next.Index + next.Length;
            }
            if (EtAlRegex().IsMatch(head[pos..Math.Min(head.Length, pos + 24)])) count++;
            return new Authors(CitationMentions.NormaliseSurname(f.Groups["s"].Value), second, count, pos);
        }
        // style B: "I. I. Surname, I. Surname and I. Surname. Title"
        if (InitialsFirstStartRegex().Match(head) is { Success: true } b)
        {
            string first = CitationMentions.NormaliseSurname(b.Groups["s"].Value);
            string? second = null;
            int n = 1, pos = b.Index + b.Length;
            while (NextInitialsFirstAuthorRegex().Match(head, pos) is { Success: true } next)
            {
                n++; second ??= CitationMentions.NormaliseSurname(next.Groups["s"].Value); pos = next.Index + next.Length;
            }
            if (EtAlRegex().IsMatch(head[pos..Math.Min(head.Length, pos + 12)])) n++;
            return new Authors(first, second, n, pos);
        }
        return null;
    }

    /// <summary>The publication year: one in parentheses, else the first right after the authors
    /// ("…Roiter, 1997:"), else the last year in the entry ("…, 2026.").</summary>
    private static string? PickYear(string body, int authorsEnd)
    {
        string head = body.Length > 400 ? body[..400] : body;
        head = IdentifierRegex().Replace(head, m => new string(' ', m.Length));   // years inside a DOI, URL or arXiv id are not the year
        if (ParenthesisedYearRegex().Match(head) is { Success: true } p) return p.Groups[1].Value;
        var all = YearRegex().Matches(head);
        if (all.Count == 0) return null;
        foreach (Match m in all)
            if (m.Index >= authorsEnd - 2 && m.Index - authorsEnd <= 24) return m.Value;
        return all[^1].Value;
    }
}
