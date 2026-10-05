using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Hawker.Core.Feeds;

/// <summary>
/// Reads RSS 2.0, RSS 1.0 (RDF) and Atom. Written by hand rather than using System.ServiceModel.Syndication,
/// which throws on the non-standard dates many real feeds publish and hides content:encoded and media:* elements.
/// </summary>
public static partial class FeedParser
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Rss1 = "http://purl.org/rss/1.0/";
    private static readonly XNamespace Content = "http://purl.org/rss/1.0/modules/content/";
    private static readonly XNamespace Media = "http://search.yahoo.com/mrss/";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    public static IReadOnlyList<FeedItem> Parse(Stream stream, Uri feedUri, int feedId = 0, string feedName = "")
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var reader = XmlReader.Create(stream, settings);
        var doc = XDocument.Load(reader);
        var root = doc.Root ?? throw new FormatException("Feed has no root element.");

        IEnumerable<FeedItem?> items = root.Name.LocalName switch
        {
            "rss" => root.Element("channel")?.Elements("item").Select(e => ParseRssItem(e, feedUri)) ?? [],
            "RDF" => root.Elements(Rss1 + "item").Select(e => ParseRss1Item(e, feedUri)),
            "feed" when root.Name.Namespace == Atom => root.Elements(Atom + "entry").Select(e => ParseAtomEntry(e, feedUri)),
            _ => throw new FormatException($"Unrecognised feed format <{root.Name.LocalName}>."),
        };

        return items.OfType<FeedItem>().Select(i => i with { FeedId = feedId, FeedName = feedName }).ToList();
    }

    private static FeedItem? ParseRssItem(XElement item, Uri baseUri)
    {
        var url = Resolve(baseUri, Text(item.Element("link")))
            ?? (item.Element("guid") is { } guid && (string?)guid.Attribute("isPermaLink") != "false" ? Resolve(baseUri, Text(guid)) : null);
        var title = Title(item.Element("title"));
        if (url is null || title is null)
        {
            return null;
        }

        return new FeedItem
        {
            Title = title,
            Url = url,
            Author = Text(item.Element(Dc + "creator")) ?? Text(item.Element("author")),
            Published = ParseDate(Text(item.Element("pubDate")) ?? Text(item.Element(Dc + "date"))),
            SummaryHtml = Text(item.Element("description")),
            ContentHtml = Text(item.Element(Content + "encoded")),
            ImageUrl = Resolve(baseUri, FindImage(item)),
            CommentsUrl = Resolve(baseUri, Text(item.Element("comments"))),
        };
    }

    private static FeedItem? ParseRss1Item(XElement item, Uri baseUri)
    {
        var url = Resolve(baseUri, Text(item.Element(Rss1 + "link")));
        var title = Title(item.Element(Rss1 + "title"));
        if (url is null || title is null)
        {
            return null;
        }

        return new FeedItem
        {
            Title = title,
            Url = url,
            Author = Text(item.Element(Dc + "creator")),
            Published = ParseDate(Text(item.Element(Dc + "date"))),
            SummaryHtml = Text(item.Element(Rss1 + "description")),
            ContentHtml = Text(item.Element(Content + "encoded")),
            ImageUrl = Resolve(baseUri, FindImage(item)),
        };
    }

    private static FeedItem? ParseAtomEntry(XElement entry, Uri baseUri)
    {
        var links = entry.Elements(Atom + "link").ToList();
        var link = links.FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate") ?? links.FirstOrDefault();
        var url = Resolve(baseUri, (string?)link?.Attribute("href"));
        var title = Title(entry.Element(Atom + "title"));
        if (url is null || title is null)
        {
            return null;
        }

        var imageLink = links.FirstOrDefault(l => (string?)l.Attribute("rel") == "enclosure" && IsImageType((string?)l.Attribute("type")));
        var repliesLink = links.FirstOrDefault(l => (string?)l.Attribute("rel") == "replies" && (string?)l.Attribute("type") is null or "text/html");

        return new FeedItem
        {
            Title = title,
            Url = url,
            Author = Text(entry.Element(Atom + "author")?.Element(Atom + "name")),
            Published = ParseDate(Text(entry.Element(Atom + "published")) ?? Text(entry.Element(Atom + "updated"))),
            SummaryHtml = Text(entry.Element(Atom + "summary")),
            ContentHtml = Text(entry.Element(Atom + "content")),
            ImageUrl = Resolve(baseUri, (string?)imageLink?.Attribute("href") ?? FindImage(entry)),
            CommentsUrl = Resolve(baseUri, (string?)repliesLink?.Attribute("href")),
        };
    }

    private static string? FindImage(XElement item)
    {
        // Some feeds (e.g. The Guardian) give media:content no medium or type, in several sizes; take the widest.
        var mediaContent = Widest(item.Descendants(Media + "content").Where(m =>
            (string?)m.Attribute("medium") is var medium && (string?)m.Attribute("type") is var type
            && (medium == "image" || IsImageType(type) || (medium is null && type is null))));
        if ((string?)mediaContent?.Attribute("url") is { } contentUrl)
        {
            return contentUrl;
        }

        if ((string?)Widest(item.Descendants(Media + "thumbnail"))?.Attribute("url") is { } thumbnailUrl)
        {
            return thumbnailUrl;
        }

        var enclosure = item.Elements("enclosure").FirstOrDefault(e => IsImageType((string?)e.Attribute("type")));
        return (string?)enclosure?.Attribute("url");
    }

    private static XElement? Widest(IEnumerable<XElement> media) =>
        media.Where(m => m.Attribute("url") is not null)
            .OrderByDescending(m => int.TryParse((string?)m.Attribute("width"), out var w) ? w : 0)
            .FirstOrDefault();

    private static bool IsImageType(string? mimeType) => mimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;

    private static string? Text(XElement? element)
    {
        if (element is null)
        {
            return null;
        }

        // Atom type="xhtml" content is inline markup rather than escaped text.
        var value = (string?)element.Attribute("type") == "xhtml"
            ? string.Concat(element.Nodes().Select(n => n.ToString()))
            : element.Value;

        value = value.Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>Titles are printed as plain text, but feeds often put markup or HTML entities in them.</summary>
    private static string? Title(XElement? element)
    {
        if (Text(element) is not { } raw)
        {
            return null;
        }

        var text = WebUtility.HtmlDecode(Tag().Replace(raw, ""));
        text = WhiteSpace().Replace(text, " ").Trim();
        return text.Length == 0 ? null : text;
    }

    private static string? Resolve(Uri baseUri, string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(baseUri, url.Trim(), out var resolved))
        {
            return null;
        }

        return resolved.Scheme is "http" or "https" ? resolved.AbsoluteUri : null;
    }

    private static readonly string[] Rfc822Formats =
    [
        "d MMM yyyy HH:mm:ss zzz",
        "d MMM yyyy HH:mm zzz",
        "d MMM yy HH:mm:ss zzz",
        "d MMM yyyy HH:mm:ss",
        "d MMMM yyyy HH:mm:ss zzz",
    ];

    private static readonly Dictionary<string, string> ZoneAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UT"] = "+00:00", ["UTC"] = "+00:00", ["GMT"] = "+00:00", ["Z"] = "+00:00",
        ["EST"] = "-05:00", ["EDT"] = "-04:00", ["CST"] = "-06:00", ["CDT"] = "-05:00",
        ["MST"] = "-07:00", ["MDT"] = "-06:00", ["PST"] = "-08:00", ["PDT"] = "-07:00",
        ["BST"] = "+01:00", ["CET"] = "+01:00", ["CEST"] = "+02:00",
        ["AEST"] = "+10:00", ["AEDT"] = "+11:00",
    };

    /// <summary>Parses RFC 822 dates (with the usual real-world variations) and ISO 8601 dates.</summary>
    public static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();

        // ISO 8601 (Atom, dc:date) first: it is unambiguous.
        if (char.IsDigit(value[0]) && value.Length >= 10 && value[4] == '-'
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var iso))
        {
            return iso;
        }

        // RFC 822: drop the optional day name, then normalise the zone into +hh:mm.
        var rfc = DayNamePrefix().Replace(value, "");
        rfc = WhiteSpace().Replace(rfc, " ");
        var zoneMatch = TrailingZone().Match(rfc);
        if (zoneMatch.Success)
        {
            var zone = zoneMatch.Groups["zone"].Value;
            var offset = ZoneAbbreviations.TryGetValue(zone, out var known)
                ? known
                : zone.Length == 5 ? $"{zone[..3]}:{zone[3..]}" : zone;
            rfc = rfc[..zoneMatch.Index] + " " + offset;
        }

        if (DateTimeOffset.TryParseExact(rfc, Rfc822Formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var fallback)
            ? fallback
            : null;
    }

    [GeneratedRegex(@"^[A-Za-z]{3,9},?\s*")]
    private static partial Regex DayNamePrefix();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

    [GeneratedRegex(@"\s(?<zone>[+-]\d{4}|[+-]\d{2}:\d{2}|[A-Za-z]{1,4})$")]
    private static partial Regex TrailingZone();
}
