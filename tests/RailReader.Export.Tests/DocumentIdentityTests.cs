using Xunit;
using RailReader2.Services;

namespace RailReader.Export.Tests;

public sealed class DocumentIdentityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("docid").FullName;

    public DocumentIdentityTests()
    {
        DocumentIdentity.MapPathOverride = Path.Combine(_dir, "ids.json");
        DocumentIdentity.ResetForTests();
    }

    public void Dispose()
    {
        DocumentIdentity.MapPathOverride = null;
        DocumentIdentity.ResetForTests();
        Directory.Delete(_dir, true);
    }

    private string Write(string name, string content)
    {
        string p = Path.Combine(_dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void MovedAndCopiedFilesKeepTheirId()
    {
        string a = Write("a.pdf", "same bytes");
        string id = DocumentIdentity.Resolve(a);
        string moved = Path.Combine(_dir, "moved.pdf");
        File.Move(a, moved);
        string copy = Write("copy.pdf", "same bytes");
        Assert.Equal(id, DocumentIdentity.Resolve(moved));
        Assert.Equal(id, DocumentIdentity.Resolve(copy));
    }

    [Fact]
    public void DifferentContentGetsADifferentId()
        => Assert.NotEqual(DocumentIdentity.Resolve(Write("a.pdf", "one")), DocumentIdentity.Resolve(Write("b.pdf", "two")));

    [Fact]
    public void RewritingInPlaceKeepsTheIdAndTheNewBytesAreAnAlias()
    {
        string a = Write("a.pdf", "original");
        string id = DocumentIdentity.Resolve(a);
        File.WriteAllText(a, "original plus an annotation");
        File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddMinutes(5));
        Assert.Equal(id, DocumentIdentity.Resolve(a));
        string copyOfRewritten = Write("c.pdf", "original plus an annotation");
        Assert.Equal(id, DocumentIdentity.Resolve(copyOfRewritten));
    }

    [Fact]
    public void AnUnchangedFileIsNotRehashedAndPeekNeedsNoFile()
    {
        string a = Write("a.pdf", "x");
        string id = DocumentIdentity.Resolve(a);
        Assert.Equal(id, DocumentIdentity.Peek(a));
        File.Delete(a);
        Assert.Equal(id, DocumentIdentity.Peek(a));
        Assert.Null(DocumentIdentity.Peek(Path.Combine(_dir, "never.pdf")));
    }

    [Fact]
    public void TheMapSurvivesARestart()
    {
        string a = Write("a.pdf", "persist me");
        string id = DocumentIdentity.Resolve(a);
        DocumentIdentity.ResetForTests();
        Assert.Equal(id, DocumentIdentity.Peek(a));
        Assert.Contains(Path.GetFullPath(a), DocumentIdentity.KnownPaths(id));
    }
}
