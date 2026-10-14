using Hawker.Web.Editions;
using Hawker.Web.Hosting;
using Hawker.Web.Updates;

namespace Hawker.Web.Tray;

/// <summary>
/// Shows the tray icon in headless mode, once the web server is listening. A desktop without a tray only gets a
/// warning: Hawker keeps printing either way.
/// </summary>
public sealed class TrayHostedService(
    ITrayIcon tray,
    EditionRunQueue runs,
    UpdateService updates,
    IConfiguration configuration,
    IBrowserLauncher browser,
    IHostApplicationLifetime lifetime,
    ILogger<TrayHostedService> logger) : IHostedService
{
    private readonly Lock _shownGate = new();
    private IReadOnlyList<TrayMenuItem>? _shown;

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
        updates.Changed -= RefreshMenu;
        tray.Dispose();
        return Task.CompletedTask;
    }

    public IReadOnlyList<TrayMenuItem> BuildMenu()
    {
        List<TrayMenuItem> menu = [new($"Hawker {AppVersion.Display(updates.CurrentVersion)}", null, Enabled: false), new("Open dashboard", OpenDashboard)];
        if (UpdateItem() is { } update)
        {
            menu.Add(update);
        }

        menu.AddRange([TrayMenuItem.Separator, new("Quit Hawker", Quit)]);
        return menu;
    }

    private TrayMenuItem? UpdateItem()
    {
        if (updates.Latest is not { } release || !updates.IsUpdateAvailable)
        {
            return null;
        }

        return updates.Status switch
        {
            UpdateStatus.Downloading => new("Downloading update…", null, Enabled: false),
            UpdateStatus.Restarting => new("Restarting into the update…", null, Enabled: false),
            _ when updates.Target is null => new($"Update {release.Tag} available…", () => browser.Open(release.PageUrl.AbsoluteUri)),
            _ => new($"Install update {release.Tag}…", InstallUpdate),
        };
    }

    /// <summary>Installs only after a yes; the dashboard shows how it went if it fails.</summary>
    private void InstallUpdate()
    {
        if (updates.Latest is not { } release || !tray.Confirm(
                $"Install Hawker {release.Tag}?",
                $"Hawker will download version {release.Tag} and restart. Your settings, feeds and printed papers are kept.",
                $"Click again to install {release.Tag}"))
        {
            return;
        }

        _ = updates.InstallAsync();
    }

    private void RefreshMenu()
    {
        var menu = BuildMenu();
        lock (_shownGate)
        {
            if (_shown is null || _shown.Select(i => (i.Text, i.Enabled)).SequenceEqual(menu.Select(i => (i.Text, i.Enabled))))
            {
                return;
            }

            _shown = menu;
        }

        tray.SetMenu(menu);
    }

    private void Show()
    {
        try
        {
            var menu = BuildMenu();
            lock (_shownGate)
            {
                _shown = menu;
            }

            updates.Changed += RefreshMenu;
            if (!tray.Show("Hawker", menu, OpenDashboard))
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
