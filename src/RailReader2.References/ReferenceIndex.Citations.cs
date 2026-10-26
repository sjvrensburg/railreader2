using RailReader.Core.Models;

namespace RailReader2.References;

// Citations: a mention such as "[12]" or "Smith et al. (2019)" resolves to an entry of the document's
// reference list. The list is found by its heading, not by guessing which blocks look like entries.
internal sealed partial class ReferenceIndex
{
    /// <summary>With more than one reference list in the document (a proceedings volume) a citation
    /// belongs to the next list after it; one further than this many pages away is treated as another
    /// paper's.</summary>
    internal const int MaxPagesToReferenceList = 25;

    private sealed record BibState(int Analysed, List<BibRegion> Regions, HashSet<(int Page, int Block)> Blocks);

    private BibState? _bib;
    private readonly Dictionary<int, (PageAnalysis Analysis, List<(int Block, bool IsReferenceHeading)> Headings)> _bibHeadings = [];
    private readonly Dictionary<int, (PageAnalysis Analysis, List<BibEntry> Entries)> _bibEntries = [];

    /// <summary>Index the document's reference lists, spending at most <paramref name="budget"/> page
    /// builds; sets <paramref name="incomplete"/> and returns null when it ran out.</summary>
    private BibState? EnsureBibliography(IReferenceDocument doc, ref int budget, ref bool incomplete)
    {
        int analysed = doc.AnalysedPageCount;
        if (_bib is { } cached && cached.Analysed == analysed) return cached;

        List<BibRegion> regions = [];
        HashSet<(int, int)> blocks = [];
        BibRegion? current = null;
        bool inRegion = false;
        for (int page = 0; page < doc.PageCount; page++)
        {
            if (!doc.TryGetAnalysis(page, out var analysis)) continue;   // a gap: keep the state
            if (!_bibHeadings.TryGetValue(page, out var h) || !ReferenceEquals(h.Analysis, analysis))
            {
                if (budget <= 0) { incomplete = true; return null; }
                budget--;
                _bibHeadings[page] = h = (analysis, FindHeadings(doc, page, analysis));
            }
            bool entersRegion = h.Headings.Any(x => x.IsReferenceHeading);
            List<BibEntry> entries = [];
            if (inRegion || entersRegion)
            {
                if (!_bibEntries.TryGetValue(page, out var e) || !ReferenceEquals(e.Analysis, analysis))
                {
                    if (budget <= 0) { incomplete = true; return null; }
                    budget--;
                    _bibEntries[page] = e = (analysis, FindEntries(doc, page, analysis));
                }
                entries = e.Entries;
            }
            for (int b = 0; b < analysis.Blocks.Count; b++)
            {
                foreach (var (block, isReference) in h.Headings)
                {
                    if (block != b) continue;
                    if (isReference) { current = new BibRegion(page, []); regions.Add(current); inRegion = true; }
                    else inRegion = false;
                }
                if (!inRegion) continue;
                var role = analysis.Blocks[b].Role;
                if (role is BlockRole.Text or BlockRole.Heading) blocks.Add((page, b));
                foreach (var entry in entries)
                    if (entry.Block == b) current!.Entries.Add(entry);
            }
        }
        return _bib = new BibState(analysed, regions, blocks);
    }

    private static List<(int Block, bool IsReferenceHeading)> FindHeadings(IReferenceDocument doc, int page, PageAnalysis analysis)
    {
        List<(int, bool)> found = [];
        PageText? text = null;
        for (int i = 0; i < analysis.Blocks.Count; i++)
        {
            var block = analysis.Blocks[i];
            bool heading = block.Role is BlockRole.Heading or BlockRole.Title;
            bool shortLine = block.Role == BlockRole.Text && block.BBox.H <= 22;
            if (!heading && !shortLine) continue;
            string t = (text ??= doc.GetText(page)).ExtractBlockText(block).Trim();
            if (BibliographyParser.IsReferenceHeading(t)) found.Add((i, true));
            else if (heading) found.Add((i, false));
        }
        return found;
    }

    private static List<BibEntry> FindEntries(IReferenceDocument doc, int page, PageAnalysis analysis)
    {
        List<BibEntry> entries = [];
        var text = doc.GetText(page);
        for (int i = 0; i < analysis.Blocks.Count; i++)
        {
            var block = analysis.Blocks[i];
            if (block.Role != BlockRole.Text) continue;
            string t = text.ExtractBlockText(block);
            IReadOnlyList<string>? lines = null;
            if (block.Lines.Count > 1 && System.Text.RegularExpressions.Regex.Count(t, @"\[\s*\d{1,3}\s*\]") >= 2)
                lines = [.. block.Lines.Select(l => LineText(text, l) ?? "")];
            entries.AddRange(BibliographyParser.ParseBlock(page, i, t, lines));
        }
        return entries;
    }

    /// <summary>True when the block lies inside a reference list the index has already built; such a
    /// block cites nothing of its own (its "[3]" or "Smith (2019)" is an entry, not a mention).</summary>
    internal bool IsBibliographyBlock(int page, int block) => _bib?.Blocks.Contains((page, block)) == true;

    /// <summary>Build the reference lists completely (repeating until the per-call budget allows).</summary>
    internal void PrepareBibliography(IReferenceDocument doc)
    {
        for (int i = 0; i <= doc.PageCount * 2 + 2; i++)
        {
            int budget = MaxPageBuildsPerResolve;
            bool incomplete = false;
            if (EnsureBibliography(doc, ref budget, ref incomplete) is not null || !incomplete) return;
        }
    }

    private Target? ResolveCitation(IReferenceDocument doc, Reference reference, int nearPage, ref int budget, ref bool incomplete)
    {
        if (EnsureBibliography(doc, ref budget, ref incomplete) is not { } bib || bib.Regions.Count == 0) return null;

        // The reference list a citation belongs to is the next one after it; after the last one, the last.
        BibRegion? region = bib.Regions.FirstOrDefault(r => r.StartPage >= nearPage);
        if (region is null) region = bib.Regions[^1];
        else if (bib.Regions.Count > 1 && region.StartPage - nearPage > MaxPagesToReferenceList) return null;

        var matches = MatchEntries(region.Entries, reference.Number);
        return matches.Count == 1 ? new Target(matches[0].Page, matches[0].Block, matches[0].Block) : null;
    }

    private static List<BibEntry> MatchEntries(List<BibEntry> entries, string key)
    {
        int colon = key.IndexOf(':');
        if (colon < 0) return [.. entries.Where(e => e.Label == key)];

        string who = key[..colon], year = key[(colon + 1)..];
        bool etal = who.EndsWith('+');
        int amp = who.IndexOf('&');
        string first = etal ? who[..^1] : amp >= 0 ? who[..amp] : who;
        string? second = amp >= 0 ? who[(amp + 1)..] : null;
        bool suffixed = year.Length > 4;
        return [.. entries.Where(e =>
            e.FirstAuthor == first && e.Year is { } y
            && (suffixed ? y == year : y.StartsWith(year, StringComparison.Ordinal))
            && (etal ? e.AuthorCount >= 2
                : second is not null ? e.AuthorCount == 2 && e.SecondAuthor == second
                : e.AuthorCount == 1))];
    }
}
