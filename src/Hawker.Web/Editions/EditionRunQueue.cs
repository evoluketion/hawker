using System.Threading.Channels;
using Hawker.Core.Domain;

namespace Hawker.Web.Editions;

/// <param name="ScheduledFor">The schedule time, for scheduled and caught-up runs.</param>
public sealed record RunRequest(int EditionId, RunTrigger Trigger, DateTimeOffset? ScheduledFor = null);

/// <summary>Runs wait here and are built one at a time, so two editions never fight over the browser or the printer.</summary>
public sealed class EditionRunQueue
{
    private readonly Channel<RunRequest> _channel = Channel.CreateUnbounded<RunRequest>(new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(RunRequest request) => _channel.Writer.TryWrite(request);

    public IAsyncEnumerable<RunRequest> ReadAllAsync(CancellationToken cancellationToken) => _channel.Reader.ReadAllAsync(cancellationToken);
}
