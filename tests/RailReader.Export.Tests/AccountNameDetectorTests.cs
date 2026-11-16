using RailReader2.Services;
using Xunit;

namespace RailReader.Export.Tests;

public class AccountNameDetectorTests
{
    [Theory]
    [InlineData("Jane Q. Example,Room 4,555-0100,,", "Jane Q. Example")]
    [InlineData("  Jane Example  ", "Jane Example")]
    [InlineData(",,,", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("root", null)]
    [InlineData("12345", null)]
    public void FromGecos_TakesTheFullNameField(string? gecos, string? expected)
        => Assert.Equal(expected, AccountNameDetector.FromGecos(gecos));

    [Theory]
    [InlineData("Administrator", null)]
    [InlineData("user", null)]
    [InlineData("jexample", "jexample")]
    public void Clean_DropsPlaceholderAccounts(string name, string? expected)
        => Assert.Equal(expected, AccountNameDetector.Clean(name));

    [Fact]
    public void Detect_NeverThrows_AndIsCached()
        => Assert.Equal(AccountNameDetector.Detect(), AccountNameDetector.Detect());
}
