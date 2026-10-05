using System.Text.RegularExpressions;

namespace Hawker.Core.Feeds;

/// <summary>Recognises rolling live-update pages ("– Europe live", /news/live/…), which do not print well.</summary>
public static partial class LiveBlogDetector
{
    public static bool IsLiveBlog(FeedItem item) =>
        LiveTitle().IsMatch(item.Title)
        || (Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) && LivePath().IsMatch(uri.AbsolutePath));

    // "… – Europe live", "… - live", "LIVE: …", "Live updates: …", "… as it happened"
    [GeneratedRegex(@"(\s[–—-]\s(.*\s)?live$)|(^live(\s+updates)?\s*[:|–—-])|(\bas it happened$)", RegexOptions.IgnoreCase)]
    private static partial Regex LiveTitle();

    // A "live" path segment (Guardian /world/live/2026/…, BBC /news/live/…) or an explicit live-updates/live-blog slug.
    // Not any "live-" slug: /culture/live-music is an ordinary story.
    [GeneratedRegex(@"/(live(/|$)|live-?updates?\b|live-?blog\b)", RegexOptions.IgnoreCase)]
    private static partial Regex LivePath();
}
