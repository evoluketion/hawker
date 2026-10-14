using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Hawker.Core;
using Hawker.Core.Data;
using Hawker.Core.Domain;
using Hawker.Core.Feeds;
using Hawker.Core.Selection;
using Hawker.Web.Printing;
using Microsoft.EntityFrameworkCore;

namespace Hawker.Web.Editions;

/// <summary>
/// Carries out one run of an edition: build it, archive the PDF, print it (honouring the retry setting, which defaults to
/// none), and record what happened. Articles count as printed only once the paper has actually been printed.
/// </summary>
public sealed partial class EditionRunService(
    IDbContextFactory<HawkerDbContext> dbFactory,
    IEditionComposer composer,
    IPrintService printer,
    SeenArticleStore seenArticles,
    HawkerPaths paths,
    TimeProvider time,
    IHostApplicationLifetime lifetime,
    RunEvents events,
    ILogger<EditionRunService> logger)
{
    /// <summary>How long after printing to look at the print queue for a stuck job.</summary>
    public TimeSpan JobCheckDelay { get; init; } = TimeSpan.FromMinutes(3);

    /// <returns>The recorded run, or null if the edition no longer exists or is disabled.</returns>
    public async Task<EditionRun?> ExecuteAsync(RunRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var edition = await db.Editions.Include(e => e.Feeds).SingleOrDefaultAsync(e => e.Id == request.EditionId, cancellationToken);
        if (edition is null || (!edition.Enabled && request.Trigger is RunTrigger.Schedule or RunTrigger.MissedOnStartup))
        {
            return null;
        }

        var isPreview = request.Trigger == RunTrigger.Preview;
        var issueNumber = edition.IssueCounter + 1;
        var run = new EditionRun
        {
            EditionId = edition.Id,
            Trigger = request.Trigger,
            ScheduledFor = request.ScheduledFor,
            StartedAt = time.GetUtcNow(),
            Status = RunStatus.Running,
            IssueNumber = isPreview ? 0 : issueNumber,
        };
        db.EditionRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        events.Notify(run.Id);

        var log = new List<string> { Describe(request) };
        try
        {
            var composed = await composer.ComposeAsync(edition, issueNumber, cancellationToken);
            log.AddRange(composed.Log);

            run.PdfPath = await ArchiveAsync(edition, composed.Pdf, isPreview, cancellationToken);
            run.ArticleUrls = composed.Articles.Select(a => a.Url).ToList();
            log.Add($"Saved to {run.PdfPath}.");
            if (!isPreview)
            {
                edition.IssueCounter = issueNumber;
            }

            // Stories left out as non-articles count as seen too, so they are not picked again next time.
            var hashes = composed.Articles.Select(a => a.UrlHash).Concat(composed.LeftOutUrlHashes ?? []).ToList();
            if (isPreview)
            {
                run.Status = RunStatus.Succeeded;
            }
            else if (!edition.AutoPrint)
            {
                log.Add("Auto-print is off for this edition; the PDF is in the archive.");
                await seenArticles.MarkSeenAsync(edition.Id, hashes, cancellationToken);
                run.Status = RunStatus.Succeeded;
            }
            else
            {
                var settings = await db.AppSettings.SingleAsync(cancellationToken);
                var outcome = await PrintAsync(run.PdfPath, PrintOptions.For(edition), settings.PrintRetryCount,
                    TimeSpan.FromSeconds(Math.Max(0, settings.PrintRetryDelaySeconds)), log, cancellationToken);
                if (outcome.Result is { } printed)
                {
                    run.Printed = true;
                    run.PrintedAt = time.GetUtcNow();
                    run.Status = RunStatus.Succeeded;
                    await seenArticles.MarkSeenAsync(edition.Id, hashes, cancellationToken);
                    StartJobCheck(run.Id, printed);
                }
                else
                {
                    run.Status = RunStatus.PrintFailed;
                    run.Error = outcome.Error;
                    log.Add("The paper is in the archive and can be reprinted from there. Its articles stay available for the next edition.");
                }
            }
        }
        catch (NoArticlesException ex)
        {
            log.AddRange(ex.Log);
            run.Status = RunStatus.Failed;
            run.Error = ex.Message;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Run of {Edition} failed", edition.Name);
            log.Add($"Failed: {ex.Message}");
            run.Status = RunStatus.Failed;
            run.Error = ex.Message;
        }
        finally
        {
            run.FinishedAt = time.GetUtcNow();
            run.Log = string.Join('\n', log);
            await db.SaveChangesAsync(CancellationToken.None);
            events.Notify(run.Id);
        }

        logger.LogInformation("Run {Run} of {Edition}: {Status}", run.Id, edition.Name, run.Status);
        return run;
    }

    /// <summary>
    /// Prints an archived paper again, once, at the user's request. Its articles are marked as printed if this succeeds.
    /// </summary>
    public async Task<EditionRun> ReprintAsync(int runId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var run = await db.EditionRuns.Include(r => r.Edition).SingleAsync(r => r.Id == runId, cancellationToken);
        if (run.PdfPath is null || !File.Exists(run.PdfPath))
        {
            throw new InvalidOperationException("The PDF for this run is no longer in the archive.");
        }

        var log = new List<string> { $"Reprint requested at {time.GetUtcNow():yyyy-MM-dd HH:mm} UTC." };
        var outcome = await PrintAsync(run.PdfPath, PrintOptions.For(run.Edition!), retries: 0, TimeSpan.Zero, log, cancellationToken);
        if (outcome.Result is { } printed)
        {
            run.Printed = true;
            run.PrintedAt = time.GetUtcNow();
            run.Error = null;
            if (run.Status == RunStatus.PrintFailed)
            {
                run.Status = RunStatus.Succeeded;
            }

            if (run.Trigger != RunTrigger.Preview)
            {
                await seenArticles.MarkSeenAsync(run.EditionId, run.ArticleUrls.Select(UrlNormalizer.Hash), cancellationToken);
            }

            StartJobCheck(run.Id, printed);
        }
        else
        {
            run.Error = outcome.Error;
        }

        run.Log = string.Join('\n', [run.Log, .. log]);
        await db.SaveChangesAsync(CancellationToken.None);
        events.Notify(run.Id);
        return run;
    }

    /// <summary>
    /// One attempt, plus up to <paramref name="retries"/> more when the print system clearly rejected the job. A failure
    /// that may already have printed (a timeout) is never retried.
    /// </summary>
    public async Task<(PrintResult? Result, string? Error)> PrintAsync(
        string pdfPath, PrintOptions options, int retries, TimeSpan delay, List<string> log, CancellationToken cancellationToken)
    {
        var attempts = 1 + Math.Max(0, retries);
        string? error = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                var result = await printer.PrintAsync(pdfPath, options, cancellationToken);
                log.Add($"Sent to {result.Printer}{(result.JobId is null ? "" : $" as job {result.JobId}")}" +
                    $"{(options.Copies > 1 ? $", {options.Copies} copies" : "")}{(options.Duplex ? ", double-sided" : "")}.");
                return (result, null);
            }
            catch (PrintException ex)
            {
                error = ex.Message;
                log.Add(attempts == 1 ? $"Print failed: {ex.Message}" : $"Print attempt {attempt} of {attempts} failed: {ex.Message}");
                if (ex.MayHavePrinted)
                {
                    if (attempt < attempts)
                    {
                        log.Add("Not retrying, because the job may already have printed.");
                    }

                    break;
                }

                if (attempt < attempts)
                {
                    log.Add($"Retrying in {delay.TotalSeconds:0} seconds.");
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, time, cancellationToken);
                    }
                }
            }
        }

        return (null, error);
    }

    private void StartJobCheck(int runId, PrintResult job)
    {
        if (job.JobId is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var stopping = lifetime.ApplicationStopping;
                await Task.Delay(JobCheckDelay, time, stopping);
                if (await printer.CheckJobAsync(job, stopping) is not { } problem)
                {
                    return;
                }

                await using var db = await dbFactory.CreateDbContextAsync(stopping);
                var run = await db.EditionRuns.SingleAsync(r => r.Id == runId, stopping);
                run.Log += $"\nPrint queue check: {problem}";
                run.Error ??= problem;
                await db.SaveChangesAsync(stopping);
                events.Notify(runId);
                logger.LogWarning("Run {Run}: {Problem}", runId, problem);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not check the print queue for run {Run}", runId);
            }
        });
    }

    private async Task<string> ArchiveAsync(Edition edition, byte[] pdf, bool isPreview, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(paths.ArchiveDirectory, $"{Slug(edition.Name)}-{edition.Id}");
        Directory.CreateDirectory(folder);

        var local = TimeZoneInfo.ConvertTime(time.GetUtcNow(), EditionComposer.ResolveTimeZone(edition.TimeZone));
        var baseName = local.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture) + (isPreview ? "-preview" : "");
        var path = Path.Combine(folder, baseName + ".pdf");
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(folder, $"{baseName}-{n}.pdf");
        }

        await File.WriteAllBytesAsync(path, pdf, cancellationToken);
        return path;
    }

    private static string Describe(RunRequest request) => request.Trigger switch
    {
        RunTrigger.Schedule => $"Scheduled run for {request.ScheduledFor:yyyy-MM-dd HH:mm zzz}.",
        RunTrigger.MissedOnStartup => $"Catching up the run scheduled for {request.ScheduledFor:yyyy-MM-dd HH:mm zzz}, missed while Hawker was not running.",
        RunTrigger.Preview => "Preview (not printed).",
        _ => "Run started by hand.",
    };

    public static string Slug(string name)
    {
        var slug = NonSlug().Replace(name.Normalize(NormalizationForm.FormKD).ToLowerInvariant(), "-").Trim('-');
        return slug.Length == 0 ? "edition" : slug[..Math.Min(slug.Length, 60)];
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();
}
