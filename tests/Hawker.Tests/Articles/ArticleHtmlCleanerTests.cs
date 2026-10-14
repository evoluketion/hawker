using Hawker.Core.Articles;

namespace Hawker.Tests.Articles;

public class ArticleHtmlCleanerTests
{
    private static readonly Uri Base = new("https://news.example.com/2026/10/story");

    private static CleanedBody Clean(string html, string? title = null) => ArticleHtmlCleaner.Clean(html, Base, "a1", title);

    private static List<string> Html(CleanedBody body) => body.Blocks.Select(b => b.Html).ToList();

    [Fact]
    public void Keeps_text_exactly_and_simplifies_markup()
    {
        var body = Clean("""
            <p class="lead" style="color:red">The council voted <b>7–2</b> on <a href="/x">Tuesday’s</a> “bridge” plan &amp; <i>more</i>.</p>
            """);

        Assert.Equal(["<p>The council voted <strong>7–2</strong> on Tuesday’s “bridge” plan &amp; <em>more</em>.</p>"], Html(body));
        Assert.Equal(10, body.Blocks[0].WordCount);
    }

    [Fact]
    public void Escapes_only_what_html_requires()
    {
        var body = Clean("<p>S&aacute;nchez said &quot;it&#39;s 5 &lt; 6&quot; &middot; caf&eacute;</p>");

        Assert.Equal("<p>Sánchez said \"it's 5 &lt; 6\" · café</p>", body.Blocks[0].Html);
    }

    [Fact]
    public void Drops_scripts_forms_embeds_and_hidden_elements_entirely()
    {
        var body = Clean("""
            <p>Kept.</p>
            <script>alert('x')</script>
            <style>p{}</style>
            <form><input value="Subscribe"><button>Go</button></form>
            <iframe src="https://video.example.com"></iframe>
            <aside>Read more: other story</aside>
            <nav>Home | News</nav>
            <table><tr><td>cell</td></tr></table>
            <div hidden>Hidden text</div>
            <p style="display: none">Also hidden</p>
            <span aria-hidden="true">icon</span>
            """);

        Assert.Equal(["<p>Kept.</p>"], Html(body));
    }

    [Fact]
    public void Loose_text_in_containers_becomes_paragraphs()
    {
        var body = Clean("First line<br><br>Second line<div>In a div <em>with</em> emphasis</div>Tail");

        Assert.Equal(
            ["<p>First line</p>", "<p>Second line</p>", "<p>In a div <em>with</em> emphasis</p>", "<p>Tail</p>"],
            Html(body));
    }

    [Fact]
    public void Empty_paragraphs_are_dropped()
    {
        var body = Clean("<p>&nbsp;</p><p> <br> </p><p><span></span></p><p>Real</p>");

        Assert.Equal(["<p>Real</p>"], Html(body));
    }

    [Fact]
    public void Paragraphs_that_are_only_a_link_address_are_dropped()
    {
        var body = Clean("""
            <p>&lt;a href="https://medium.com/media/e93d74e6/href"&gt;https://medium.com/media/e93d74e6/href&lt;/a&gt;</p>
            <p><a href="https://example.com/x">https://example.com/x</a></p>
            <p>See https://example.com/report for the full figures.</p>
            """);

        Assert.Equal(["<p>See https://example.com/report for the full figures.</p>"], Html(body));
    }

    [Fact]
    public void Headings_quotes_lists_and_code_are_kept_as_blocks()
    {
        var body = Clean("""
            <h1>Section one</h1>
            <h4>Detail</h4>
            <blockquote><p>We will build it.</p><p>Soon.</p></blockquote>
            <ul><li>First <strong>point</strong></li><li></li><li>Second</li></ul>
            <ol><li>One</li></ol>
            <pre>  var x = 1 &lt; 2;</pre>
            """);

        Assert.Equal(
            [BlockKind.Heading, BlockKind.Heading, BlockKind.Quote, BlockKind.List, BlockKind.List, BlockKind.Preformatted],
            body.Blocks.Select(b => b.Kind));
        Assert.Equal(
            [
                "<h2>Section one</h2>",
                "<h3>Detail</h3>",
                "<blockquote><p>We will build it.</p><p>Soon.</p></blockquote>",
                "<ul><li>First <strong>point</strong></li><li>Second</li></ul>",
                "<ol><li>One</li></ol>",
                "<pre>  var x = 1 &lt; 2;</pre>",
            ],
            Html(body));
        Assert.Equal(5, body.Blocks[2].WordCount);
    }

    [Fact]
    public void A_leading_heading_that_repeats_the_headline_is_dropped()
    {
        var body = Clean("<h1>Council Approves Bridge</h1><p>Body.</p><h2>Council approves bridge</h2>", title: "Council approves bridge");

        Assert.Equal(["<p>Body.</p>", "<h2>Council approves bridge</h2>"], Html(body));
    }

    [Fact]
    public void Figures_keep_their_caption_and_resolve_relative_urls()
    {
        var body = Clean("""
            <figure><img src="/img/bridge.jpg" alt="Alt text"><figcaption>The new <em>bridge</em>. Photo: Jo</figcaption></figure>
            <img src="https://cdn.example.com/plain.jpg" alt="Only alt">
            """);

        Assert.Equal([BlockKind.Figure, BlockKind.Figure], body.Blocks.Select(b => b.Kind));
        Assert.Equal(["a1-1", "a1-2"], body.Blocks.Select(b => b.ImageId));
        Assert.Equal("https://news.example.com/img/bridge.jpg", body.Images[0].SourceUrl);
        Assert.Equal("The new bridge. Photo: Jo", body.Images[0].Caption);
        Assert.Equal("Only alt", body.Images[1].Caption);
    }

    [Fact]
    public void Images_inside_paragraphs_are_lifted_out_after_the_paragraph()
    {
        var body = Clean("""<p>Before <img src="https://cdn.example.com/in.jpg"> after.</p><p>Next.</p>""");

        Assert.Equal([BlockKind.Paragraph, BlockKind.Figure, BlockKind.Paragraph], body.Blocks.Select(b => b.Kind));
        Assert.Equal("<p>Before after.</p>", body.Blocks[0].Html);
    }

    [Fact]
    public void Lazy_loaded_images_use_the_real_source()
    {
        var body = Clean("""
            <img src="data:image/gif;base64,R0lGOD" data-src="https://cdn.example.com/lazy.jpg">
            <img src="https://cdn.example.com/small.jpg" srcset="https://cdn.example.com/s.jpg 320w, https://cdn.example.com/l.jpg 1280w, https://cdn.example.com/m.jpg 640w">
            """);

        Assert.Equal(["https://cdn.example.com/lazy.jpg", "https://cdn.example.com/l.jpg"], body.Images.Select(i => i.SourceUrl));
    }

    [Fact]
    public void Svg_data_uri_and_repeated_images_are_skipped()
    {
        var body = Clean("""
            <img src="/logo.svg">
            <img src="data:image/png;base64,AAAA">
            <img src="https://cdn.example.com/a.jpg">
            <img src="https://cdn.example.com/a.jpg">
            """);

        Assert.Equal(["https://cdn.example.com/a.jpg"], body.Images.Select(i => i.SourceUrl));
        Assert.Single(body.Blocks);
    }

    [Fact]
    public void Blank_input_gives_an_empty_body()
    {
        Assert.Empty(Clean("   ").Blocks);
        Assert.Empty(ArticleHtmlCleaner.Clean(null, Base, "a1").Blocks);
    }
}
