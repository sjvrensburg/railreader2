using RailReader.Core.Models;

namespace RailReader2.References;

/// <summary>
/// The slice of a document <see cref="ReferenceIndex"/> needs: page count, analysed layout and
/// extracted text. Lets the GUI (backed by <c>DocumentModel</c>) and the CLI (which analyses pages
/// directly) share one resolver.
/// </summary>
internal interface IReferenceDocument
{
    int PageCount { get; }

    /// <summary>Number of pages with a cached layout analysis. A changed value invalidates the
    /// index's negative cache.</summary>
    int AnalysedPageCount { get; }

    bool TryGetAnalysis(int page, out PageAnalysis analysis);

    PageText GetText(int page);
}
