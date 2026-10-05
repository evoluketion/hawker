using Hawker.Core.Data;
using Hawker.Core.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Hawker.Tests;

public sealed class HawkerDbContextTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public HawkerDbContextTests() => _connection.Open();

    public void Dispose() => _connection.Dispose();

    private HawkerDbContext CreateContext()
    {
        var db = new HawkerDbContext(new DbContextOptionsBuilder<HawkerDbContext>().UseSqlite(_connection).Options);
        db.Database.Migrate();
        return db;
    }

    [Fact]
    public void Migration_seeds_single_settings_row()
    {
        using var db = CreateContext();

        var settings = Assert.Single(db.AppSettings);
        Assert.Equal("claude-sonnet-5-5", settings.ClaudeModel);
    }

    [Fact]
    public void Edition_with_feeds_schedules_and_runs_round_trips()
    {
        var started = new DateTimeOffset(2026, 10, 5, 6, 0, 0, TimeSpan.FromHours(1));
        using (var db = CreateContext())
        {
            var feed = new Feed { Name = "Hacker News", Url = "https://hnrss.org/frontpage", MinScore = 100 };
            db.Editions.Add(new Edition
            {
                Name = "The Morning Hawker",
                Feeds = [feed],
                SortMode = SortMode.MostPopular,
                LookbackHours = 48,
                Schedules =
                [
                    new EditionSchedule
                    {
                        Kind = ScheduleKind.Weekly,
                        Times = [new TimeOnly(8, 0)],
                        Weekdays = [DayOfWeek.Saturday, DayOfWeek.Sunday],
                        CronExpression = "0 8 * * 6,0",
                    },
                ],
                Runs = [new EditionRun { StartedAt = started, ArticleUrls = ["https://example.com/a"] }],
            });
            db.SaveChanges();
        }

        using (var db = CreateContext())
        {
            var edition = db.Editions.Include(e => e.Feeds).Include(e => e.Schedules).Include(e => e.Runs).Single();

            Assert.Equal(SortMode.MostPopular, edition.SortMode);
            Assert.Equal(100, Assert.Single(edition.Feeds).MinScore);
            var schedule = Assert.Single(edition.Schedules);
            Assert.Equal([DayOfWeek.Saturday, DayOfWeek.Sunday], schedule.Weekdays);
            Assert.Equal([new TimeOnly(8, 0)], schedule.Times);
            var run = Assert.Single(edition.Runs);
            Assert.Equal(started, run.StartedAt);
            Assert.Equal(["https://example.com/a"], run.ArticleUrls);
        }
    }

    [Fact]
    public void Runs_can_be_filtered_and_ordered_by_start_time()
    {
        var baseline = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        using var db = CreateContext();
        db.Editions.Add(new Edition
        {
            Name = "Daily",
            Runs = Enumerable.Range(0, 5).Select(i => new EditionRun { StartedAt = baseline.AddDays(i) }).ToList(),
        });
        db.SaveChanges();

        var recent = db.EditionRuns
            .Where(r => r.StartedAt >= baseline.AddDays(2))
            .OrderByDescending(r => r.StartedAt)
            .Select(r => r.StartedAt)
            .ToList();

        Assert.Equal([baseline.AddDays(4), baseline.AddDays(3), baseline.AddDays(2)], recent);
    }
}
