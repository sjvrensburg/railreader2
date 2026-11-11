using RailReader2;
using Xunit;

namespace RailReader.Export.Tests.Updates;

public class StartupArgsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rr args ").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string MakePdf(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void QuotedPath_IsUsedAsIs()
    {
        var pdf = MakePdf("Draft - annotated.pdf");
        Assert.Equal(pdf, App.FindStartupDocument([pdf]));
    }

    [Fact]
    public void UnquotedPathWithSpaces_IsJoined()
    {
        var pdf = MakePdf("226204707 Research Write Up Draft - annotated.pdf");
        Assert.Equal(pdf, App.FindStartupDocument(pdf.Split(' ')));
    }

    [Fact]
    public void UnquotedPath_AfterFlagWithValue_IsJoined()
    {
        var pdf = MakePdf("a b.pdf");
        Assert.Equal(pdf, App.FindStartupDocument(["--page", "3", .. pdf.Split(' '), "--rail"]));
    }

    [Fact]
    public void NothingExists_ReturnsNull()
        => Assert.Null(App.FindStartupDocument([Path.Combine(_dir, "missing"), "file.pdf"]));
}
