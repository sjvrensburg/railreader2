using RailReader.Cli;
using Xunit;

namespace RailReader.Export.Tests;

/// <summary>Positional-PDF parsing for the CLI (option values must not be mistaken for the PDF).</summary>
public class CliProgramTests
{
    [Fact]
    public void GetRequiredPdf_SkipsValueTakingOptionValues()
    {
        string pdf = Path.GetTempFileName();
        try
        {
            Assert.Equal(Path.GetFullPath(pdf),
                Program.GetRequiredPdf(["--output-dir", "out", pdf]));
            Assert.Equal(Path.GetFullPath(pdf),
                Program.GetRequiredPdf(["--pages", "1-3", "--output", "x.json", pdf]));
            Assert.Equal(Path.GetFullPath(pdf), Program.GetRequiredPdf([pdf, "--pages", "2"]));
        }
        finally { File.Delete(pdf); }
    }

    [Fact]
    public void GetRequiredPdf_Missing_Throws()
        => Assert.Throws<InvalidOperationException>(() => Program.GetRequiredPdf(["--pages", "1"]));
}
