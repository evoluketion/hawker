using System.Globalization;
using Hawker.Core.Articles;
using Hawker.Core.Domain;
using Hawker.Core.Feeds;
using Hawker.Core.Layout;
using QRCoder;

namespace Hawker.Web.Rendering;

/// <summary>Everything the newspaper template prints, already cut to length.</summary>
public sealed record NewspaperModel(
    string EditionName,
    DateTimeOffset LocalTime,
    int IssueNumber,
    PaperSize PaperSize,
    StoryView Lead,
    IReadOnlyList<SectionView> Sections)
{
    public int StoryCount => 1 + Sections.Sum(s => s.StoryCount);
}

public sealed record SectionView(string Name, IReadOnlyList<StoryView> Majors, IReadOnlyList<StoryView> Minors, IReadOnlyList<StoryView> Briefs)
{
    public int StoryCount => Majors.Count + Minors.Count + Briefs.Count;
}

public sealed record StoryView(
    Article Article,
    StorySize Size,
    ArticleImage? Image,
    IReadOnlyList<BlockView> Blocks,
    bool Truncated,
    string? QrSvg,
    string ShortUrl,
    string MetaLine);

/// <param name="Image">Set for figure blocks.</param>
public sealed record BlockView(BlockKind Kind, string Html, ArticleImage? Image);

public static class NewspaperModelBuilder
{
    public static NewspaperModel Build(
        string editionName,
        int issueNumber,
        PaperSize paperSize,
        DateTimeOffset now,
        TimeZoneInfo timeZone,
        LayoutPlan plan,
        IReadOnlyList<Article> articles)
    {
        var byId = articles.ToDictionary(a => a.Id);
        StoryView Story(LayoutItem item) => BuildStory(item, byId[item.ArticleId], timeZone);

        var sections = plan.Sections
            .Select(s => new SectionView(
                s.Name,
                s.Items.Where(i => i.Size is StorySize.Major or StorySize.Lead).Select(Story).ToList(),
                s.Items.Where(i => i.Size == StorySize.Minor).Select(Story).ToList(),
                s.Items.Where(i => i.Size == StorySize.Brief).Select(Story).ToList()))
            .ToList();

        return new NewspaperModel(editionName, TimeZoneInfo.ConvertTime(now, timeZone), issueNumber, paperSize, Story(plan.Lead), sections);
    }

    private static StoryView BuildStory(LayoutItem item, Article article, TimeZoneInfo timeZone)
    {
        var bodyImages = article.BodyImages.ToDictionary(i => i.Id);
        // Body pictures only fit in the wide stories; narrow ones keep just their text.
        var allowFigures = item.Size is StorySize.Lead or StorySize.Major;

        var blocks = new List<BlockView>();
        var textBlocks = 0;
        var truncated = false;
        foreach (var block in article.Blocks)
        {
            if (block.Kind == BlockKind.Figure)
            {
                if (allowFigures && block.ImageId is not null && bodyImages.TryGetValue(block.ImageId, out var image))
                {
                    blocks.Add(new BlockView(block.Kind, "", image));
                }

                continue;
            }

            if (item.KeepBlocks is { } keep && textBlocks == keep)
            {
                truncated = true;
                break;
            }

            blocks.Add(new BlockView(block.Kind, block.Html, null));
            textBlocks++;
        }

        // A figure stranded after the last kept paragraph would end the story on a picture.
        while (blocks.Count > 0 && blocks[^1].Kind == BlockKind.Figure && truncated)
        {
            blocks.RemoveAt(blocks.Count - 1);
        }

        return new StoryView(
            article,
            item.Size,
            item.ShowImage ? article.LeadImage : null,
            blocks,
            truncated,
            QrSvg(UrlNormalizer.StripTracking(article.Url)),
            ShortUrl(article.Url),
            MetaLine(article, timeZone));
    }

    /// <summary>"Hacker News · 412 points · 138 comments · By Jo Reporter · 5 Oct, 10:35"</summary>
    public static string MetaLine(Article article, TimeZoneInfo timeZone)
    {
        var parts = new List<string> { article.Source };
        if (article.Score is { } score)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{score:N0} point{(score == 1 ? "" : "s")}"));
        }

        if (article.CommentCount is { } comments)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{comments:N0} comment{(comments == 1 ? "" : "s")}"));
        }

        if (article.Author is { } author)
        {
            parts.Add($"By {author}");
        }

        if (article.Published is { } published)
        {
            parts.Add(TimeZoneInfo.ConvertTime(published, timeZone).ToString("d MMM, HH:mm", CultureInfo.InvariantCulture));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>Host and path without the scheme, for printing under the QR code.</summary>
    public static string ShortUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        var path = uri.AbsolutePath.TrimEnd('/');
        var shortUrl = host + path;
        return shortUrl.Length <= 48 ? shortUrl : shortUrl[..47] + "…";
    }

    public static string QrSvg(string url)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        // ViewBox sizing lets CSS set the printed size.
        return new SvgQRCode(data).GetGraphic(4, "#000000", "#ffffff", drawQuietZones: false, SvgQRCode.SizingMode.ViewBoxAttribute);
    }
}
