using Hawker.Core.Data;
using Hawker.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Hawker.Web.Settings;

public sealed record GeneralSettings(int PrintRetryCount, int PrintRetryDelaySeconds, int MissedRunGraceMinutes);

/// <summary>Print retry and catch-up settings.</summary>
public sealed class GeneralSettingsService(IDbContextFactory<HawkerDbContext> dbFactory)
{
    public const int MaxPrintRetries = 5;

    public async Task<GeneralSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var s = await db.AppSettings.SingleAsync(cancellationToken);
        return new GeneralSettings(s.PrintRetryCount, s.PrintRetryDelaySeconds, s.MissedRunGraceMinutes);
    }

    public static IReadOnlyList<string> Validate(GeneralSettings settings)
    {
        var errors = new List<string>();
        if (settings.PrintRetryCount is < 0 or > MaxPrintRetries)
        {
            errors.Add($"Print retries must be between 0 and {MaxPrintRetries}.");
        }

        if (settings.PrintRetryDelaySeconds is < 10 or > 3600)
        {
            errors.Add("The wait between retries must be between 10 seconds and an hour.");
        }

        if (settings.MissedRunGraceMinutes is < 0 or > 24 * 60)
        {
            errors.Add("The catch-up window must be between 0 minutes and 24 hours.");
        }

        return errors;
    }

    /// <param name="retriesConfirmed">
    /// Must be true to raise print retries above 0: a retry can produce a duplicate paper, so the page asks first.
    /// </param>
    public async Task<IReadOnlyList<string>> SaveAsync(GeneralSettings settings, bool retriesConfirmed, CancellationToken cancellationToken = default)
    {
        var errors = Validate(settings).ToList();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var stored = await db.AppSettings.SingleAsync(cancellationToken);
        if (settings.PrintRetryCount > stored.PrintRetryCount && settings.PrintRetryCount > 0 && !retriesConfirmed)
        {
            errors.Add("Raising print retries needs confirmation.");
        }

        if (errors.Count > 0)
        {
            return errors;
        }

        stored.PrintRetryCount = settings.PrintRetryCount;
        stored.PrintRetryDelaySeconds = settings.PrintRetryDelaySeconds;
        stored.MissedRunGraceMinutes = settings.MissedRunGraceMinutes;
        await db.SaveChangesAsync(cancellationToken);
        return [];
    }

    public static GeneralSettings Defaults
    {
        get
        {
            var d = new AppSettings();
            return new GeneralSettings(d.PrintRetryCount, d.PrintRetryDelaySeconds, d.MissedRunGraceMinutes);
        }
    }
}
