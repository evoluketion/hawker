namespace Hawker.Web.Hosting;

/// <summary>
/// Opens the dashboard once the web server is listening, when <c>Hawker:OpenDashboardOnStart</c> is on. Waiting for
/// ApplicationStarted means the page never loads before the server can answer it.
/// </summary>
public sealed class OpenDashboardOnStart(
    IConfiguration configuration,
    IBrowserLauncher browser,
    IHostApplicationLifetime lifetime) : IHostedService
{
    public static bool IsOn(IConfiguration configuration) => configuration.GetValue<bool>("Hawker:OpenDashboardOnStart");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsOn(configuration))
        {
            lifetime.ApplicationStarted.Register(() => browser.Open(DashboardUrl.For(configuration)));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
