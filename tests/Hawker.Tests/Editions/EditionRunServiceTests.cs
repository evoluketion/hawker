using Hawker.Core;
using Hawker.Core.Articles;
using Hawker.Core.Data;
using Hawker.Core.Domain;
using Hawker.Core.Feeds;
using Hawker.Core.Layout;
using Hawker.Core.Selection;
using Hawker.Web.Editions;
using Hawker.Web.Printing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using static Hawker.Tests.Layout.TestArticles;

namespace Hawker.Tests.Editions;

public sealed class EditionRunServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Factory _factory;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 5, 6, 0, 0, TimeSpan.Zero));
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "hawker-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeComposer _composer = new();
    private readonly FakePrinter _printer = new();
    private readonly SeenArticleStore _seen;
    private readonly int _editionId;

    public EditionRunServiceTests()
    {
        _connection.Open();
        _factory = new Factory(new DbContextOptionsBuilder<HawkerDbContext>().UseSqlite(_connection).Options);
        using (var db = _factory.CreateDbContext())
        {
            db.Database.Migrate();
            var edition = new Edition { Name = "The Morning Hawker", TimeZone = "UTC", PrinterName = "Office", Copies = 2 };
            db.Editions.Add(edition);
            db.SaveChanges();
            _editionId = edition.Id;
        }

        _seen = new SeenArticleStore(_factory, _time);
    }

    public void Dispose()
    {
        _connection.Dispose();
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    private EditionRunService Service() => new(
        _factory, _composer, _printer, _seen, new HawkerPaths(_dataDir), _time, new FakeLifetime(), new RunEvents(), NullLogger<EditionRunService>.Instance)
    {
        JobCheckDelay = TimeSpan.FromDays(1), // never fires during a test
    };

    private void Configure(Action<Edition>? edition = null, Action<AppSettings>? settings = null)
    {
        using var db = _factory.CreateDbContext();
        edition?.Invoke(db.Editions.Single());
        settings?.Invoke(db.AppSettings.Single());
        db.SaveChanges();
    }

    private Edition LoadEdition()
    {
        using var db = _factory.CreateDbContext();
        return db.Editions.Single();
    }

    private static Article Article(string id) => Make(id) with { UrlHash = UrlNormalizer.Hash($"https://news.example.com/{id}") };

    private async Task<IReadOnlySet<string>> SeenAsync() =>
        await _seen.GetSeenAsync(_editionId, [Article("a1").UrlHash, Article("a2").UrlHash]);

    [Fact]
    public async Task A_scheduled_run_archives_prints_and_marks_articles_printed()
    {
        var run = await Service().ExecuteAsync(new RunRequest(_editionId, RunTrigger.Schedule, _time.GetUtcNow()));

        Assert.NotNull(run);
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.True(run.Printed);
        Assert.Equal(1, run.IssueNumber);
        Assert.True(File.Exists(run.PdfPath));
        Assert.EndsWith(Path.Combine("the-morning-hawker-" + _editionId, "2026-10-05_0600.pdf"), run.PdfPath);
        Assert.Equal(new PrintOptions("Office", 2, true, PaperSize.A4), Assert.Single(_printer.Attempts));
        Assert.Equal(2, (await SeenAsync()).Count);
        Assert.Equal(1, LoadEdition().IssueCounter);
        Assert.Contains("Sent to Office as job Office-7, 2 copies, double-sided.", run.Log);
    }

    [Fact]
    public async Task A_failed_print_keeps_the_paper_and_leaves_its_articles_available()
    {
        _printer.Fail("lp: The printer or class does not exist.");

        var run = await Service().ExecuteAsync(new RunRequest(_editionId, RunTrigger.Schedule, _time.GetUtcNow()));

        Assert.Equal(RunStatus.PrintFailed, run!.Status);
        Assert.False(run.Printed);
        Assert.Contains("does not exist", run.Error);
        Assert.True(File.Exists(run.PdfPath));
        Assert.Empty(await SeenAsync());
        Assert.Equal(1, LoadEdition().IssueCounter); // the issue was produced, just not printed
    }

    [Fact]
    public async Task By_default_a_failed_print_is_attempted_exactly_once()
    {
        _printer.Fail("rejected");

        await Service().ExecuteAsync(new RunRequest(_editionId, RunTrigger.Manual));

        Assert.Single(_printer.Attempts);
    }

    [Fact]
    public async Task Configured_retries_repeat_a_rejected_print()
    {
        Configure(settings: s => { s.PrintRetryCount = 2; s.PrintRetryDelaySeconds = 0; });
        _printer.Fail("rejected", times: 2);

        var run = await Service().ExecuteAsync(new RunRequest(_editionId, RunTrigger.Manual));

        Assert.Equal(3, _printer.Attempts.Count);
        Assert.Equal(RunStatus.Succeeded, run!.Status);
        Assert.Contains("Print attempt 1 of 3 failed: rejected", run.Log);
    }

    [Fact]
    public async Task A_print_that_may_have_gone_through_is_never_retried()
    {
        Configure(settings: s => { s.PrintRetryCount = 5; s.PrintRetryDelaySeconds = 0; });
        _printer.Fail(PrintException.TimedOutMessage, mayHavePrinted: true, times: 10);

        var run = await Service().ExecuteAsync(new RunRequest(_editionId, RunTrigger.Manual));

        Assert.Single(_printer.Attempts);
        Assert.Equal(RunStatus.PrintFailed, run!.Status);
        Assert.Contains("Not retrying, because the job may already have printed.", run.Log);
    }

    [Fact]
    public async Task With_auto_print_off_the_paper_is_archived_and_counts_as_used()
    {
        Configure(edition: e => e.AutoPrint = false);

        var run = await Service().ExecuteAsync(new RunRequest(_editionId, RunTrigger.Schedule, _time.GetUtcNow()));

        Assert.Equal(RunStatus.Succeeded, run!.Status);
        Assert.Empty(_printer.Attempts);
        Assert.Equal(2, (await SeenAsync()).Count);
    }

    [Fact]
    public async Task A_preview_neither_prints_nor_uses_up_articles_or_issue_numbers()
    {
        var run = await Service().ExecuteAsync(new RunRequest(_editionId, RunTrigger.Preview));

        Assert.Equal(RunStatus.Succeeded, run!.Status);
        Assert.Empty(_printer.Attempts);
        Assert.Empty(await SeenAsync());
        Assert.Equal(0, LoadEdition().IssueCounter);
        Assert.EndsWith("-preview.pdf", run.PdfPath);
    }

    [Fact]
    public async Task No_articles_fails_the_run_without_printing()
    {
        _composer.Empty = true;

        var run = await Service().ExecuteAsync(new RunRequest(_editionId, RunTrigger.Schedule, _time.GetUtcNow()));

        Assert.Equal(RunStatus.Failed, run!.Status);
        Assert.Equal("No articles were available for this edition.", run.Error);
        Assert.Empty(_printer.Attempts);
        Assert.Equal(0, LoadEdition().IssueCounter);
    }

    [Fact]
    public async Task A_disabled_edition_does_not_run_on_schedule_but_can_run_by_hand()
    {
        Configure(edition: e => e.Enabled = false);

        Assert.Null(await Service().ExecuteAsync(new RunRequest(_editionId, RunTrigger.Schedule, _time.GetUtcNow())));
        Assert.NotNull(await Service().ExecuteAsync(new RunRequest(_editionId, RunTrigger.Manual)));
    }

    [Fact]
    public async Task Reprint_after_a_failure_prints_once_and_marks_articles_printed()
    {
        _printer.Fail("offline");
        var failed = await Service().ExecuteAsync(new RunRequest(_editionId, RunTrigger.Manual));
        _printer.Attempts.Clear();

        var reprinted = await Service().ReprintAsync(failed!.Id);

        Assert.Single(_printer.Attempts);
        Assert.Equal(RunStatus.Succeeded, reprinted.Status);
        Assert.True(reprinted.Printed);
        Assert.Null(reprinted.Error);
        Assert.Contains("Reprint requested", reprinted.Log);
        Assert.Equal(2, (await SeenAsync()).Count);
    }

    [Fact]
    public async Task Reprint_ignores_the_retry_setting()
    {
        Configure(settings: s => { s.PrintRetryCount = 3; s.PrintRetryDelaySeconds = 0; });
        var run = await Service().ExecuteAsync(new RunRequest(_editionId, RunTrigger.Manual));
        _printer.Attempts.Clear();
        _printer.Fail("offline", times: 10);

        var reprinted = await Service().ReprintAsync(run!.Id);

        Assert.Single(_printer.Attempts);
        Assert.Equal("offline", reprinted.Error);
    }

    [Fact]
    public async Task Archive_names_never_overwrite_an_earlier_paper()
    {
        var first = await Service().ExecuteAsync(new RunRequest(_editionId, RunTrigger.Manual));
        var second = await Service().ExecuteAsync(new RunRequest(_editionId, RunTrigger.Manual));

        Assert.NotEqual(first!.PdfPath, second!.PdfPath);
        Assert.EndsWith("2026-10-05_0600-2.pdf", second.PdfPath);
        Assert.Equal(2, second.IssueNumber);
    }

    [Theory]
    [InlineData("The Morning Hawker", "the-morning-hawker")]
    [InlineData("  Café  Gazette! ", "cafe-gazette")]
    [InlineData("???", "edition")]
    public void Edition_names_become_safe_folder_names(string name, string expected)
    {
        Assert.Equal(expected, EditionRunService.Slug(name));
    }

    private sealed class FakeComposer : IEditionComposer
    {
        public bool Empty { get; set; }

        public Task<ComposedEdition> ComposeAsync(Edition edition, int issueNumber, CancellationToken cancellationToken = default)
        {
            if (Empty)
            {
                throw new NoArticlesException("No articles were available for this edition.", ["Selected 0 article(s)."]);
            }

            Article[] articles = [Article("a1"), Article("a2")];
            var plan = new LayoutPlan(new LayoutItem("a1", StorySize.Lead, true, null), []);
            return Task.FromResult(new ComposedEdition("%PDF-1.7 fake"u8.ToArray(), "<html></html>", articles, plan, ["Laid out by standard layout."]));
        }
    }

    private sealed class FakePrinter : IPrintService
    {
        private readonly Queue<PrintException> _failures = new();

        public List<PrintOptions> Attempts { get; } = [];

        public void Fail(string message, bool mayHavePrinted = false, int times = 1)
        {
            for (var i = 0; i < times; i++)
            {
                _failures.Enqueue(new PrintException(message, mayHavePrinted: mayHavePrinted));
            }
        }

        public Task<IReadOnlyList<PrinterInfo>> ListPrintersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PrinterInfo>>([new PrinterInfo("Office", true)]);

        public Task<IReadOnlyList<PaperSourceInfo>> ListPaperSourcesAsync(string? printerName, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PaperSourceInfo>>([new PaperSourceInfo("Main", false)]);

        public Task<PrintResult> PrintAsync(string pdfPath, PrintOptions options, CancellationToken cancellationToken = default)
        {
            Attempts.Add(options);
            return _failures.TryDequeue(out var failure)
                ? Task.FromException<PrintResult>(failure)
                : Task.FromResult(new PrintResult(options.PrinterName ?? "default", "Office-7"));
        }

        public Task<string?> CheckJobAsync(PrintResult job, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
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
