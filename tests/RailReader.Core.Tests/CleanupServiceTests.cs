using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class CleanupServiceTests
{
    [Fact]
    public void FormatReport_ZeroFiles_ReturnsNothingMessage()
    {
        var result = CleanupService.FormatReport(0, 0);
        Assert.Equal("Nothing to clean up.", result);
    }

    [Fact]
    public void FormatReport_WithFiles_IncludesCountAndSize()
    {
        var result = CleanupService.FormatReport(3, 2048);

        Assert.Contains("3 file(s)", result);
        Assert.Contains("2.0 KB", result);
    }

    [Fact]
    public void FormatReport_SingleFile_IncludesFileCount()
    {
        var result = CleanupService.FormatReport(1, 512);

        Assert.Contains("1 file(s)", result);
    }
}
