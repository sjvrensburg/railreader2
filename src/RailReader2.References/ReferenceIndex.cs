using System.Text.RegularExpressions;
using RailReader.Core;
using RailReader.Core.Models;

namespace RailReader2.References;

/// <summary>
/// Resolves in-text references like "Figure 3" / "Table 2.1" to the detected figure/table block they
/// name, by parsing the leading label of <see cref="BlockRole.Caption"/> blocks across analysed pages
/// and associating each caption with its adjacent figure/table. Backs the automatic portal pinning:
/// the portal sync loop parses the current rail line for references and shows the resolved target.
/// Per-document; UI thread only (text extraction goes through PDFium-backed caches).
/// </summary>
internal sealed partial class ReferenceIndex
{
    internal enum RefKind { Figure, Table, Theorem, Lemma, Proposition, Corollary, Definition, Example, Remark, Exercise, Equation }

    /// <summary>Numbered statements (as opposed to captioned floats): their target is the text block
    /// whose line opens with the header ("Definition 2.1 (graph)."), not a float beside a caption.</summary>
    internal static bool IsStatement(RefKind kind) => kind is >= RefKind.Theorem and <= RefKind.Exercise;

    /// <summary>Display equations: their target is a display-maths block carrying the printed tag "(3.2)".</summary>
    internal static bool IsEquation(RefKind kind) => kind == RefKind.Equation;

    /// <summary>A parsed reference label, e.g. (Figure, "3.2"). Numbers are normalized to lowercase
    /// (suffix letters, appendix prefixes, roman numerals) so an in-text mention and a caption label
    /// compare equal.</summary>
    internal readonly record struct Reference(RefKind Kind, string Number)
    {
        public override string ToString() => $"{Kind} {Number}";
    }

    /// <summary>A resolved reference target: the figure/table block plus the caption that named it.</summary>
    internal readonly record struct Target(int Page, int TargetBlock, int CaptionBlock);

    // Number forms a figure/table label can take: arabic with an optional appendix-letter prefix and
    // sub-figure suffix ("3", "2.1", "A.4", "4b"), or — case-sensitively, to avoid swallowing common
    // words — uppercase roman numerals / a bare uppercase letter (IEEE "TABLE II", appendix "Figure A").
    private const string NumberPattern = @"(?:(?:[A-Za-z][.\-])?\d+(?:\.\d+)*[a-z]?|(?-i:[IVXLC]+|[A-Z]))\b";

    // In-text mentions: "Figure 3", "Fig. 2.1", "Figs 4a", "Table 12", "Tab. 3", "TABLE II".
    // Statement kind words are matched case-sensitively and capitalised ("Theorem 2", "Ex. 3", "Defs. 2.12"):
    // lower-case "example 3" / "ex 3" is ordinary prose far more often than a reference.
    [GeneratedRegex(@"\b(?<kind>fig(?:ure)?s?|tab(?:le)?s?|(?-i:Theorems?|Thms?|Lemmas?|Lems?|Propositions?|Props?|Corollar(?:y|ies)|Cors?|Definitions?|Defs?|Exercises?|Examples?|Exs?|Remarks?|Rems?))\.?\s*(?<num>" + NumberPattern + ")",
        RegexOptions.IgnoreCase)]
    private static partial Regex LineReferenceRegex();

    // A statement header opening a line: full kind word, number, then ".", ":" or an opening bracket
    // ("Definition 2.1 (graph).", "Theorem 1. If", "Exercise 3.1 [Title]"). Body sentences such as
    // "Theorem 1 shows that" and citations such as "Definition 17 of [4]" do not match.
    [GeneratedRegex(@"^(?:[\s\p{P}-[•·▪◦‣⁃*\-–—]]){0,3}(?<kind>(?-i:Theorem|Lemma|Proposition|Corollary|Definition|Exercise|Example|Remark))\s*(?<num>(?:[A-Z]\.)?\d+(?:\.\d+)*)(?=\s*[.:]|\s+[(\[])")]
    private static partial Regex StatementHeaderRegex();

    // "… of [4]", "… in Smith (2019)", "… from Smith et al. [3]": the number names a statement in some
    // OTHER work, so it must not resolve to this document's statement of the same number.
    [GeneratedRegex(@"\G\s*(?:of|in|from)\s+(?:\[|[A-Z][\p{L}'-]+(?:\s+et\s+al\.?)?\s*[\[(])")]
    private static partial Regex ForeignCitationRegex();

    // Follow-on numbers of a plural/range mention — "Figures 2 and 3", "Tables 1, 4", "Figs. 3–5"
    // (ranges yield their endpoints). \G anchors each match at the previous one's end. Only applied
    // after a PLURAL kind word: gating on the plural keeps ordinary prose numbers out ("Table 1, 95%
    // of cases" must not produce a phantom Table 95).
    [GeneratedRegex(@"\G\s*(?:,|;|and|&|–|—|to|through|-)\s*(?<num>" + NumberPattern + ")",
        RegexOptions.IgnoreCase)]
    private static partial Regex ContinuationRegex();

    // Caption labels must LEAD the caption text ("Figure 3: ..." / "Tab. 2 — ..."), allowing a little
    // leading punctuation/whitespace noise from text extraction.
    [GeneratedRegex(@"^[\s\p{P}]{0,3}(?<kind>fig(?:ure)?|tab(?:le)?)\.?\s*(?<num>" + NumberPattern + ")",
        RegexOptions.IgnoreCase)]
    private static partial Regex CaptionLabelRegex();

    // Chapter-style sub-numbering ("Figure 3-1", "Table 2-4"): a hyphen immediately (no surrounding
    // space) followed by digits, right after a reference's number. Matched separately from
    // NumberPattern/ContinuationRegex so it never collides with the plural range syntax ("Figures
    // 3-5" still yields two references, not one "3-5"); \G requires the hyphen to sit right at the
    // caller-supplied offset, i.e. no space before it — a spaced/dashed range ("Fig. 2 - Sample",
    // "Figs. 3–5") never matches here.
    [GeneratedRegex(@"\G-(?<sub>\d+[a-z]?)")]
    private static partial Regex ChapterDashSuffixRegex();

    // A detected Caption block binds to a float only when it overlaps it horizontally within this
    // vertical gap (fraction of page height) — a caption whose own float was misdetected must drop
    // its label rather than bind to an unrelated float across the page.
    private const double CaptionMaxGapFraction = 0.15;
    // A Text-role block only counts as a misclassified caption under a tighter version of the same
    // gate (plus caption-style punctuation, below).
    private const double PseudoCaptionMaxGapFraction = 0.08;

    // Cap on synchronous per-call page-index builds: each build may extract a page's text via PDFium
    // on the UI thread, so an unresolved reference on a large fully-analysed document must not build
    // the whole index in one pass. Resolve reports `incomplete` instead; retries (analysis polls /
    // later line advances) continue from the cached progress, a few pages at a time.
    private const int MaxPageBuildsPerResolve = 8;

    // Per-page parsed caption labels, keyed to the exact PageAnalysis instance they were built from so
    // a re-analysed page (different instance in AnalysisCache) is transparently rebuilt.
    private sealed record PageLabels(PageAnalysis Analysis,
        List<(Reference Ref, int TargetBlock, int CaptionBlock)> Labels);
    private readonly Dictionary<int, PageLabels> _pages = [];

    // Negative cache: references that matched nothing when the index covered N analysed pages. A
    // changed analysed-page count (new background results, or a re-analysis trim) invalidates the
    // entry, so late-arriving captions are still found — but steady re-reads of a dangling mention
    // ("see Figure 99" with no such caption) cost a dictionary probe instead of a full scan.
    private readonly Dictionary<(Reference Ref, int Near), int> _noMatch = [];

    public void Clear()
    {
        _pages.Clear();
        _noMatch.Clear();
        _restartEvidence = null;
    }

    /// <summary>All figure/table references mentioned in a text run, in reading order. Plural
    /// mentions expand their continuations ("Figures 2 and 3" yields both); singular mentions expand
    /// only a lettered sub-part series ("Fig 3a and 3b" yields both, since "3b" can't be mistaken for
    /// ordinary prose) — otherwise ordinary prose numbers after a reference stay out. Matches starting
    /// at or beyond <paramref name="startLimit"/> are ignored — pass the current line's length when
    /// the run is "current line + next line", so a mention split across the line break is caught
    /// without firing early for mentions that wholly belong to the next line.</summary>
    public static List<Reference> ParseLine(string text, int startLimit = int.MaxValue)
    {
        // A contents entry ("C.8 Proof of Lemma 3.8 . . . . . . . 41") lists headings; it cites nothing.
        if (DotLeaderAnywhereRegex().IsMatch(text)) return [];
        List<(int Index, Reference Ref)> found = [];
        ParseCaptionedMentions(text, startLimit, found);
        ParseParenthesisedStatements(text, startLimit, found);
        ParseEquationMentions(text, startLimit, found);
        // A result named inside a citation ("[IK99, Lemma 1.15]", "(Smith 2019, Theorem 3)") belongs to
        // the cited work, not this document.
        var citations = CitationSpans(text);
        found.RemoveAll(f => citations.Any(c => f.Index >= c.Start && f.Index < c.End));
        found.Sort((a, b) => a.Index.CompareTo(b.Index));
        return [.. found.Select(f => f.Ref)];
    }

    // "[12, Theorem 3.2]", "[IK99, Lemma 1.15]", "[2, eqs. (5) and (6)]": a bibliography key plus a
    // qualifier naming a result. A bare key ("[12]") names nothing local; an interval ("[0, 1]") is not
    // a citation, so the qualifier must contain a letter or a parenthesis.
    [GeneratedRegex(@"\[\s*(?:see\s+)?(?:\d+|\p{Lu}\p{L}*(?:\+\p{L}*)?\d{2,4}[a-z]?)(?:\s*[-–—]\s*\d+)?\s*,(?<q>[^\[\]]{1,180})\]")]
    private static partial Regex BracketCitationRegex();

    // "(Smith 2019, Theorem 3)", "(Smith et al. 2019, Eq. (4))", "(Coogan & Arcak, 2015, Def. 1)".
    [GeneratedRegex(@"\(\p{Lu}[\p{L}'’\-]+(?:(?:,\s*|\s+)(?:&|and)\s+\p{Lu}[\p{L}'’\-]+|\s+et\s+al\.?)?,?\s+(?:19|20)\d{2}[a-z]?\s*,[^()]*(?:\([^()]*\)[^()]*)*\)")]
    private static partial Regex AuthorYearCitationRegex();

    private static List<(int Start, int End)> CitationSpans(string text)
    {
        List<(int, int)> spans = [];
        foreach (Match m in BracketCitationRegex().Matches(text))
        {
            string q = m.Groups["q"].Value;
            if (q.Any(char.IsLetter) || q.Contains('(')) spans.Add((m.Index, m.Index + m.Length));
        }
        foreach (Match m in AuthorYearCitationRegex().Matches(text))
            spans.Add((m.Index, m.Index + m.Length));
        return spans;
    }

    // "Theorem (3.2)" prints the statement number in parentheses.
    [GeneratedRegex(@"\b(?<kind>(?-i:Theorem|Lemma|Proposition|Corollary|Definition|Exercise|Example|Remark))\s*\(\s*(?<num>\d+(?:\.\d+)*)\s*\)")]
    private static partial Regex ParenthesisedStatementRegex();

    private static void ParseParenthesisedStatements(string text, int startLimit, List<(int Index, Reference Ref)> refs)
    {
        foreach (Match m in ParenthesisedStatementRegex().Matches(text))
        {
            if (m.Index >= startLimit) break;
            refs.Add((m.Index, new Reference(KindOfWord(m.Groups["kind"].Value), NormalizeNumber(m.Groups["num"].Value))));
        }
    }

    private static void ParseCaptionedMentions(string text, int startLimit, List<(int Index, Reference Ref)> refs)
    {
        foreach (Match m in LineReferenceRegex().Matches(text))
        {
            if (m.Index >= startLimit) break;
            string kindWord = m.Groups["kind"].Value;
            string headNumber = m.Groups["num"].Value;
            var kind = KindOfWord(kindWord);
            if (!AcceptNumber(kind, kindWord, headNumber)) continue;
            int endIndex = m.Index + m.Length;
            headNumber = MergeChapterDashSuffix(text, kindWord, headNumber, ref endIndex);
            if (ForeignCitationRegex().IsMatch(text, endIndex)) continue;
            refs.Add((m.Index, new Reference(kind, NormalizeNumber(headNumber))));

            bool plural = IsPluralKindWord(kindWord);
            // Singular kind words still expand a lettered sub-part series ("Fig 3a and 3b") — the
            // letter suffix is what makes it safe: ordinary prose numbers ("2008", "95%") never carry
            // one, so this can't reproduce the "Table 1, 95% of cases" false positive the plural gate
            // exists to avoid.
            bool letterSeries = !plural && EndsWithLetterSuffix(headNumber);
            if (!plural && !letterSeries) continue;

            for (int pos = endIndex;;)
            {
                var c = ContinuationRegex().Match(text, pos);
                if (!c.Success) break;
                string contNumber = c.Groups["num"].Value;
                if (letterSeries && !EndsWithLetterSuffix(contNumber)) break;
                if (!AcceptNumber(kind, kindWord, contNumber)) break;
                refs.Add((c.Index, new Reference(kind, NormalizeNumber(contNumber))));
                pos = c.Index + c.Length;
            }
        }
    }

    // Equation numbers: arabic with optional dotted scope and panel letter ("4", "3.28", "2a"), or an
    // appendix-scoped form ("A.1").
    private const string EquationNumberPattern = @"(?:[A-Z]\.)?\d+(?:\.\d+)*[a-z]?";

    // "Equation (3.12)", "Eq. 4", "equations (7)", "Eqs. (3)–(5)": the word names the target.
    [GeneratedRegex(@"\b(?<cue>eqs?|equations?)\.?\s*\(?\s*(?<num>" + EquationNumberPattern + @")\s*\)?", RegexOptions.IgnoreCase)]
    private static partial Regex EquationCueRegex();

    [GeneratedRegex(@"\G\s*(?:,|;|and|&|–|—|to|through|-)\s*\(?\s*(?<num>" + EquationNumberPattern + @")\s*\)?", RegexOptions.IgnoreCase)]
    private static partial Regex EquationContinuationRegex();

    // A parenthesised number standing on its own — not attached to a preceding word or closing
    // bracket ("O(1)", "AR(1)", "f(0)", "3(2)").
    [GeneratedRegex(@"(?<![\w)\]])\(\s*(?<num>" + EquationNumberPattern + @")\s*\)")]
    private static partial Regex ParenthesisedNumberRegex();

    private static void ParseEquationMentions(string text, int startLimit, List<(int Index, Reference Ref)> refs)
    {
        List<(int Start, int End)> covered = [];
        foreach (Match m in EquationCueRegex().Matches(text))
        {
            if (m.Index >= startLimit) break;
            string number = m.Groups["num"].Value;
            if (!IsPlausibleEquationNumber(number)) continue;
            int end = m.Index + m.Length;
            if (ForeignCitationRegex().IsMatch(text, end)) continue;
            refs.Add((m.Index, new Reference(RefKind.Equation, NormalizeNumber(number))));
            if (m.Groups["cue"].Value.EndsWith('s') || m.Groups["cue"].Value.EndsWith('S'))
            {
                for (int pos = end;;)
                {
                    var c = EquationContinuationRegex().Match(text, pos);
                    if (!c.Success || !IsPlausibleEquationNumber(c.Groups["num"].Value)) break;
                    refs.Add((c.Index, new Reference(RefKind.Equation, NormalizeNumber(c.Groups["num"].Value))));
                    pos = c.Index + c.Length;
                    end = pos;
                }
            }
            covered.Add((m.Index, end));
        }
        foreach (Match m in ParenthesisedNumberRegex().Matches(text))
        {
            if (m.Index >= startLimit) break;
            if (covered.Any(c => m.Index >= c.Start && m.Index < c.End)) continue;
            // "(1) first … ; (2) second" and "(1) f ∈ W": a list marker or proof step opens its clause
            // (line start, or after punctuation). A mention follows the word that points at it.
            if (!FollowsAWord(text, m.Index)) continue;
            // "conditions (1) and (3)", "(1) of Assumption 2.2", "(1)–(3)": numbered items, not equations.
            if (IsItemMarker(text, m)) continue;
            // "Theorem (3.2)", "Section (4)": the parenthesis belongs to the word before it.
            if (NumberedThingWordBeforeRegex().IsMatch(text.AsSpan(0, m.Index))) continue;
            string number = m.Groups["num"].Value;
            if (!IsPlausibleEquationNumber(number)) continue;
            if (ForeignCitationRegex().IsMatch(text, m.Index + m.Length)) continue;
            refs.Add((m.Index, new Reference(RefKind.Equation, NormalizeNumber(number))));
        }
    }

    [GeneratedRegex(@"\b(?:Theorems?|Lemmas?|Propositions?|Corollar(?:y|ies)|Definitions?|Exercises?|Examples?|Remarks?|Fig(?:ures?|s)?|Tab(?:les?|s)?|Sections?|Sec|Chapters?|Appendix|Pages?|pp?)\.?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex NumberedThingWordBeforeRegex();

    // The word a parenthesised number hangs off, skipping a chain of earlier numbers joined by and/or/commas:
    // "verify conditions (1) and (3)" → "conditions" for both.
    [GeneratedRegex(@"(?<w>[A-Za-z]{2,})\s*(?:\(\s*[\w.]+\s*\)\s*(?:,|and|or|&|–|—|-|to)\s*)*$")]
    private static partial Regex GoverningWordRegex();

    [GeneratedRegex(@"^\s*(?:of|in|from)\s+(?:Assumptions?|Conditions?|Theorems?|Lemmas?|Propositions?|Definitions?|Axioms?)\b|^\s*[-–—]\s*\(\s*\d", RegexOptions.IgnoreCase)]
    private static partial Regex ItemMarkerFollowRegex();

    private static readonly HashSet<string> ItemWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "condition", "conditions", "assumption", "assumptions", "item", "items", "property", "properties",
        "case", "cases", "part", "parts", "step", "steps", "claim", "claims", "axiom", "axioms", "point",
        "points", "statement", "statements", "clause", "clauses", "requirement", "requirements",
        "hypothesis", "hypotheses", "criterion", "criteria",
        // a conjunction directly before a number opens a new list item ("…cost, and (2) time saving"):
        // a real chain ("formula (2) and (3)") is skipped back to its word before this check
        "and", "or", "but", "nor", "then", "also", "both", "either", "neither", "plus", "while", "whereas",
        // asymptotic notation and functions applied to a number: "oP (1)", "log (2)", "Var (1)"
        "op", "oo", "ln", "log", "lg", "exp", "sin", "cos", "tan", "max", "min", "sup", "inf", "det",
        "tr", "rank", "dim", "var", "cov", "pr", "std", "deg", "mod", "gcd", "lcm", "lim", "sgn", "abs",
        "diag", "vec", "poly", "polylog", "loglog", "argmax", "argmin", "prob", "mse", "mae",
    };

    private static bool IsItemMarker(string text, Match m)
    {
        var governing = GoverningWordRegex().Match(text, 0, m.Index);
        if (governing.Success && ItemWords.Contains(governing.Groups["w"].Value)) return true;
        return ItemMarkerFollowRegex().IsMatch(text.AsSpan(m.Index + m.Length));
    }

    /// <summary>A mention follows a real word of two or more letters ("formula (2)", "in (2)", "from (1)").
    /// A single letter or a Greek symbol before it is a superscript index ("µ (2)", "π (2)").</summary>
    private static bool FollowsAWord(string text, int index)
    {
        int i = index - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i])) i--;
        int end = i;
        while (i >= 0 && char.IsAsciiLetter(text[i])) i--;
        return end - i >= 2;
    }

    /// <summary>A printed equation number is small and never a year ("(2019)") or a decimal value
    /// ("(0.52)").</summary>
    private static bool IsPlausibleEquationNumber(string number)
    {
        if (number.Length >= 4 && number.All(char.IsAsciiDigit)) return false;
        if (number[0] == '0') return false;
        return true;
    }

    /// <summary>The equation numbers printed in a display-maths block's text: parenthesised numbers
    /// standing on their own. Order as printed; a repeated number appears repeatedly.</summary>
    internal static List<string> EquationTagsIn(string blockText)
        => [.. ParenthesisedNumberRegex().Matches(blockText).Cast<Match>()
            .Select(m => m.Groups["num"].Value).Where(IsPlausibleEquationNumber).Select(NormalizeNumber)];

    [GeneratedRegex(@"^\s*\(\s*(?<num>" + EquationNumberPattern + @")\s*\)")]
    private static partial Regex LeadingTagRegex();

    [GeneratedRegex(@"\(\s*(?<num>" + EquationNumberPattern + @")\s*\)\s*$")]
    private static partial Regex TrailingTagRegex();

    /// <summary>Equation numbers a display-maths block prints: a parenthesised number that opens or
    /// closes one of its lines (right- or left-margin tag). A number in the middle of a line —
    /// "Thus (C.1) and (C.2) give …" — is a mention that the detector merged into the display.</summary>
    private static IEnumerable<string> EdgeTags(string lineText)
    {
        if (TrailingTagRegex().Match(lineText) is { Success: true } end && !AttachedToPrecedingWord(lineText, end.Index))
            yield return NormalizeNumber(end.Groups["num"].Value);
        if (LeadingTagRegex().Match(lineText) is { Success: true } start)
            yield return NormalizeNumber(start.Groups["num"].Value);
    }

    private static bool AttachedToPrecedingWord(string text, int index) => index > 0 && char.IsLetterOrDigit(text[index - 1]);

    private static void AddEquationLabels(PageAnalysis analysis, Func<PageText> text,
        List<(Reference Ref, int TargetBlock, int CaptionBlock)> labels)
    {
        Dictionary<Reference, (int Block, int Count)> found = [];
        for (int i = 0; i < analysis.Blocks.Count; i++)
        {
            var block = analysis.Blocks[i];
            if (block.Role != BlockRole.DisplayMath) continue;
            IEnumerable<string> lineTexts = block.Lines.Count > 0
                ? block.Lines.Select(l => LineText(text(), l) ?? "")
                : [text().ExtractBlockText(block)];
            foreach (string lineText in lineTexts)
                foreach (string tag in EdgeTags(lineText))
                {
                    if (!IsPlausibleEquationNumber(tag)) continue;
                    var reference = new Reference(RefKind.Equation, tag);
                    found[reference] = found.TryGetValue(reference, out var prior) ? (prior.Block, prior.Count + 1) : (i, 1);
                }
        }
        foreach (var (reference, (block, count)) in found)
            if (count == 1 && !labels.Any(l => l.Ref == reference))
                labels.Add((reference, block, block));
    }

    /// <summary>True when <paramref name="kindWord"/> is the plural form ("Figures"/"Tables", any
    /// case) rather than singular ("Figure"/"Fig."/"Table"/"Tab.").</summary>
    private static bool IsPluralKindWord(string kindWord) => kindWord.EndsWith('s') || kindWord.EndsWith('S');

    /// <summary>True when a reference number's trailing character is a sub-part letter ("3a", "4B")
    /// rather than a plain digit — gated on the number also containing a digit, since digitless roman
    /// numerals / bare appendix letters (see <see cref="NumberPattern"/>) are letters throughout and
    /// must not be mistaken for a lettered sub-part.</summary>
    private static bool EndsWithLetterSuffix(string number) =>
        number.Length > 0 && char.IsLetter(number[^1]) && number.Any(char.IsAsciiDigit);

    /// <summary>Extends <paramref name="number"/> with a tight ("Figure 3-1", no surrounding space)
    /// chapter-style hyphen suffix found at <paramref name="endIndex"/>, advancing it past the match.
    /// Skipped for plural kind words (a hyphen there is the range separator, not a chapter number —
    /// see <see cref="ContinuationRegex"/>) and for digitless numbers (roman numerals / appendix
    /// letters don't take chapter sub-numbers).</summary>
    private static string MergeChapterDashSuffix(string text, string kindWord, string number, ref int endIndex)
    {
        if (IsPluralKindWord(kindWord)) return number;
        if (!number.Any(char.IsAsciiDigit)) return number;
        var m = ChapterDashSuffixRegex().Match(text, endIndex);
        if (!m.Success) return number;
        endIndex = m.Index + m.Length;
        return number + "-" + m.Groups["sub"].Value;
    }

    /// <summary>The reference a caption's leading label declares ("Figure 3: ..."), or null when the
    /// text does not start with a figure/table label. With <paramref name="requirePunctuation"/>, the
    /// label must also be followed by caption-style punctuation (":", ".", a dash, or end of text) —
    /// used to accept Text-role blocks as captions without swallowing body sentences like
    /// "Figure 3 shows that...". A plain hyphen counts only as a separator ("Fig. 2 - Sample"), not
    /// a compound ("Figure 3-D printed...").</summary>
    internal static Reference? ParseCaptionLabel(string captionText, bool requirePunctuation = false)
    {
        var m = CaptionLabelRegex().Match(captionText);
        string kindWord = m.Groups["kind"].Value;
        string number = m.Groups["num"].Value;
        if (!m.Success || !AcceptNumber(KindOfWord(kindWord), kindWord, number)) return null;
        int endIndex = m.Index + m.Length;
        number = MergeChapterDashSuffix(captionText, kindWord, number, ref endIndex);
        if (requirePunctuation)
        {
            int i = endIndex;
            while (i < captionText.Length && char.IsWhiteSpace(captionText[i])) i++;
            if (i < captionText.Length)
            {
                char c = captionText[i];
                bool punctuated = c is ':' or '.' or '—' or '–'
                    || (c == '-' && (i + 1 >= captionText.Length || char.IsWhiteSpace(captionText[i + 1])));
                if (!punctuated) return null;
            }
        }
        return new Reference(KindOfWord(kindWord), NormalizeNumber(number));
    }

    /// <summary>The kind a mention's kind word names ("Fig." / "Tables" / "Thm." / "Defs.").</summary>
    private static RefKind KindOfWord(string kindWord)
    {
        string w = kindWord.ToLowerInvariant();
        if (w.StartsWith("fig")) return RefKind.Figure;
        if (w.StartsWith("tab")) return RefKind.Table;
        if (w.StartsWith("th")) return RefKind.Theorem;
        if (w.StartsWith("lem")) return RefKind.Lemma;
        if (w.StartsWith("pro")) return RefKind.Proposition;
        if (w.StartsWith("cor")) return RefKind.Corollary;
        if (w.StartsWith("def")) return RefKind.Definition;
        if (w.StartsWith("exer")) return RefKind.Exercise;
        if (w.StartsWith("ex")) return RefKind.Example;
        return RefKind.Remark;
    }

    /// <summary>Digitless numbers (roman numerals, bare letters) are accepted only for floats, and only
    /// after a capitalised kind word — keeps IEEE "Table II" and appendix "Figure B" while rejecting
    /// the pronoun in "the table I made". Statements always need a digit.</summary>
    private static bool AcceptNumber(RefKind kind, string kindWord, string number)
    {
        foreach (char c in number)
            if (char.IsAsciiDigit(c))
                return true;
        return !IsStatement(kind) && char.IsUpper(kindWord[0]);
    }

    private static string NormalizeNumber(string number) => number.ToLowerInvariant();

    /// <summary>Flat numbers ("3", "2a", "ii") restart in every paper of a proceedings volume and every
    /// chapter of a manuscript-style thesis, so a match this many pages away or further is more likely
    /// another document's float than the one meant. Numbers that carry their own scope ("3.2", "A.1")
    /// are not capped. Beyond the cap the resolver abstains rather than show a plausible wrong float.</summary>
    internal const int DefaultMaxFlatNumberPageDistance = 12;

    public int MaxFlatNumberPageDistance { get; init; } = DefaultMaxFlatNumberPageDistance;

    /// <summary>When true (default) the flat-number cap only applies to documents whose flat labels
    /// restart — the same "Figure 1" defined on pages more than the cap apart, as in a proceedings
    /// volume. A document without such evidence resolves a flat number at any distance. Until every
    /// page is analysed there is no evidence either way, and the cap stays.</summary>
    public bool AdaptiveCap { get; init; } = true;

    private (int Analysed, bool Value)? _restartEvidence;

    /// <summary>True when the document's flat labels may restart, or cannot yet be known. False only
    /// when every page is analysed and no flat label is defined on two pages further apart than the
    /// cap. Indexes the whole document (budgeted: sets <paramref name="incomplete"/> and the caller
    /// retries).</summary>
    private bool LabelsMayRestart(IReferenceDocument doc, ref int budget, ref bool incomplete)
    {
        int analysed = doc.AnalysedPageCount;
        if (analysed < doc.PageCount) return true;
        if (_restartEvidence is { } cached && cached.Analysed == analysed) return cached.Value;

        Dictionary<Reference, (int Min, int Max)> span = [];
        for (int page = 0; page < doc.PageCount; page++)
        {
            if (!doc.TryGetAnalysis(page, out var analysis)) return true;
            if (!_pages.TryGetValue(page, out var entry) || !ReferenceEquals(entry.Analysis, analysis))
            {
                if (budget <= 0) { incomplete = true; return true; }
                budget--;
                _pages[page] = entry = BuildPage(doc, page, analysis);
            }
            foreach (var (label, _, _) in entry.Labels)
            {
                if (!IsFlatNumber(label.Number)) continue;
                span[label] = span.TryGetValue(label, out var s) ? (Math.Min(s.Min, page), Math.Max(s.Max, page)) : (page, page);
            }
        }
        bool restarts = span.Values.Any(v => v.Max - v.Min > MaxFlatNumberPageDistance);
        _restartEvidence = (analysed, restarts);
        return restarts;
    }

    /// <summary>Resolve a reference to its defining caption + figure/table block, scanning analysed
    /// pages outward from <paramref name="nearPage"/> (nearest match wins; the preceding page is tried
    /// before the following one at each distance, since a referenced float most often sits before or
    /// at the reference). A panel mention ("Fig. 2a") resolves to the lettered caption when one exists,
    /// else to the figure its panel belongs to. Returns null when no analysed page carries the label
    /// within reach (see <see cref="MaxFlatNumberPageDistance"/>); <paramref name="incomplete"/> is true
    /// when the scan stopped at a page it had no budget to index — the caller should retry (progress is
    /// cached). The scan never skips such a page to return a farther match: a nearer one may be hiding
    /// there. A complete miss is negative-cached until the set of analysed pages changes.</summary>
    public Target? Resolve(IReferenceDocument doc, Reference reference, int nearPage, out bool incomplete)
    {
        incomplete = false;
        int budget = MaxPageBuildsPerResolve;
        var exact = ResolveExact(doc, reference, nearPage, ref budget, ref incomplete);
        if (exact is not null || incomplete) return exact;
        return PanelParent(reference) is { } parent
            ? ResolveExact(doc, parent, nearPage, ref budget, ref incomplete)
            : null;
    }

    /// <summary>The figure a panel mention belongs to ("2a" → "2", "4.1b" → "4.1"), or null when the
    /// number isn't a digit-led value with a trailing panel letter.</summary>
    private static Reference? PanelParent(Reference reference)
    {
        string n = reference.Number;
        if (n.Length < 2 || !char.IsAsciiLetter(n[^1]) || !char.IsAsciiDigit(n[^2])) return null;
        return reference with { Number = n[..^1] };
    }

    private static bool IsFlatNumber(string number) => !number.Contains('.') && !number.Contains('-');

    private Target? ResolveExact(IReferenceDocument doc, Reference reference, int nearPage,
        ref int budget, ref bool incomplete)
    {
        int analysed = doc.AnalysedPageCount;
        int pageCount = doc.PageCount;
        bool capped = IsFlatNumber(reference.Number) && MaxFlatNumberPageDistance < pageCount - 1;
        int reach = capped ? MaxFlatNumberPageDistance : pageCount - 1;
        // A capped miss only covers the window around nearPage, so it is keyed by it; an uncapped
        // scan covered the whole document and serves every caller.
        var missKey = (reference, capped ? nearPage : -1);
        if (_noMatch.TryGetValue(missKey, out int seenAt) && seenAt == analysed)
            return null;

        // Floats and statements legitimately restart (every paper of a volume has a "Figure 1"), so the
        // nearest definition wins. A display-equation number that is defined twice inside the window is
        // more often a numbered list of conditions typed as display maths than two real equations, so
        // it is ambiguous and shows nothing.
        bool uniqueRequired = IsEquation(reference.Kind);
        Target? first = null;
        bool? mayRestart = null;
        for (int d = 0; d < pageCount; d++)
        {
            if (capped && d > reach)
            {
                // The window is exhausted. A document whose flat labels never restart has no foreign
                // float to mistake this one for, so keep looking outward; a proceedings volume (labels
                // restarting per paper) stops here, as before.
                // An equation already found inside the window still needs the rest of the document checked
                // for a second definition — unless the labels restart, where a far copy is another paper's.
                mayRestart ??= !AdaptiveCap || LabelsMayRestart(doc, ref budget, ref incomplete);
                if (incomplete) return null;
                if (mayRestart.Value) break;
            }
            foreach (int page in d == 0 ? [nearPage] : new[] { nearPage - d, nearPage + d })
            {
                if (page < 0 || page >= pageCount) continue;
                var t = FindOnPage(doc, page, reference, ref budget, ref incomplete);
                if (incomplete) return null;
                if (t is not { } found) continue;
                if (!uniqueRequired) return found;
                if (first is null) first = found;
                else if (first.Value != found) return null;
            }
        }
        if (first is not null) return first;
        _noMatch[missKey] = analysed;
        return null;
    }

    /// <summary>How many places in the document define <paramref name="reference"/> (a panel mention
    /// falls back to its parent figure), indexing every analysed page. More than one means the label
    /// restarts somewhere — the resolver picked the nearest, and an audit should look at that choice.</summary>
    internal int CandidateCount(IReferenceDocument doc, Reference reference)
    {
        int Count(Reference r)
        {
            int n = 0;
            for (int page = 0; page < doc.PageCount; page++)
            {
                if (!doc.TryGetAnalysis(page, out var analysis)) continue;
                if (!_pages.TryGetValue(page, out var entry) || !ReferenceEquals(entry.Analysis, analysis))
                    _pages[page] = entry = BuildPage(doc, page, analysis);
                n += entry.Labels.Count(l => l.Ref == r);
            }
            return n;
        }
        int exact = Count(reference);
        return exact > 0 || PanelParent(reference) is not { } parent ? exact : Count(parent);
    }

    private Target? FindOnPage(IReferenceDocument doc, int page, Reference reference,
        ref int budget, ref bool incomplete)
    {
        if (!doc.TryGetAnalysis(page, out var analysis)) return null;
        if (!_pages.TryGetValue(page, out var entry) || !ReferenceEquals(entry.Analysis, analysis))
        {
            if (budget <= 0)
            {
                incomplete = true;   // not built yet and out of budget — a later call picks it up
                return null;
            }
            budget--;
            _pages[page] = entry = BuildPage(doc, page, analysis);
        }
        foreach (var (r, target, caption) in entry.Labels)
            if (r == reference)
                return new Target(page, target, caption);
        return null;
    }

    /// <summary>Parse every caption block on a page into (label → adjacent figure/table) entries.
    /// Detected <see cref="BlockRole.Caption"/> blocks are taken first; a second pass accepts
    /// Text-role blocks that read like a caption AND hug a float, covering detector misclassification.
    /// Both passes require the float to overlap the caption horizontally within a bounded vertical
    /// gap, so a label whose own float was misdetected is dropped rather than bound to an unrelated
    /// block across the page. Each float is bound to at most one caption, nearest pair first: with
    /// stacked floats whose captions sit below, a caption is slightly nearer the NEXT float than its
    /// own, but that float's own caption is nearer still and claims it. Text extraction is cached per
    /// page (IReferenceDocument.GetText), so rebuilds after the first touch are regex + geometry only.</summary>
    private static PageLabels BuildPage(IReferenceDocument doc, int page, PageAnalysis analysis)
    {
        List<(Reference Ref, int TargetBlock, int CaptionBlock)> labels = [];
        HashSet<int> claimed = [];
        PageText? pageText = null;
        PageText Text() => pageText ??= doc.GetText(page);

        List<(int Block, Reference Ref)> captions = [];
        for (int i = 0; i < analysis.Blocks.Count; i++)
        {
            if (analysis.Blocks[i].Role != BlockRole.Caption) continue;
            if (ParseCaptionLabel(Text().ExtractBlockText(analysis.Blocks[i])) is { } reference)
                captions.Add((i, reference));
        }
        Bind(analysis, captions, analysis.PageHeight * CaptionMaxGapFraction, claimed, labels);

        double pseudoMaxGap = analysis.PageHeight * PseudoCaptionMaxGapFraction;
        List<(int Block, Reference Ref)> pseudo = [];
        for (int i = 0; i < analysis.Blocks.Count; i++)
        {
            if (analysis.Blocks[i].Role != BlockRole.Text) continue;
            // Geometry gate first (cheap): must hug a float of either kind before any text work.
            var fig = FindNearestTarget(analysis, i, RefKind.Figure);
            var tab = FindNearestTarget(analysis, i, RefKind.Table);
            bool nearFig = fig.Index >= 0 && fig.OverlapX && fig.Gap <= pseudoMaxGap;
            bool nearTab = tab.Index >= 0 && tab.OverlapX && tab.Gap <= pseudoMaxGap;
            if (!nearFig && !nearTab) continue;

            if (ParseCaptionLabel(Text().ExtractBlockText(analysis.Blocks[i]), requirePunctuation: true)
                is not { } reference)
                continue;
            if (reference.Kind == RefKind.Figure ? !nearFig : !nearTab) continue;   // kind must match the hugged float
            if (labels.Any(l => l.Ref == reference)) continue;                      // detected captions win
            pseudo.Add((i, reference));
        }
        Bind(analysis, pseudo, pseudoMaxGap, claimed, labels);

        AddStatementLabels(analysis, Text, labels);
        AddEquationLabels(analysis, Text, labels);

        return new PageLabels(analysis, labels);
    }

    [GeneratedRegex(@"(?:\.\s?){5,}|…{2,}")]
    private static partial Regex DotLeaderAnywhereRegex();

    [GeneratedRegex(@"\G.*?(?:(?:\.\s?){4,}|…+|\.{3,})\s*\d+\s*$")]
    private static partial Regex DotLeaderRegex();

    [GeneratedRegex(@"\b(?:Theorem|Lemma|Proposition|Corollary|Definition|Exercise|Example|Remark)\s*\d")]
    private static partial Regex StatementWordRegex();

    /// <summary>Index the numbered statements on a page: any line of a Text block that opens with a
    /// header ("Definition 2.1 (graph).") labels that block — a header may start mid-block when the
    /// detector merged the statement with the paragraph before it. A header that occurs twice on one
    /// page is ambiguous and labels nothing. The block is both target and "caption": the statement
    /// carries its own label.</summary>
    private static void AddStatementLabels(PageAnalysis analysis, Func<PageText> text,
        List<(Reference Ref, int TargetBlock, int CaptionBlock)> labels)
    {
        Dictionary<Reference, (int Block, int Count)> found = [];
        for (int i = 0; i < analysis.Blocks.Count; i++)
        {
            var block = analysis.Blocks[i];
            if (!CanHoldStatementHeader(block.Role)) continue;
            if (!StatementWordRegex().IsMatch(text().ExtractBlockText(block))) continue;   // cheap prefilter
            string? previous = null;
            foreach (var line in block.Lines)
            {
                string? lineText = LineText(text(), line);
                string? before = previous;
                previous = lineText;
                if (lineText is not { Length: > 0 }) continue;
                if (ParseStatementHeader(lineText) is not { } reference) continue;
                // A header starting a block is a header; one starting a later line must follow a line that
                // ended a sentence, or it is a mention that happened to wrap ("…are given in⏎Remark 2.15. We").
                if (before is not null && !EndsSentence(before)) continue;
                found[reference] = found.TryGetValue(reference, out var prior) ? (prior.Block, prior.Count + 1) : (i, 1);
            }
        }
        foreach (var (reference, (block, count)) in found)
            if (count == 1 && !labels.Any(l => l.Ref == reference))
                labels.Add((reference, block, block));
    }

    /// <summary>The statement a line of text opens, e.g. "Definition 2.1 (graph). Every …" →
    /// (Definition, "2.1"); null when the line is not a statement header.</summary>
    internal static Reference? ParseStatementHeader(string lineText)
    {
        var m = StatementHeaderRegex().Match(lineText);
        // A contents entry ("Definition 6.1 . . . . . 12", "Theorem 3.2 …… 41") is not a header.
        if (m.Success && DotLeaderRegex().IsMatch(lineText, m.Index + m.Length)) return null;
        return m.Success
            ? new Reference(KindOfWord(m.Groups["kind"].Value), NormalizeNumber(m.Groups["num"].Value))
            : null;
    }

    // A statement's body is the blocks after its header up to the next statement, heading, "Proof" or
    // float; capped so a mis-detected boundary can't turn the preview into half a page.
    private const int MaxStatementExtentBlocks = 8;

    [GeneratedRegex(@"^[\s\p{P}]{0,3}Proof\b")]
    private static partial Regex ProofStartRegex();

    /// <summary>Index of the last block of the statement whose header is block <paramref name="block"/>
    /// on <paramref name="page"/>: continuation text, list items and display maths up to the next
    /// statement header, heading, proof, float, page furniture, or a jump back up the page (next
    /// column). Equals <paramref name="block"/> when nothing follows. Used to preview the whole
    /// statement rather than just its first block.</summary>
    internal static int StatementExtentEnd(IReferenceDocument doc, int page, int block)
    {
        if (!doc.TryGetAnalysis(page, out var analysis) || block < 0 || block >= analysis.Blocks.Count)
            return block;
        PageText? text = null;
        int last = block;
        for (int i = block + 1; i < analysis.Blocks.Count && i - block <= MaxStatementExtentBlocks; i++)
        {
            var b = analysis.Blocks[i];
            var prev = analysis.Blocks[last].BBox;
            if (b.BBox.Y + 2 < prev.Y + prev.H) break;   // jumped back up: next column
            if (b.Role is BlockRole.DisplayMath or BlockRole.Algorithm) { last = i; continue; }
            if (b.Role != BlockRole.Text) break;
            string firstLine = (text ??= doc.GetText(page)).ExtractBlockText(b).TrimStart();
            if (ParseStatementHeader(firstLine) is not null || ProofStartRegex().IsMatch(firstLine)) break;
            last = i;
        }
        return last;
    }

    /// <summary>Detectors file statement headers under several roles: usually Text, but a short header
    /// line is often typed Heading or Caption. Floats, page furniture and maths never hold one.</summary>
    internal static bool CanHoldStatementHeader(BlockRole role)
        => role is BlockRole.Text or BlockRole.Heading or BlockRole.Caption;

    private static bool EndsSentence(string line)
    {
        string t = line.TrimEnd();
        return t.Length > 0 && t[^1] is '.' or '?' or '!' or ':' or ';' or ')' or ']';
    }

    private static string? LineText(PageText text, LineInfo line)
    {
        float top = line.Y - line.Height / 2f;
        return text.ExtractTextInRect(line.X, top, line.X + line.Width, top + line.Height);
    }

    /// <summary>Bind captions to floats one-to-one, smallest vertical gap first. Only kind-matched
    /// floats that overlap the caption horizontally within <paramref name="maxGap"/> are candidates;
    /// floats in <paramref name="claimed"/> (taken by an earlier pass) are skipped.</summary>
    private static void Bind(PageAnalysis analysis, List<(int Block, Reference Ref)> captions, double maxGap,
        HashSet<int> claimed, List<(Reference Ref, int TargetBlock, int CaptionBlock)> labels)
    {
        List<(double Gap, int Caption, int Float)> pairs = [];
        for (int c = 0; c < captions.Count; c++)
        {
            for (int j = 0; j < analysis.Blocks.Count; j++)
            {
                if (claimed.Contains(j) || !IsFloat(analysis.Blocks[j].Role, captions[c].Ref.Kind)) continue;
                var (gap, overlapX) = Gap(analysis.Blocks[captions[c].Block].BBox, analysis.Blocks[j].BBox);
                if (overlapX && gap <= maxGap) pairs.Add((gap, c, j));
            }
        }
        pairs.Sort((x, y) => x.Gap != y.Gap ? x.Gap.CompareTo(y.Gap)
            : x.Caption != y.Caption ? x.Caption.CompareTo(y.Caption) : x.Float.CompareTo(y.Float));

        HashSet<int> bound = [];
        foreach (var (_, c, j) in pairs)
        {
            if (bound.Contains(c) || !claimed.Add(j)) continue;
            bound.Add(c);
            labels.Add((captions[c].Ref, j, captions[c].Block));
        }
    }

    private static bool IsFloat(BlockRole role, RefKind kind) => kind == RefKind.Figure
        ? role is BlockRole.Figure or BlockRole.Chart
        : role is BlockRole.Table;

    private static (double Gap, bool OverlapX) Gap(BBox cap, BBox b)
        => (Math.Max(0, Math.Max(cap.Y - (b.Y + b.H), b.Y - (cap.Y + cap.H))),
            Math.Min(cap.X + cap.W, b.X + b.W) > Math.Max(cap.X, b.X));

    /// <summary>The text of line <paramref name="lineIdx"/> of a block, followed by the next line so a
    /// mention split across the break ("…see Figure ⏎ 3 shows…") is caught. <paramref name="startLimit"/>
    /// is the first line's length, for <see cref="ParseLine"/>, so a mention wholly on the next line
    /// doesn't fire a line early. Null when the line has no text.</summary>
    internal static string? LineTextWithLookahead(PageText pageText, IReadOnlyList<LineInfo> lines,
        int lineIdx, out int startLimit)
    {
        startLimit = 0;
        string? lineText = LineText(pageText, lines[lineIdx]);
        if (string.IsNullOrEmpty(lineText)) return null;
        startLimit = lineText.Length;
        if (lineIdx + 1 < lines.Count && LineText(pageText, lines[lineIdx + 1]) is { Length: > 0 } next)
            lineText = lineText + " " + next;
        return lineText;
    }

    /// <summary>Back-compat shim for tests: index of the nearest kind-matching float.</summary>
    internal static int NearestTargetBlock(PageAnalysis analysis, int captionIndex, RefKind kind)
        => FindNearestTarget(analysis, captionIndex, kind).Index;

    /// <summary>The figure/table block a caption belongs to: the role-matching block with the smallest
    /// vertical gap to the caption, preferring blocks that overlap it horizontally (captions sit
    /// directly above or below their float; the no-overlap fallback covers side captions and detector
    /// quirks). Index is -1 when the page has no candidate block of the right kind; Gap/OverlapX
    /// describe the chosen candidate.</summary>
    internal static (int Index, double Gap, bool OverlapX) FindNearestTarget(
        PageAnalysis analysis, int captionIndex, RefKind kind)
    {
        var cap = analysis.Blocks[captionIndex].BBox;
        int best = -1;
        double bestGap = 0;
        bool bestOverlap = false;
        double bestScore = double.MaxValue;
        for (int j = 0; j < analysis.Blocks.Count; j++)
        {
            if (j == captionIndex) continue;
            if (!IsFloat(analysis.Blocks[j].Role, kind)) continue;

            var (gap, overlapX) = Gap(cap, analysis.Blocks[j].BBox);
            // Horizontally-overlapping candidates always beat non-overlapping ones; within each tier,
            // the smallest vertical gap wins.
            double score = gap + (overlapX ? 0 : 1e6);
            if (score < bestScore)
            {
                bestScore = score;
                best = j;
                bestGap = gap;
                bestOverlap = overlapX;
            }
        }
        return (best, bestGap, bestOverlap);
    }
}
