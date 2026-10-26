using RailReader2.Services;
using Xunit;

namespace RailReader.Export.Tests;

public class AnnotationColourNamesTests
{
    [Fact]
    public void NamesAreLookedUpIgnoringCaseAndBlankRemoves()
    {
        var p = new AnnotationPreferences();
        Assert.Null(p.NameFor("#FFEB3B"));
        p.SetName("#ffeb3b", "  Definition ");
        Assert.Equal("Definition", p.NameFor("#FFEB3B"));
        Assert.Equal("Definition", p.NameFor("#ffeb3b"));
        p.SetName("#FFEB3B", "  ");
        Assert.Null(p.NameFor("#ffeb3b"));
        Assert.Empty(p.ColourNames);
    }

    [Fact]
    public void NullColourHasNoName() => Assert.Null(new AnnotationPreferences().NameFor(null));
}
