namespace Hawker.Web.Updates;

/// <summary>Reports a finished update once Hawker is up, then checks for a newer release now and every few hours.</summary>
public sealed class UpdateCheckWorker(UpdateService updates, IHostApplicationLifetime lifetime) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (lifetime.ApplicationStarted.Register(started.SetResult))
        await using (stoppingToken.Register(started.SetCanceled))
        {
            await started.Task;
        }

        updates.ReportStarted();

        // By now any applier that started this version has finished with its files.
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        updates.RemoveLeftovers();
        if (!updates.CheckAutomatically)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            await updates.CheckAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
