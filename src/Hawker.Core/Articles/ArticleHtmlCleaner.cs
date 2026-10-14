using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Hawker.Core.Articles;

public sealed record CleanedBody(IReadOnlyList<ArticleBlock> Blocks, IReadOnlyList<ArticleImage> Images);

/// <summary>
/// Turns arbitrary article HTML into a flat list of print-safe blocks. Text is kept exactly as published; only markup
/// is removed or simplified. Allowed blocks: paragraphs, headings, quotes, lists, figures and preformatted text.
/// Allowed inline markup: emphasis, strong, sup/sub, code and line breaks. Links keep their text but lose the link.
/// </summary>
public static partial class ArticleHtmlCleaner
{
    private static readonly HashSet<string> Dropped =
    [
        "SCRIPT", "STYLE", "NOSCRIPT", "IFRAME", "FORM", "BUTTON", "INPUT", "SELECT", "TEXTAREA", "LABEL",
        "NAV", "ASIDE", "FOOTER", "HEADER", "SVG", "CANVAS", "VIDEO", "AUDIO", "OBJECT", "EMBED", "TEMPLATE",
        "TABLE", "DIALOG", "MENU", "LINK", "META", "SOURCE", "TRACK", "MAP",
    ];

    private static readonly HashSet<string> Inline =
    [
        "A", "EM", "I", "STRONG", "B", "SPAN", "SUP", "SUB", "CODE", "SMALL", "MARK", "U", "ABBR", "TIME",
        "CITE", "Q", "S", "DEL", "INS", "FONT", "BDI", "BDO", "DFN", "KBD", "SAMP", "VAR", "WBR", "DATA",
    ];

    private static readonly Dictionary<string, string> KeptInline = new()
    {
        ["EM"] = "em", ["I"] = "em", ["CITE"] = "em", ["DFN"] = "em",
        ["STRONG"] = "strong", ["B"] = "strong",
        ["SUP"] = "sup", ["SUB"] = "sub", ["CODE"] = "code", ["KBD"] = "code", ["SAMP"] = "code",
    };

    public static CleanedBody Clean(string? html, Uri baseUri, string imageIdPrefix, string? articleTitle = null)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return new CleanedBody([], []);
        }

        var document = new HtmlParser().ParseDocument($"<!doctype html><html><body>{html}</body></html>");
        var state = new State(baseUri, imageIdPrefix, articleTitle);
        state.Walk(document.Body!);
        state.FlushPending();
        return new CleanedBody(state.Blocks, state.Images);
    }

    private sealed class State(Uri baseUri, string imageIdPrefix, string? articleTitle)
    {
        public List<ArticleBlock> Blocks { get; } = [];
        public List<ArticleImage> Images { get; } = [];

        private readonly StringBuilder _pending = new();
        private readonly List<ArticleBlock> _hoistedFigures = [];

        public void Walk(INode parent)
        {
            foreach (var node in parent.ChildNodes)
            {
                switch (node)
                {
                    case IText text:
                        _pending.Append(Encode(text.Data));
                        break;
                    case IElement element:
                        WalkElement(element);
                        break;
                }
            }
        }

        private void WalkElement(IElement element)
        {
            var tag = element.TagName;
            if (Dropped.Contains(tag) || IsHidden(element))
            {
                return;
            }

            switch (tag)
            {
                case "P":
                    FlushPending();
                    AddTextBlock(BlockKind.Paragraph, "p", RenderInline(element));
                    FlushHoisted();
                    break;
                case "H1" or "H2":
                    FlushPending();
                    AddHeading("h2", RenderInline(element));
                    break;
                case "H3" or "H4" or "H5" or "H6":
                    FlushPending();
                    AddHeading("h3", RenderInline(element));
                    break;
                case "BLOCKQUOTE":
                    FlushPending();
                    AddQuote(element);
                    break;
                case "UL" or "OL":
                    FlushPending();
                    AddList(element);
                    break;
                case "FIGURE":
                    FlushPending();
                    AddFigure(element);
                    break;
                case "IMG" or "PICTURE":
                    FlushPending();
                    AddFigure(element);
                    break;
                case "PRE":
                    FlushPending();
                    var code = element.TextContent.TrimEnd();
                    if (code.Trim().Length > 0)
                    {
                        Blocks.Add(new ArticleBlock(BlockKind.Preformatted, $"<pre>{Escape(code)}</pre>", CountWords(code)));
                    }
                    break;
                case "BR" or "HR":
                    FlushPending();
                    break;
                default:
                    if (Inline.Contains(tag))
                    {
                        _pending.Append(RenderInline(element, wrapSelf: true));
                    }
                    else
                    {
                        // Containers (div, section, article…): text directly inside them still forms paragraphs.
                        FlushPending();
                        Walk(element);
                        FlushPending();
                    }
                    break;
            }
        }

        public void FlushPending()
        {
            if (_pending.Length > 0)
            {
                AddTextBlock(BlockKind.Paragraph, "p", _pending.ToString());
                _pending.Clear();
            }

            FlushHoisted();
        }

        private void FlushHoisted()
        {
            Blocks.AddRange(_hoistedFigures);
            _hoistedFigures.Clear();
        }

        private void AddTextBlock(BlockKind kind, string tag, string innerHtml)
        {
            var inner = Tidy(innerHtml);
            var text = PlainText(inner);
            if (text.Length == 0 || (kind == BlockKind.Paragraph && BareLink().IsMatch(text)))
            {
                return;
            }

            Blocks.Add(new ArticleBlock(kind, $"<{tag}>{inner}</{tag}>", CountWords(PlainText(inner))));
        }

        private void AddHeading(string tag, string innerHtml)
        {
            var text = PlainText(Tidy(innerHtml));
            // The headline is printed separately, so a repeat of it at the top of the body is dropped.
            if (articleTitle is not null && Blocks.Count == 0 && string.Equals(text, articleTitle.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            AddTextBlock(BlockKind.Heading, tag, innerHtml);
        }

        private void AddQuote(IElement quote)
        {
            var inner = new State(baseUri, imageIdPrefix, null);
            inner.Walk(quote);
            inner.FlushPending();
            var paragraphs = inner.Blocks.Where(b => b.Kind is BlockKind.Paragraph or BlockKind.List).ToList();
            if (paragraphs.Count == 0)
            {
                return;
            }

            Blocks.Add(new ArticleBlock(
                BlockKind.Quote,
                $"<blockquote>{string.Concat(paragraphs.Select(p => p.Html))}</blockquote>",
                paragraphs.Sum(p => p.WordCount)));
        }

        private void AddList(IElement list)
        {
            var items = list.Children
                .Where(c => c.TagName == "LI")
                .Select(li => Tidy(RenderInline(li)))
                .Where(li => PlainText(li).Length > 0)
                .ToList();
            if (items.Count == 0)
            {
                return;
            }

            var tag = list.TagName.ToLowerInvariant();
            Blocks.Add(new ArticleBlock(
                BlockKind.List,
                $"<{tag}>{string.Concat(items.Select(i => $"<li>{i}</li>"))}</{tag}>",
                items.Sum(i => CountWords(PlainText(i)))));
        }

        private void AddFigure(IElement element)
        {
            var img = element.TagName == "IMG" ? element : element.QuerySelector("img");
            if (img is null || RegisterImage(img, CaptionFor(element)) is not { } image)
            {
                return;
            }

            Blocks.Add(new ArticleBlock(BlockKind.Figure, "", 0, image.Id));
        }

        private string? CaptionFor(IElement figure)
        {
            if (figure.TagName != "FIGURE" || figure.QuerySelector("figcaption") is not { } caption)
            {
                return null;
            }

            var text = PlainText(Tidy(RenderInline(caption)));
            return text.Length == 0 ? null : text;
        }

        private ArticleImage? RegisterImage(IElement img, string? caption)
        {
            var url = ImageUrl(img);
            if (url is null)
            {
                return null;
            }

            var existing = Images.FirstOrDefault(i => i.SourceUrl == url);
            if (existing is not null)
            {
                return null;
            }

            var alt = img.GetAttribute("alt")?.Trim();
            var image = new ArticleImage
            {
                Id = $"{imageIdPrefix}-{Images.Count + 1}",
                SourceUrl = url,
                Caption = caption ?? (string.IsNullOrEmpty(alt) ? null : alt),
            };
            Images.Add(image);
            return image;
        }

        private string? ImageUrl(IElement img)
        {
            // Lazy-loading sites keep the real image in data-* attributes and a placeholder in src.
            var candidates = new[]
            {
                LargestFromSrcset(img.GetAttribute("srcset") ?? img.GetAttribute("data-srcset")),
                img.GetAttribute("data-src"),
                img.GetAttribute("data-lazy-src"),
                img.GetAttribute("data-original"),
                img.GetAttribute("src"),
            };

            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate) || candidate.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (Uri.TryCreate(baseUri, candidate.Trim(), out var uri) && uri.Scheme is "http" or "https"
                    && !uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    return uri.AbsoluteUri;
                }
            }

            return null;
        }

        private string RenderInline(IElement element, bool wrapSelf = false)
        {
            var sb = new StringBuilder();
            foreach (var node in element.ChildNodes)
            {
                switch (node)
                {
                    case IText text:
                        sb.Append(Encode(text.Data));
                        break;
                    case IElement child when Dropped.Contains(child.TagName) || IsHidden(child):
                        break;
                    case IElement { TagName: "BR" }:
                        sb.Append("<br>");
                        break;
                    case IElement { TagName: "IMG" or "PICTURE" or "FIGURE" } child:
                        // Images inside a paragraph are lifted out and placed after it.
                        var img = child.TagName == "IMG" ? child : child.QuerySelector("img");
                        if (img is not null && RegisterImage(img, CaptionFor(child)) is { } image)
                        {
                            _hoistedFigures.Add(new ArticleBlock(BlockKind.Figure, "", 0, image.Id));
                        }
                        break;
                    case IElement child:
                        sb.Append(RenderInline(child, wrapSelf: true));
                        break;
                }
            }

            var inner = sb.ToString();
            return wrapSelf && KeptInline.TryGetValue(element.TagName, out var tag) && inner.Trim().Length > 0
                ? $"<{tag}>{inner}</{tag}>"
                : inner;
        }
    }

    private static bool IsHidden(IElement element) =>
        element.HasAttribute("hidden")
        || element.GetAttribute("aria-hidden") == "true"
        || (element.GetAttribute("style")?.Replace(" ", "").Contains("display:none", StringComparison.OrdinalIgnoreCase) ?? false);

    private static string? LargestFromSrcset(string? srcset)
    {
        if (string.IsNullOrWhiteSpace(srcset))
        {
            return null;
        }

        return srcset.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => entry.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length > 0)
            .Select(parts => (Url: parts[0], Size: parts.Length > 1 && double.TryParse(parts[1].TrimEnd('w', 'x'), out var s) ? s : 0))
            .OrderByDescending(e => e.Size)
            .Select(e => e.Url)
            .FirstOrDefault();
    }

    /// <summary>Escapes only what HTML requires, so quotes and accented letters stay readable in the stored body.</summary>
    private static string Encode(string text) => Escape(WhiteSpace().Replace(text, " "));

    private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Collapses whitespace, trims, and removes leading/trailing line breaks.</summary>
    private static string Tidy(string html)
    {
        var tidy = WhiteSpace().Replace(html, " ").Trim();
        tidy = EdgeBreaks().Replace(tidy, "");
        return tidy.Trim();
    }

    public static string PlainText(string html) =>
        WhiteSpace().Replace(WebUtility.HtmlDecode(Tag().Replace(html.Replace("<br>", " ").Replace("</li>", " "), "")), " ").Trim();

    public static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

    // A paragraph that is only an address, often an embed the site printed as escaped markup: <a href="…">…</a>.
    [GeneratedRegex(@"^(<a\s[^>]*>)?\s*https?://\S+?\s*(</a>)?$", RegexOptions.IgnoreCase)]
    private static partial Regex BareLink();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"^(\s*<br>\s*)+|(\s*<br>\s*)+$")]
    private static partial Regex EdgeBreaks();
}
