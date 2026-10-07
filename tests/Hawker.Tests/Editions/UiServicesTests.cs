using Hawker.Core.Data;
using Hawker.Core.Domain;
using Hawker.Core.Feeds;
using Hawker.Core.Scheduling;
using Hawker.Web.Editions;
using Hawker.Web.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Hawker.Tests.Editions;

public sealed class UiServicesTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Factory _factory;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero)); // a Monday
    private readonly EditionStore _editions;
    private readonly FeedStore _feeds;
    private readonly GeneralSettingsService _general;

    public UiServicesTests()
    {
        _connection.Open();
        _factory = new Factory(new DbContextOptionsBuilder<HawkerDbContext>().UseSqlite(_connection).Options);
        using (var db = _factory.CreateDbContext())
        {
            db.Database.Migrate();
        }

        _editions = new EditionStore(_factory, _time);
        _feeds = new FeedStore(_factory, new FeedFetcher(new HttpClient(), NullLogger<FeedFetcher>.Instance));
        _general = new GeneralSettingsService(_factory);
    }

    public void Dispose() => _connection.Dispose();

    private async Task<int> AddFeedAsync(string name, string url)
    {
        var result = await _feeds.SaveAsync(new Feed { Name = name, Url = url });
        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        return result.Id;
    }

    private static EditionForm ValidForm(params int[] feedIds) => new()
    {
        Name = "  The Morning Hawker ",
        FeedIds = feedIds.ToHashSet(),
        TimeZone = "UTC",
        Schedules = [new ScheduleForm { Kind = ScheduleKind.Weekly, Times = [new TimeOnly(7, 0)], Weekdays = [DayOfWeek.Saturday, DayOfWeek.Sunday] }],
    };

    [Fact]
    public void Edition_form_reports_every_problem_in_plain_words()
    {
        var form = new EditionForm
        {
            Name = " ",
            ArticleCount = 0,
            LookbackHours = 10_000,
            Copies = 50,
            TimeZone = "Not/AZone",
            Schedules = [new ScheduleForm { Kind = ScheduleKind.Weekly, Weekdays = [] }, new ScheduleForm { Kind = ScheduleKind.Cron, CronExpression = "nope" }],
        };

        var errors = form.Validate();

        Assert.Equal(8, errors.Count);
        Assert.Contains("Choose at least one feed.", errors);
        Assert.Contains("Schedule 1: Choose at least one day of the week.", errors);
        Assert.Contains(errors, e => e.StartsWith("Schedule 2:"));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(10, 7)]
    [InlineData(15, 10)]
    public void Page_estimate_follows_one_and_a_half_stories_a_page(int articles, int pages)
    {
        Assert.Equal(pages, new EditionForm { ArticleCount = articles }.EstimatedPages);
    }

    [Fact]
    public async Task Editions_save_load_and_list_with_their_next_run()
    {
        var bbc = await AddFeedAsync("BBC News", "https://feeds.bbci.co.uk/news/rss.xml");
        var hn = await AddFeedAsync("Hacker News", "https://hnrss.org/frontpage");

        var saved = await _editions.SaveAsync(ValidForm(bbc, hn));
        Assert.True(saved.Succeeded);

        var loaded = await _editions.LoadAsync(saved.Id);
        Assert.NotNull(loaded);
        Assert.Equal("The Morning Hawker", loaded.Name);
        Assert.Equal([bbc, hn], loaded.FeedIds.Order());
        var schedule = Assert.Single(loaded.Schedules);
        Assert.Equal(ScheduleKind.Weekly, schedule.Kind);
        Assert.Equal([DayOfWeek.Sunday, DayOfWeek.Saturday], schedule.Weekdays.Order());

        var summary = Assert.Single(await _editions.ListAsync());
        Assert.Equal(["Weekends at 07:00"], summary.Schedules);
        Assert.Equal(new DateTimeOffset(2026, 10, 10, 7, 0, 0, TimeSpan.Zero), summary.NextRun);
        Assert.Null(summary.LastRun);
    }

    [Fact]
    public async Task Editing_replaces_schedules_and_feeds()
    {
        var bbc = await AddFeedAsync("BBC News", "https://feeds.bbci.co.uk/news/rss.xml");
        var hn = await AddFeedAsync("Hacker News", "https://hnrss.org/frontpage");
        var id = (await _editions.SaveAsync(ValidForm(bbc, hn))).Id;

        var form = (await _editions.LoadAsync(id))!;
        form.FeedIds = [hn];
        form.Schedules = [new ScheduleForm { Kind = ScheduleKind.MultiDaily, Times = [new TimeOnly(6, 0), new TimeOnly(18, 0)] }];
        Assert.True((await _editions.SaveAsync(form)).Succeeded);

        var reloaded = (await _editions.LoadAsync(id))!;
        Assert.Equal([hn], reloaded.FeedIds);
        Assert.Equal([new TimeOnly(6, 0), new TimeOnly(18, 0)], Assert.Single(reloaded.Schedules).Times);
        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, db.EditionSchedules.Count());
    }

    [Fact]
    public async Task Listing_shows_each_editions_latest_run()
    {
        var feed = await AddFeedAsync("BBC News", "https://feeds.bbci.co.uk/news/rss.xml");
        var first = (await _editions.SaveAsync(ValidForm(feed))).Id;
        var form = ValidForm(feed);
        form.Name = "Evening";
        var second = (await _editions.SaveAsync(form)).Id;
        await using (var db = _factory.CreateDbContext())
        {
            db.EditionRuns.AddRange(
                new EditionRun { EditionId = first, StartedAt = _time.GetUtcNow(), Status = RunStatus.Failed },
                new EditionRun { EditionId = first, StartedAt = _time.GetUtcNow().AddHours(1), Status = RunStatus.Succeeded },
                new EditionRun { EditionId = second, StartedAt = _time.GetUtcNow(), Status = RunStatus.PrintFailed });
            db.SaveChanges();
        }

        var list = await _editions.ListAsync();

        Assert.Equal(RunStatus.Succeeded, list.Single(e => e.Id == first).LastRun!.Status);
        Assert.Equal(RunStatus.PrintFailed, list.Single(e => e.Id == second).LastRun!.Status);
    }

    [Fact]
    public async Task Invalid_editions_are_not_saved()
    {
        var result = await _editions.SaveAsync(new EditionForm { Name = "No feeds" });

        Assert.False(result.Succeeded);
        Assert.Empty(await _editions.ListAsync());
    }

    [Fact]
    public async Task The_same_feed_cannot_be_added_twice()
    {
        await AddFeedAsync("BBC News", "https://feeds.bbci.co.uk/news/rss.xml");

        var duplicate = await _feeds.SaveAsync(new Feed { Name = "BBC again", Url = "http://feeds.bbci.co.uk/news/rss.xml/" });

        Assert.Equal(["This feed is already added as \"BBC News\"."], duplicate.Errors);
    }

    [Fact]
    public async Task A_feed_can_be_saved_again_without_tripping_the_duplicate_check()
    {
        var id = await AddFeedAsync("BBC News", "https://feeds.bbci.co.uk/news/rss.xml");
        var feed = (await _feeds.LoadAsync(id))!;
        feed.MinScore = 10;

        Assert.True((await _feeds.SaveAsync(feed)).Succeeded);
    }

    [Theory]
    [InlineData("", "https://x.example.com/rss", "Give the feed a name")]
    [InlineData("X", "not a url", "full web address")]
    [InlineData("X", "ftp://x.example.com/rss", "full web address")]
    public void Feed_validation_explains_the_problem(string name, string url, string expected)
    {
        Assert.Contains(FeedStore.Validate(new Feed { Name = name, Url = url }), e => e.Contains(expected));
    }

    [Fact]
    public void Custom_score_pattern_must_be_a_valid_regex_with_a_score_group()
    {
        Assert.Contains(FeedStore.Validate(new Feed { Name = "X", Url = "https://x.example.com", ScoreSource = ScoreSource.Regex, ScoreRegex = "(\\d+) votes" }),
            e => e.Contains("named group called 'score'"));
        Assert.Contains(FeedStore.Validate(new Feed { Name = "X", Url = "https://x.example.com", ScoreSource = ScoreSource.Regex, ScoreRegex = "(?<score>" }),
            e => e.StartsWith("The score pattern is not valid"));
        Assert.Empty(FeedStore.Validate(new Feed { Name = "X", Url = "https://x.example.com", ScoreSource = ScoreSource.Regex, ScoreRegex = "(?<score>\\d+) votes" }));
    }

    [Fact]
    public async Task Print_retries_default_to_zero_and_need_confirmation_to_raise()
    {
        Assert.Equal(0, (await _general.GetAsync()).PrintRetryCount);

        var refused = await _general.SaveAsync(new GeneralSettings(2, 60, 120), retriesConfirmed: false);
        Assert.Contains("Raising print retries needs confirmation.", refused);
        Assert.Equal(0, (await _general.GetAsync()).PrintRetryCount);

        Assert.Empty(await _general.SaveAsync(new GeneralSettings(2, 60, 120), retriesConfirmed: true));
        Assert.Equal(2, (await _general.GetAsync()).PrintRetryCount);

        // Lowering, or changing other settings, needs no confirmation.
        Assert.Empty(await _general.SaveAsync(new GeneralSettings(1, 30, 60), retriesConfirmed: false));
        Assert.Equal(new GeneralSettings(1, 30, 60), await _general.GetAsync());
    }

    [Fact]
    public async Task General_settings_are_range_checked()
    {
        var errors = await _general.SaveAsync(new GeneralSettings(9, 5, -1), retriesConfirmed: true);

        Assert.Equal(3, errors.Count);
        Assert.Equal(GeneralSettingsService.Defaults, await _general.GetAsync());
    }

    private sealed class Factory(DbContextOptions<HawkerDbContext> options) : IDbContextFactory<HawkerDbContext>
    {
        public HawkerDbContext CreateDbContext() => new(options);
    }
}
