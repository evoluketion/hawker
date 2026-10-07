using Hawker.Core.Data;
using Hawker.Core.Domain;
using Hawker.Core.Selection;
using Hawker.Web.Editions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Hawker.Tests.Editions;

public sealed class EditionSchedulerTests : IDisposable
{
    private static readonly DateTimeOffset Monday6am = new(2026, 10, 5, 6, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Factory _factory;
    private readonly FakeTimeProvider _time = new(Monday6am);
    private readonly EditionRunQueue _queue = new();
    private readonly EditionScheduler _scheduler;

    public EditionSchedulerTests()
    {
        _connection.Open();
        _factory = new Factory(new DbContextOptionsBuilder<HawkerDbContext>().UseSqlite(_connection).Options);
        using (var db = _factory.CreateDbContext())
        {
            db.Database.Migrate();
        }

        _scheduler = new EditionScheduler(_factory, _queue, new SeenArticleStore(_factory, _time), _time, new StartedLifetime(), NullLogger<EditionScheduler>.Instance);
    }

    public void Dispose() => _connection.Dispose();

    private int AddEdition(bool enabled = true, params DateTimeOffset[] previousScheduledRuns)
    {
        using var db = _factory.CreateDbContext();
        var edition = new Edition
        {
            Name = "Morning",
            TimeZone = "UTC",
            Enabled = enabled,
            Schedules = [new EditionSchedule { Kind = ScheduleKind.Daily, Times = [new TimeOnly(6, 0)] }],
            Runs = previousScheduledRuns.Select(t => new EditionRun { StartedAt = t, Trigger = RunTrigger.Schedule, Status = RunStatus.Succeeded }).ToList(),
        };
        db.Editions.Add(edition);
        db.SaveChanges();
        return edition.Id;
    }

    private async Task<List<RunRequest>> DrainQueueAsync()
    {
        var requests = new List<RunRequest>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        try
        {
            await foreach (var request in _queue.ReadAllAsync(cts.Token))
            {
                requests.Add(request);
            }
        }
        catch (OperationCanceledException)
        {
        }

        return requests;
    }

    [Fact]
    public async Task A_due_edition_is_queued_on_the_regular_check()
    {
        var id = AddEdition();

        await _scheduler.TickAsync(Monday6am.AddSeconds(10), Monday6am.AddSeconds(-20), CancellationToken.None);

        Assert.Equal([new RunRequest(id, RunTrigger.Schedule, Monday6am)], await DrainQueueAsync());
    }

    [Fact]
    public async Task Startup_catches_up_a_missed_run_using_the_last_run_from_the_database()
    {
        var id = AddEdition(enabled: true, Monday6am.AddDays(-2), Monday6am.AddDays(-1));

        await _scheduler.TickAsync(Monday6am.AddMinutes(30), lastCheck: null, CancellationToken.None);

        Assert.Equal([new RunRequest(id, RunTrigger.MissedOnStartup, Monday6am)], await DrainQueueAsync());
    }

    [Fact]
    public async Task Startup_does_not_repeat_todays_run()
    {
        AddEdition(enabled: true, Monday6am.AddSeconds(2));

        await _scheduler.TickAsync(Monday6am.AddMinutes(30), lastCheck: null, CancellationToken.None);

        Assert.Empty(await DrainQueueAsync());
    }

    [Fact]
    public async Task Disabled_editions_are_never_queued()
    {
        AddEdition(enabled: false);

        await _scheduler.TickAsync(Monday6am.AddSeconds(10), Monday6am.AddSeconds(-20), CancellationToken.None);

        Assert.Empty(await DrainQueueAsync());
    }

    [Fact]
    public async Task The_catch_up_window_follows_the_grace_setting()
    {
        AddEdition(enabled: true, Monday6am.AddDays(-1));
        using (var db = _factory.CreateDbContext())
        {
            db.AppSettings.Single().MissedRunGraceMinutes = 15;
            db.SaveChanges();
        }

        var plan = await _scheduler.TickAsync(Monday6am.AddMinutes(30), lastCheck: null, CancellationToken.None);

        Assert.Empty(plan.Due);
        Assert.Empty(await DrainQueueAsync());
    }

    [Fact]
    public async Task Nothing_is_queued_if_the_app_never_finishes_starting()
    {
        AddEdition(enabled: true, Monday6am.AddDays(-1)); // a run would be due for catch-up
        var neverStarted = new NotStartedLifetime();
        var scheduler = new EditionScheduler(_factory, _queue, new SeenArticleStore(_factory, _time), _time, neverStarted, NullLogger<EditionScheduler>.Instance);
        _time.Advance(TimeSpan.FromMinutes(30));

        using var stop = new CancellationTokenSource();
        await scheduler.StartAsync(stop.Token);
        await Task.Delay(200);
        await stop.CancelAsync();
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Empty(await DrainQueueAsync());
    }

    private sealed class NotStartedLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    private sealed class StartedLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => new(canceled: true);
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    private sealed class Factory(DbContextOptions<HawkerDbContext> options) : IDbContextFactory<HawkerDbContext>
    {
        public HawkerDbContext CreateDbContext() => new(options);
    }
}
