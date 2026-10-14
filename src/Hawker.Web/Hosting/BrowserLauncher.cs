using System.Diagnostics;

namespace Hawker.Web.Hosting;

public interface IBrowserLauncher
{
    void Open(string url);
}

/// <summary>
/// Opens a page in the user's default browser. A failure is only logged: the dashboard can still be opened by hand.
/// </summary>
public sealed class BrowserLauncher(ILogger<BrowserLauncher> logger) : IBrowserLauncher
{
    public void Open(string url)
    {
        try
        {
            var startInfo = OperatingSystem.IsWindows()
                ? new ProcessStartInfo(url) { UseShellExecute = true }
                : new ProcessStartInfo("xdg-open") { ArgumentList = { url }, UseShellExecute = false };
            using var process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not open {Url} in the browser", url);
        }
    }
}
