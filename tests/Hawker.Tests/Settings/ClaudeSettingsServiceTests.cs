using Hawker.Core.Data;
using Hawker.Web.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hawker.Tests.Settings;

public sealed class ClaudeSettingsServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Factory _factory;
    private readonly ClaudeSettingsService _service;

    public ClaudeSettingsServiceTests()
    {
        _connection.Open();
        _factory = new Factory(new DbContextOptionsBuilder<HawkerDbContext>().UseSqlite(_connection).Options);
        using (var db = _factory.CreateDbContext())
        {
            db.Database.Migrate();
        }

        _service = new ClaudeSettingsService(_factory, new EphemeralDataProtectionProvider(), NullLogger<ClaudeSettingsService>.Instance);
    }

    public void Dispose() => _connection.Dispose();

    // These tests assume ANTHROPIC_API_KEY is not set for the test run, which would take precedence.
    private static bool EnvironmentKeySet => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ClaudeSettingsService.ApiKeyEnvironmentVariable));

    [Fact]
    public async Task Stored_key_is_encrypted_at_rest_and_round_trips()
    {
        if (EnvironmentKeySet)
        {
            return;
        }

        await _service.SaveApiKeyAsync("  sk-ant-test-123  ");

        using (var db = _factory.CreateDbContext())
        {
            var stored = db.AppSettings.Single().EncryptedApiKey;
            Assert.NotNull(stored);
            Assert.DoesNotContain("sk-ant-test-123", stored);
        }

        var settings = await _service.GetAsync();
        Assert.Equal("sk-ant-test-123", settings.ApiKey);
        Assert.Equal(ApiKeySource.Settings, settings.KeySource);
        Assert.True(settings.IsConfigured);
    }

    [Fact]
    public async Task Clearing_the_key_falls_back_to_the_standard_layout()
    {
        if (EnvironmentKeySet)
        {
            return;
        }

        await _service.SaveApiKeyAsync("sk-ant-test-123");
        await _service.SaveApiKeyAsync("");

        var settings = await _service.GetAsync();
        Assert.False(settings.IsConfigured);
        Assert.Equal(ApiKeySource.None, settings.KeySource);
    }

    [Fact]
    public async Task Model_defaults_to_opus_and_can_be_changed()
    {
        Assert.Equal("claude-opus-5-5", (await _service.GetAsync()).Model);

        await _service.SaveModelAsync("claude-sonnet-5-5");
        Assert.Equal("claude-sonnet-5-5", (await _service.GetAsync()).Model);

        await _service.SaveModelAsync(" ");
        Assert.Equal("claude-opus-5-5", (await _service.GetAsync()).Model);
    }

    private sealed class Factory(DbContextOptions<HawkerDbContext> options) : IDbContextFactory<HawkerDbContext>
    {
        public HawkerDbContext CreateDbContext() => new(options);
    }
}
