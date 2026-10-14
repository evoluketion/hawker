using System.Text.Json;
using Hawker.Web.Updates;

namespace Hawker.Tests.Updates;

public class ReleaseInfoTests
{
    private static ReleaseInfo? Parse(string json) => ReleaseInfo.Parse(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Reads_version_notes_page_and_https_assets()
    {
        var release = Parse(UpdateFixture.Release("v0.3.0"))!;

        Assert.Equal(new Version(0, 3, 0), release.Version);
        Assert.Equal("v0.3.0", release.Tag);
        Assert.Equal("Hawker v0.3.0", release.Name);
        Assert.Contains("Keeps headlines whole", release.Notes);
        Assert.Equal("https://github.com/evoluketion/hawker/releases/tag/v0.3.0", release.PageUrl.AbsoluteUri);
        Assert.Equal(["hawker-win-x64.zip", "hawker-linux-x64.tar.gz"], release.Assets.Select(a => a.Name));
        Assert.Equal("abcdef", release.Asset("HAWKER-WIN-X64.ZIP")!.Sha256);
        Assert.Null(release.Asset("hawker-linux-x64.tar.gz")!.Sha256);
        Assert.Equal(2000, release.Asset("hawker-linux-x64.tar.gz")!.Size);
    }

    [Fact]
    public void Drafts_prereleases_and_unversioned_tags_are_ignored()
    {
        Assert.Null(Parse(UpdateFixture.Release("0.3.0", draft: true)));
        Assert.Null(Parse(UpdateFixture.Release("0.3.0", prerelease: true)));
        Assert.Null(Parse(UpdateFixture.Release("nightly")));
        Assert.Null(Parse("""{ "message": "Not Found" }"""));
    }
}
