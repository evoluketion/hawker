using Hawker.Web.Editions;
using Hawker.Web.Hosting;

namespace Hawker.Web.Tray;

/// <summary>
/// Shows the tray icon in headless mode, once the web server is listening. A desktop without a tray only gets a
/// warning: Hawker keeps printing either way.
/// </summary>
public sealed class TrayHostedService(
    ITrayIcon tray,
    EditionRunQueue runs,
    IConfiguration configuration,
    IBrowserLauncher browser,
    IHostApplicationLifetime lifetime,
    ILogger<TrayHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Headless.IsOn(configuration))
        {
            lifetime.ApplicationStarted.Register(Show);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        tray.Dispose();
        return Task.CompletedTask;
    }

    public IReadOnlyList<TrayMenuItem> BuildMenu() =>
    [
        new($"Hawker {Version}", null, Enabled: false),
        new("Open dashboard", OpenDashboard),
        TrayMenuItem.Separator,
        new("Quit Hawker", Quit),
    ];

    private static string Version => typeof(TrayHostedService).Assembly.GetName().Version?.ToString(3) ?? "";

    private void Show()
    {
        try
        {
            if (!tray.Show("Hawker", BuildMenu(), OpenDashboard))
            {
                logger.LogWarning("No system tray is available, so Hawker has no icon. It keeps running; open {Url} to use it", DashboardUrl.For(configuration));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not show the tray icon. Hawker keeps running; open {Url} to use it", DashboardUrl.For(configuration));
        }
    }

    /// <summary>Quitting mid-run abandons that paper, so it needs a yes first.</summary>
    private void Quit()
    {
        if (runs.IsRunning && !tray.Confirm(
                "Quit Hawker?",
                "A paper is being made. Quit anyway? It will be stopped and can be printed again from the archive.",
                "Quit anyway (a paper is being made)"))
        {
            return;
        }

        lifetime.StopApplication();
    }

    private void OpenDashboard() => browser.Open(DashboardUrl.For(configuration));
}
