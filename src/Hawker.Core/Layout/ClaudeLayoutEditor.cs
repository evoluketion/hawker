using Anthropic;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Logging;

namespace Hawker.Core.Layout;

/// <summary>Asks Claude to arrange the edition. Any failure surfaces as an exception so the caller can fall back.</summary>
public sealed class ClaudeLayoutEditor(AnthropicClient client, string model, ILogger<ClaudeLayoutEditor> logger) : ILayoutEditor
{
    public string Name => $"Claude ({model})";

    public async Task<LayoutPlan> PlanAsync(LayoutRequest request, CancellationToken cancellationToken = default)
    {
        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = 16000,
            // A declined request is retried server-side on Anthropic's recommended fallback model.
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
            System = ClaudeLayoutPrompt.SystemPrompt,
            OutputConfig = new BetaOutputConfig
            {
                Effort = Effort.Medium,
                Format = new BetaJsonOutputFormat { Schema = ClaudeLayoutPrompt.OutputSchema(request) },
            },
            Messages = [new() { Role = Role.User, Content = ClaudeLayoutPrompt.BuildUserMessage(request) }],
        };

        var response = await client.Beta.Messages.Create(parameters, cancellationToken);

        if (response.StopReason == "refusal")
        {
            throw new InvalidOperationException($"Claude declined to lay out the edition ({response.StopDetails?.Category}).");
        }

        if (response.StopReason == "max_tokens")
        {
            throw new InvalidOperationException("Claude's layout was cut off before it finished.");
        }

        var json = string.Concat(response.Content.Select(b => b.TryPickText(out var text) ? text.Text : ""));
        logger.LogInformation("Claude layout used {Input} input and {Output} output tokens", response.Usage.InputTokens, response.Usage.OutputTokens);
        return ClaudeLayoutPrompt.ParsePlan(json);
    }
}
