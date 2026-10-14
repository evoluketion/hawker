using System.Text.Json;

namespace Hawker.Web.Updates;

/// <param name="Sha256">The digest GitHub records for the upload, in lowercase hex, when it has one.</param>
public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size, string? Sha256);

/// <summary>A published release, as GitHub's releases API describes it.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string Name, string Notes, Uri PageUrl, DateTimeOffset? PublishedAt, IReadOnlyList<ReleaseAsset> Assets)
{
    public ReleaseAsset? Asset(string name) => Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <returns>Null for drafts, pre-releases and tags that aren't a version number.</returns>
    public static ReleaseInfo? Parse(JsonElement release)
    {
        string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        bool Flag(string name) => release.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

        var tag = Text(release, "tag_name");
        if (Flag("draft") || Flag("prerelease") || AppVersion.Parse(tag) is not { } version
            || !Uri.TryCreate(Text(release, "html_url"), UriKind.Absolute, out var page))
        {
            return null;
        }

        var assets = new List<ReleaseAsset>();
        if (release.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in list.EnumerateArray())
            {
                if (Text(asset, "name") is { } name && Uri.TryCreate(Text(asset, "browser_download_url"), UriKind.Absolute, out var url)
                    && (url.Scheme == Uri.UriSchemeHttps || url.IsLoopback))
                {
                    var digest = Text(asset, "digest");
                    var sha256 = digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? digest[7..].ToLowerInvariant() : null;
                    var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                    assets.Add(new ReleaseAsset(name, url, size, sha256));
                }
            }
        }

        var published = DateTimeOffset.TryParse(Text(release, "published_at"), out var at) ? at : (DateTimeOffset?)null;
        return new ReleaseInfo(version, tag!, Text(release, "name") is { Length: > 0 } title ? title : tag!, Text(release, "body") ?? "", page, published, assets);
    }
}
