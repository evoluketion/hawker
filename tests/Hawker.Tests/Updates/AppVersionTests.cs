using Hawker.Web.Updates;

namespace Hawker.Tests.Updates;

public class AppVersionTests
{
    [Theory]
    [InlineData("0.2.1", "0.2.1")]
    [InlineData("v0.3.0", "0.3.0")]
    [InlineData("V1.2", "1.2.0")]
    [InlineData("1.4.0-beta.2", "1.4.0")]
    [InlineData("0.2.1+82c4cf8a0ff8af449571fe895088db29dc6034c8", "0.2.1")]
    [InlineData("1.2.3.4", "1.2.3")]
    public void Reads_tags_and_informational_versions(string text, string expected) =>
        Assert.Equal(expected, AppVersion.Display(AppVersion.Parse(text)!));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("release-7")]
    public void Anything_else_is_not_a_version(string? text) => Assert.Null(AppVersion.Parse(text));

    [Fact]
    public void Compares_numerically() => Assert.True(AppVersion.Parse("0.10.0") > AppVersion.Parse("v0.9.9"));
}
