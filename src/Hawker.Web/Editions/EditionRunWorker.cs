namespace Hawker.Web.Editions;

/// <summary>Takes runs off the queue and carries them out one at a time.</summary>
public sealed class EditionRunWorker(EditionRunQueue queue, IServiceScopeFactory scopes, ILogger<EditionRunWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                // A fresh scope per run gives each run its own HTTP clients and services.
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<EditionRunService>().ExecuteAsync(request, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A broken run must never stop the worker; later editions still need printing.
                logger.LogError(ex, "Run for edition {Edition} could not be completed", request.EditionId);
            }
        }
    }
}
