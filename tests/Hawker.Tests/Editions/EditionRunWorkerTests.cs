using Hawker.Core.Domain;
using Hawker.Web.Editions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hawker.Tests.Editions;

public sealed class EditionRunWorkerTests
{
    [Fact]
    public async Task The_running_flag_is_set_during_a_run_and_cleared_when_it_throws()
    {
        var queue = new EditionRunQueue();
        bool? runningDuringRun = null;
        var services = new ServiceCollection()
            .AddTransient<EditionRunService>(_ =>
            {
                runningDuringRun = queue.IsRunning;
                throw new InvalidOperationException("Broken run");
            })
            .BuildServiceProvider();
        var worker = new EditionRunWorker(queue, services.GetRequiredService<IServiceScopeFactory>(), NullLogger<EditionRunWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        queue.Enqueue(new RunRequest(1, RunTrigger.Manual));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((runningDuringRun is null || queue.IsRunning) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        await worker.StopAsync(CancellationToken.None);

        Assert.True(runningDuringRun);
        Assert.False(queue.IsRunning);
    }
}
