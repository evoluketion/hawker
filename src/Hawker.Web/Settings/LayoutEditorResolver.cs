using System.Collections.Concurrent;
using Anthropic;
using Hawker.Core.Layout;

namespace Hawker.Web.Settings;

/// <summary>Uses Claude as the layout editor when an API key is configured, and the standard layout otherwise.</summary>
public sealed class LayoutEditorResolver(ClaudeSettingsService settings, ILoggerFactory loggerFactory)
{
    // One client per key, so HTTP connections are reused between editions.
    private readonly ConcurrentDictionary<string, AnthropicClient> _clients = new();
    private readonly DeterministicLayoutEditor _standard = new();

    public async Task<ILayoutEditor> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var claude = await settings.GetAsync(cancellationToken);
        if (claude.ApiKey is not { } apiKey)
        {
            return _standard;
        }

        var client = _clients.GetOrAdd(apiKey, key => new AnthropicClient { ApiKey = key, Timeout = TimeSpan.FromMinutes(3) });
        return new ClaudeLayoutEditor(client, claude.Model, loggerFactory.CreateLogger<ClaudeLayoutEditor>());
    }
}
