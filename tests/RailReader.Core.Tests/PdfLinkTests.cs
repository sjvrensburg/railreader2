using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class PdfLinkTests
{
    [Fact]
    public void HitTestLink_ReturnsCachedLink()
    {
        var config = new AppConfig();
        var marshaller = new SynchronousThreadMarshaller();
        var factory = TestFixtures.CreatePdfFactory();
        var pdfPath = TestFixtures.GetTestPdfPath();

        var state = new DocumentState(pdfPath, factory.CreatePdfService(pdfPath),
            factory.CreatePdfTextService(), config, marshaller);
        state.LoadPageBitmap();

        // Inject a synthetic link into the cache
        var link = new PdfLink
        {
            Rect = new RectF(100, 200, 300, 220),
            Destination = new PageDestination { PageIndex = 5 },
        };
        state.SetLinks(0, [link]);

        // Hit inside the rect
        var hit = state.HitTestLink(200, 210);
        Assert.NotNull(hit);
        Assert.Same(link, hit);
        var pageDest = Assert.IsType<PageDestination>(hit.Destination);
        Assert.Equal(5, pageDest.PageIndex);

        // Miss outside the rect
        var miss = state.HitTestLink(50, 210);
        Assert.Null(miss);

        state.Dispose();
    }

    [Fact]
    public void HitTestLink_UriDestination()
    {
        var link = new PdfLink
        {
            Rect = new RectF(10, 10, 200, 30),
            Destination = new UriDestination { Uri = "https://example.com" },
        };

        Assert.True(link.Rect.Contains(100, 20));
        Assert.False(link.Rect.Contains(300, 20));

        var uri = Assert.IsType<UriDestination>(link.Destination);
        Assert.Equal("https://example.com", uri.Uri);
    }

    [Fact]
    public void GetOrExtractLinks_CachesResult()
    {
        var config = new AppConfig();
        var marshaller = new SynchronousThreadMarshaller();
        var factory = TestFixtures.CreatePdfFactory();
        var pdfPath = TestFixtures.GetTestPdfPath();

        var state = new DocumentState(pdfPath, factory.CreatePdfService(pdfPath),
            factory.CreatePdfTextService(), config, marshaller);
        state.LoadPageBitmap();

        // First call extracts (test PDF likely has no links)
        var links1 = state.GetOrExtractLinks(0);
        Assert.NotNull(links1);

        // Second call returns same cached instance
        var links2 = state.GetOrExtractLinks(0);
        Assert.Same(links1, links2);

        state.Dispose();
    }
}
