using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace Hawker.Web.Settings;

/// <summary>Checks that an API key and model work, with the smallest possible request.</summary>
public sealed class ClaudeConnectionTester(ILogger<ClaudeConnectionTester> logger)
{
    public async Task<(bool Ok, string Message)> TestAsync(string apiKey, string model, CancellationToken cancellationToken = default)
    {
        var client = new AnthropicClient { ApiKey = apiKey, Timeout = TimeSpan.FromSeconds(60), MaxRetries = 1 };
        try
        {
            var response = await client.Messages.Create(new MessageCreateParams
            {
                Model = model,
                MaxTokens = 64,
                OutputConfig = new OutputConfig { Effort = Effort.Low },
                Messages = [new() { Role = Role.User, Content = "Reply with the single word: ready" }],
            }, cancellationToken);
            return (true, $"Connected. {model} answered ({response.Usage.InputTokens + response.Usage.OutputTokens} tokens used).");
        }
        catch (AnthropicUnauthorizedException)
        {
            return (false, "The API key was rejected. Check it was copied in full.");
        }
        catch (AnthropicNotFoundException)
        {
            return (false, $"The model {model} is not available to this key.");
        }
        catch (AnthropicRateLimitException)
        {
            return (false, "The key works but is being rate limited right now. Try again in a minute.");
        }
        catch (AnthropicApiException ex)
        {
            logger.LogWarning(ex, "Claude connection test failed");
            return (false, $"Claude returned an error: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Could not reach the Claude API: {ex.Message}");
        }
    }
}
