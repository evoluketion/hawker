using System.Text.Json;
using System.Text.Json.Serialization;
using Hawker.Core.Articles;

namespace Hawker.Core.Layout;

/// <summary>
/// Builds the request and reads the response for the Claude layout editor. Claude sees article metadata and a short
/// excerpt for context, and answers with IDs, sizes, cut points and section names chosen from a fixed list, so it has
/// no way to put its own words into the paper.
/// </summary>
public static class ClaudeLayoutPrompt
{
    /// <summary>Standard newspaper sections Claude may file stories under, in addition to each feed's name.</summary>
    public static readonly IReadOnlyList<string> StandardSections =
    [
        "World", "UK", "US", "Europe", "Asia", "Australia", "Politics", "Business", "Economy", "Technology", "Science",
        "Health", "Environment", "Sport", "Culture", "Arts", "Lifestyle", "Travel", "Opinion", "Local", "More news",
    ];

    private const int ExcerptWords = 60;

    public const string SystemPrompt = """
        You are the layout editor of a small printed newspaper. You arrange articles that have already been chosen; you never
        write, rewrite or summarise anything. Every headline and every word of body text is printed exactly as the source
        published it. Your decisions are only these:

        - lead: the single story for the top of the front page. Pick the most significant, broadly interesting story, preferring
          one with a usable picture (has_image with a reasonably large image) and enough text to fill three columns.
        - sections: group the remaining stories into sections by topic, using only the allowed section names. Order sections by
          importance, and stories within a section from most to least important. Two to five sections usually reads best; avoid
          sections with a single brief.
        - size of each story:
            major = full width, large headline (use for the next two or three most important stories with pictures)
            minor = half width (most stories)
            brief = headline and first paragraph only, in an "In brief" box (very short or less important items)
        - show_image: whether to print the story's picture. Drop pictures that are small (under ~450px wide) for lead or major
          stories, and on minor stories when space is tight.
        - keep_blocks: how many text blocks (paragraphs, quotes, list items) to print before the story is cut with a
          "Continued online" QR code. 0 means print the whole story. Cut at a natural break: after the opening has made the
          story's point, never mid-argument where a quote is being introduced. Rough word budgets: lead ~550, major ~380,
          minor ~220, brief: just the first block. A story only slightly over budget can be printed whole.
          A minor printing more than about 450 words (370 with its picture) is too tall for a half-width column and is set
          full width instead, so either cut it with keep_blocks or make it a major.

        The input lists articles in ranked order (the edition's ranking by recency or popularity); treat rank as a signal of
        importance, not a rule. Place every article exactly once: the lead must not appear again in a section.
        """;

    public static string BuildUserMessage(LayoutRequest request)
    {
        var articles = request.Articles.Select((a, rank) => new ArticleSummary(
            a.Id,
            rank + 1,
            a.Title,
            a.Source,
            a.Published?.ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture),
            a.Score,
            a.CommentCount,
            a.WordCount,
            a.Blocks.Where(b => b.Kind != BlockKind.Figure).Select(b => b.WordCount).ToList(),
            a.LeadImage?.DataUri is not null,
            a.LeadImage?.Width,
            a.LeadImage?.Height,
            Excerpt(a)));

        var payload = new { edition = request.EditionName, articles };
        return "Lay out this edition. Articles (block_words lists the word count of each text block in order):\n\n"
            + JsonSerializer.Serialize(payload, JsonOptions);
    }

    public static IReadOnlyList<string> AllowedSections(LayoutRequest request) =>
        StandardSections.Concat(request.Articles.Select(a => a.Source)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>JSON schema for the structured output. Strict schemas need every property required and no extras.</summary>
    public static Dictionary<string, JsonElement> OutputSchema(LayoutRequest request)
    {
        var ids = request.Articles.Select(a => a.Id).ToArray();
        object Placement(string[] sizes) => new
        {
            type = "object",
            additionalProperties = false,
            required = sizes.Length > 0 ? new[] { "id", "size", "show_image", "keep_blocks" } : ["id", "show_image", "keep_blocks"],
            properties = sizes.Length > 0
                ? (object)new
                {
                    id = new { type = "string", @enum = ids },
                    size = new { type = "string", @enum = sizes },
                    show_image = new { type = "boolean" },
                    keep_blocks = new { type = "integer" },
                }
                : new
                {
                    id = new { type = "string", @enum = ids },
                    show_image = new { type = "boolean" },
                    keep_blocks = new { type = "integer" },
                },
        };

        var schema = new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "lead", "sections" },
            properties = new
            {
                lead = Placement([]),
                sections = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        additionalProperties = false,
                        required = new[] { "name", "stories" },
                        properties = new
                        {
                            name = new { type = "string", @enum = AllowedSections(request) },
                            stories = new { type = "array", items = Placement(["major", "minor", "brief"]) },
                        },
                    },
                },
            },
        };

        return JsonSerializer.SerializeToElement(schema).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    /// <summary>Turns Claude's JSON into a plan. The result still goes through <see cref="LayoutPlanValidator"/>.</summary>
    public static LayoutPlan ParsePlan(string json)
    {
        var response = JsonSerializer.Deserialize<PlanResponse>(json, JsonOptions)
            ?? throw new FormatException("The layout response was empty.");
        if (response.Lead is null)
        {
            throw new FormatException("The layout response has no lead story.");
        }

        static int? Keep(int value) => value <= 0 ? null : value;

        var lead = new LayoutItem(response.Lead.Id, StorySize.Lead, response.Lead.ShowImage, Keep(response.Lead.KeepBlocks));
        var sections = (response.Sections ?? [])
            .Select(s => new LayoutSection(s.Name, (s.Stories ?? [])
                .Select(p => new LayoutItem(p.Id, ParseSize(p.Size), p.ShowImage, Keep(p.KeepBlocks)))
                .ToList()))
            .ToList();

        return new LayoutPlan(lead, sections);
    }

    private static StorySize ParseSize(string? size) => size?.ToLowerInvariant() switch
    {
        "major" => StorySize.Major,
        "brief" => StorySize.Brief,
        _ => StorySize.Minor,
    };

    private static string Excerpt(Article article)
    {
        var first = article.Blocks.FirstOrDefault(b => b.Kind == BlockKind.Paragraph);
        if (first is null)
        {
            return "";
        }

        var words = ArticleHtmlCleaner.PlainText(first.Html).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= ExcerptWords ? string.Join(' ', words) : string.Join(' ', words.Take(ExcerptWords)) + " …";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed record ArticleSummary(
        string Id,
        int Rank,
        string Title,
        string Source,
        string? Published,
        int? Score,
        int? Comments,
        int Words,
        List<int> BlockWords,
        bool HasImage,
        int? ImageWidth,
        int? ImageHeight,
        string Excerpt);

    private sealed record PlanResponse(Placement? Lead, List<SectionResponse>? Sections);

    private sealed record SectionResponse(string Name, List<Placement>? Stories);

    private sealed record Placement(string Id, string? Size, bool ShowImage, int KeepBlocks);
}
