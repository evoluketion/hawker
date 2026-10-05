using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Hawker.Core.Articles;
using Hawker.Core.Layout;
using Microsoft.Extensions.Logging.Abstractions;
using static Hawker.Tests.Layout.TestArticles;

namespace Hawker.Tests.Layout;

public class ClaudeLayoutEditorTests
{
    private static readonly IReadOnlyList<Article> Articles =
    [
        Make("a1", "BBC News", title: "Storm batters coast"),
        Make("a2", "Hacker News", score: 412, image: false),
        Make("a3", "BBC News", paragraphs: [30]),
    ];

    private static readonly LayoutRequest Request = new("The Morning Hawker", Articles);

    private const string ValidPlanJson = """
        {"lead":{"id":"a1","show_image":true,"keep_blocks":0},
         "sections":[{"name":"Technology","stories":[{"id":"a2","size":"minor","show_image":false,"keep_blocks":1}]},
                     {"name":"UK","stories":[{"id":"a3","size":"brief","show_image":false,"keep_blocks":1}]}]}
        """;

    private static (ClaudeLayoutEditor Editor, StubHandler Http) CreateEditor(string stopReason = "end_turn", string text = ValidPlanJson, HttpStatusCode status = HttpStatusCode.OK)
    {
        var http = new StubHandler(status, MessageJson(stopReason, text));
        var client = new AnthropicClient { ApiKey = "test-key", HttpClient = new HttpClient(http), MaxRetries = 0 };
        return (new ClaudeLayoutEditor(client, "claude-opus-5-5", NullLogger<ClaudeLayoutEditor>.Instance), http);
    }

    private static string MessageJson(string stopReason, string text) => JsonSerializer.Serialize(new
    {
        id = "msg_test",
        type = "message",
        role = "assistant",
        model = "claude-opus-5-5",
        content = new[] { new { type = "text", text } },
        stop_reason = stopReason,
        stop_sequence = (string?)null,
        stop_details = stopReason == "refusal" ? new { type = "refusal", category = "cyber", explanation = "declined" } : null,
        usage = new { input_tokens = 1200, output_tokens = 150 },
    });

    [Fact]
    public async Task Sends_a_structured_output_request_with_default_fallbacks()
    {
        var (editor, http) = CreateEditor();

        await editor.PlanAsync(Request);

        var body = JsonNode.Parse(http.LastBody!)!;
        Assert.Equal("claude-opus-5-5", (string?)body["model"]);
        Assert.Equal("default", (string?)body["fallbacks"]);
        Assert.Equal("medium", (string?)body["output_config"]!["effort"]);
        Assert.Equal("json_schema", (string?)body["output_config"]!["format"]!["type"]);
        Assert.Contains("server-side-fallback-2026-07-01", http.LastBetaHeader);
        Assert.Contains("layout editor", (string?)body["system"]);
        Assert.Equal("test-key", http.LastApiKey);
    }

    [Fact]
    public async Task Turns_the_response_into_a_plan()
    {
        var (editor, _) = CreateEditor();

        var plan = await editor.PlanAsync(Request);

        Assert.Equal(new LayoutItem("a1", StorySize.Lead, true, null), plan.Lead);
        Assert.Equal(["Technology", "UK"], plan.Sections.Select(s => s.Name));
        Assert.Equal(new LayoutItem("a2", StorySize.Minor, false, 1), plan.Sections[0].Items[0]);
        Assert.Equal(StorySize.Brief, plan.Sections[1].Items[0].Size);
        Assert.Empty(LayoutPlanValidator.Validate(plan, Articles).Corrections);
    }

    [Fact]
    public async Task A_refusal_is_an_error_so_the_standard_layout_takes_over()
    {
        var (editor, _) = CreateEditor(stopReason: "refusal", text: "");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => editor.PlanAsync(Request));
        Assert.Contains("declined", error.Message);
    }

    [Fact]
    public async Task A_truncated_response_is_an_error()
    {
        var (editor, _) = CreateEditor(stopReason: "max_tokens", text: "{\"lead\":");

        await Assert.ThrowsAsync<InvalidOperationException>(() => editor.PlanAsync(Request));
    }

    [Fact]
    public async Task Api_errors_propagate()
    {
        var (editor, _) = CreateEditor(status: HttpStatusCode.Unauthorized);

        await Assert.ThrowsAnyAsync<Exception>(() => editor.PlanAsync(Request));
    }

    [Fact]
    public void User_message_carries_metadata_and_a_short_excerpt_but_not_whole_articles()
    {
        var longArticle = Make("a9", paragraphs: [200, 200]);

        var message = ClaudeLayoutPrompt.BuildUserMessage(new LayoutRequest("Paper", [longArticle, .. Articles]));

        Assert.Contains("\"id\":\"a9\"", message);
        Assert.Contains("\"block_words\":[200,200]", message);
        Assert.Contains("\"score\":412", message);
        Assert.Contains("a9 paragraph 1:", message);
        Assert.DoesNotContain("a9 paragraph 2:", message);
        Assert.True(message.Length < 4000);
    }

    [Fact]
    public void Schema_limits_ids_sizes_and_section_names()
    {
        var schema = JsonSerializer.SerializeToNode(ClaudeLayoutPrompt.OutputSchema(Request))!;
        var story = schema["properties"]!["sections"]!["items"]!["properties"]!["stories"]!["items"]!;

        Assert.Equal(["a1", "a2", "a3"], story["properties"]!["id"]!["enum"]!.AsArray().Select(n => (string?)n));
        Assert.Equal(["major", "minor", "brief"], story["properties"]!["size"]!["enum"]!.AsArray().Select(n => (string?)n));
        Assert.False((bool)story["additionalProperties"]!);

        var names = schema["properties"]!["sections"]!["items"]!["properties"]!["name"]!["enum"]!.AsArray().Select(n => (string?)n).ToList();
        Assert.Contains("Technology", names);
        Assert.Contains("BBC News", names);
        Assert.Contains("Hacker News", names);
        Assert.Equal(names.Count, names.Distinct().Count());

        var lead = schema["properties"]!["lead"]!;
        Assert.Null(lead["properties"]!["size"]);
    }

    [Fact]
    public void Malformed_json_is_rejected()
    {
        Assert.ThrowsAny<JsonException>(() => ClaudeLayoutPrompt.ParsePlan("not json"));
        Assert.Throws<FormatException>(() => ClaudeLayoutPrompt.ParsePlan("""{"sections":[]}"""));
    }

    private sealed class StubHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }
        public string LastBetaHeader { get; private set; } = "";
        public string? LastApiKey { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            LastBetaHeader = request.Headers.TryGetValues("anthropic-beta", out var betas) ? string.Join(",", betas) : "";
            LastApiKey = request.Headers.TryGetValues("x-api-key", out var keys) ? keys.First() : null;

            var body = status == HttpStatusCode.OK
                ? responseBody
                : """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""";
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json"), RequestMessage = request };
        }
    }
}
