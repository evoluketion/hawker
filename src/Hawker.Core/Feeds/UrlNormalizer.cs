using System.Security.Cryptography;
using System.Text;

namespace Hawker.Core.Feeds;

/// <summary>Normalises article URLs so the same story linked slightly differently is recognised as one.</summary>
public static class UrlNormalizer
{
    private static readonly string[] TrackingPrefixes = ["utm_", "mc_", "fbclid", "gclid", "ocid", "cmpid"];

    public static string Normalize(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return url.Trim();
        }

        var query = string.Join('&', KeptParameters(uri.Query.TrimStart('?')).Order(StringComparer.Ordinal));

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www."))
        {
            host = host[4..];
        }

        var path = uri.AbsolutePath.Length > 1 ? uri.AbsolutePath.TrimEnd('/') : "";
        var port = uri.IsDefaultPort ? "" : $":{uri.Port}";

        // http and https versions of a page are treated as the same article.
        return $"{host}{port}{path}{(query.Length > 0 ? "?" + query : "")}";
    }

    /// <summary>Removes tracking parameters, leaving the rest of the URL exactly as it was so it still opens.</summary>
    public static string StripTracking(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            return url;
        }

        var hashAt = url.IndexOf('#');
        var fragment = hashAt < 0 ? "" : url[hashAt..];
        var beforeFragment = hashAt < 0 ? url : url[..hashAt];

        var queryAt = beforeFragment.IndexOf('?');
        if (queryAt < 0)
        {
            return url;
        }

        var query = string.Join('&', KeptParameters(beforeFragment[(queryAt + 1)..]));
        return $"{beforeFragment[..queryAt]}{(query.Length > 0 ? "?" + query : "")}{fragment}";
    }

    private static IEnumerable<string> KeptParameters(string query) =>
        query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !TrackingPrefixes.Any(t => p.StartsWith(t, StringComparison.OrdinalIgnoreCase)));

    public static string Hash(string url) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(url))));
}
