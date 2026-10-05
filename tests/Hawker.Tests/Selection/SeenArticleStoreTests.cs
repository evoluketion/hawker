using Hawker.Core.Data;
using Hawker.Core.Domain;
using Hawker.Core.Selection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Hawker.Tests.Selection;

public sealed class SeenArticleStoreTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 5, 6, 0, 0, TimeSpan.Zero));
    private readonly SeenArticleStore _store;
    private readonly int _editionId;
    private readonly int _otherEditionId;

    public SeenArticleStoreTests()
    {
        _connection.Open();
        var factory = new TestDbContextFactory(new DbContextOptionsBuilder<HawkerDbContext>().UseSqlite(_connection).Options);
        using (var db = factory.CreateDbContext())
        {
            db.Database.Migrate();
            var edition = new Edition { Name = "Morning" };
            var other = new Edition { Name = "Evening" };
            db.Editions.AddRange(edition, other);
            db.SaveChanges();
            _editionId = edition.Id;
            _otherEditionId = other.Id;
        }

        _store = new SeenArticleStore(factory, _time);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Marked_articles_are_seen_only_for_that_edition()
    {
        await _store.MarkSeenAsync(_editionId, ["a", "b"]);

        Assert.Equal(["a"], await _store.GetSeenAsync(_editionId, ["a", "c"]));
        Assert.Empty(await _store.GetSeenAsync(_otherEditionId, ["a", "b"]));
    }

    [Fact]
    public async Task Marking_twice_does_not_fail()
    {
        await _store.MarkSeenAsync(_editionId, ["a"]);
        await _store.MarkSeenAsync(_editionId, ["a", "a", "b"]);

        Assert.Equal(2, (await _store.GetSeenAsync(_editionId, ["a", "b"])).Count);
    }

    [Fact]
    public async Task Prune_removes_only_old_records()
    {
        await _store.MarkSeenAsync(_editionId, ["old"]);
        _time.Advance(TimeSpan.FromDays(40));
        await _store.MarkSeenAsync(_editionId, ["new"]);

        var removed = await _store.PruneAsync(TimeSpan.FromDays(30));

        Assert.Equal(1, removed);
        Assert.Equal(["new"], await _store.GetSeenAsync(_editionId, ["old", "new"]));
    }

    private sealed class TestDbContextFactory(DbContextOptions<HawkerDbContext> options) : IDbContextFactory<HawkerDbContext>
    {
        public HawkerDbContext CreateDbContext() => new(options);
    }
}
