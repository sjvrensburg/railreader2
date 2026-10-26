using RailReader.Core;
using RailReader.Core.Models;
using RailReader2.References;

namespace RailReader2.Services;

/// <summary>Adapts a Core <see cref="DocumentModel"/> to the resolver's document view.</summary>
internal sealed class DocumentModelReferenceSource(DocumentModel doc) : IReferenceDocument
{
    public int PageCount => doc.PageCount;
    public int AnalysedPageCount => doc.AnalysedPageCount;
    public bool TryGetAnalysis(int page, out PageAnalysis analysis) => doc.TryGetAnalysis(page, out analysis);
    public PageText GetText(int page) => doc.GetOrExtractText(page);
}
