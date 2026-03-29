namespace RailReader.Core.Models;

/// <summary>
/// A clickable link region on a PDF page, in page-point space (origin top-left, Y-down).
/// </summary>
public sealed class PdfLink
{
    public required RectF Rect { get; init; }
    public required PdfLinkDestination Destination { get; init; }
}

/// <summary>Discriminated destination for a PDF link.</summary>
public abstract class PdfLinkDestination;

/// <summary>Internal link to another page in the same PDF.</summary>
public sealed class PageDestination : PdfLinkDestination
{
    public required int PageIndex { get; init; }
}

/// <summary>External link to a URL.</summary>
public sealed class UriDestination : PdfLinkDestination
{
    public required string Uri { get; init; }
}
