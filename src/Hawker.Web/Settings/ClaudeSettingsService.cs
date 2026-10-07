using System.Security.Cryptography;
using Hawker.Core.Data;
using Hawker.Core.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Hawker.Web.Settings;

public enum ApiKeySource
{
    None,
    Environment,
    Settings,
}

public sealed record ClaudeSettings(string? ApiKey, ApiKeySource KeySource, string Model)
{
    public bool IsConfigured => ApiKey is not null;
}

/// <summary>Reads and stores the Claude API key (encrypted at rest) and model choice.</summary>
public sealed class ClaudeSettingsService(
    IDbContextFactory<HawkerDbContext> dbFactory,
    IDataProtectionProvider dataProtection,
    ILogger<ClaudeSettingsService> logger)
{
    public const string ApiKeyEnvironmentVariable = "ANTHROPIC_API_KEY";

    /// <summary>Models the layout editor is known to work with (structured output, effort and fallbacks).</summary>
    public static readonly IReadOnlyList<(string Id, string Label)> Models =
    [
        ("claude-opus-5-5", "Claude Opus 5.5 (recommended)"),
        ("claude-sonnet-5-5", "Claude Sonnet 5.5 (faster, lower cost)"),
        ("claude-fable-5-1", "Claude Fable 5.1 (most capable, highest cost)"),
    ];

    private readonly IDataProtector _protector = dataProtection.CreateProtector("Hawker.ClaudeApiKey");

    public async Task<ClaudeSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var settings = await db.AppSettings.SingleAsync(cancellationToken);

        var fromEnvironment = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return new ClaudeSettings(fromEnvironment.Trim(), ApiKeySource.Environment, settings.ClaudeModel);
        }

        if (settings.EncryptedApiKey is { } encrypted)
        {
            try
            {
                return new ClaudeSettings(_protector.Unprotect(encrypted), ApiKeySource.Settings, settings.ClaudeModel);
            }
            catch (CryptographicException ex)
            {
                // The data protection keys were lost or replaced; the stored key has to be entered again.
                logger.LogWarning(ex, "The stored Claude API key could not be decrypted; please enter it again in Settings.");
            }
        }

        return new ClaudeSettings(null, ApiKeySource.None, settings.ClaudeModel);
    }

    /// <param name="apiKey">Null or blank removes the stored key.</param>
    public async Task SaveApiKeyAsync(string? apiKey, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var settings = await db.AppSettings.SingleAsync(cancellationToken);
        settings.EncryptedApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : _protector.Protect(apiKey.Trim());
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveModelAsync(string model, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var settings = await db.AppSettings.SingleAsync(cancellationToken);
        settings.ClaudeModel = string.IsNullOrWhiteSpace(model) ? new AppSettings().ClaudeModel : model.Trim();
        await db.SaveChangesAsync(cancellationToken);
    }
}
