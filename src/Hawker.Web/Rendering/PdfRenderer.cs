using System.Net;
using Hawker.Core.Domain;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace Hawker.Web.Rendering;

public sealed class PdfOptions
{
    /// <summary>
    /// A Playwright browser channel such as "msedge" or "chrome" to use an installed browser. Defaults to "msedge" on
    /// Windows (always present there) and to Playwright's own Chromium elsewhere.
    /// </summary>
    public string? BrowserChannel { get; set; }

    /// <summary>Path to a specific Chromium-based browser executable. Takes precedence over the channel.</summary>
    public string? ExecutablePath { get; set; }
}

/// <summary>Turns newspaper HTML into a PDF with headless Chromium.</summary>
public sealed class PdfRenderer(IOptions<PdfOptions> options, ILogger<PdfRenderer> logger)
{
    private static readonly SemaphoreSlim InstallLock = new(1, 1);
    private static bool _installChecked;

    public async Task<byte[]> RenderAsync(string html, string editionName, PaperSize paperSize, CancellationToken cancellationToken = default)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await LaunchAsync(playwright, cancellationToken);
        var page = await browser.NewPageAsync();

        // Everything (fonts, images, styles) is inlined, so waiting for "load" is enough.
        await page.SetContentAsync(html, new PageSetContentOptions { WaitUntil = WaitUntilState.Load, Timeout = 120_000 });
        cancellationToken.ThrowIfCancellationRequested();

        return await page.PdfAsync(new PagePdfOptions
        {
            Format = paperSize == PaperSize.Letter ? "Letter" : "A4",
            PrintBackground = true,
            PreferCSSPageSize = true,
            DisplayHeaderFooter = true,
            HeaderTemplate = "<span></span>",
            FooterTemplate = FooterTemplate(editionName),
            Margin = new Margin { Top = "10mm", Bottom = "13mm", Left = "11mm", Right = "11mm" },
        });
    }

    // The footer is drawn by Chromium outside the page content, so it cannot use the embedded fonts.
    private static string FooterTemplate(string editionName) => $"""
        <div style="width:100%;padding:0 11mm;font-family:Georgia,'Times New Roman',serif;font-size:7pt;color:#444;display:flex;justify-content:space-between;">
          <span>{WebUtility.HtmlEncode(editionName)}</span>
          <span>Page <span class="pageNumber"></span> of <span class="totalPages"></span></span>
        </div>
        """;

    private async Task<IBrowser> LaunchAsync(IPlaywright playwright, CancellationToken cancellationToken)
    {
        var configured = options.Value;
        var channel = configured.BrowserChannel ?? (OperatingSystem.IsWindows() ? "msedge" : null);
        var launch = new BrowserTypeLaunchOptions { Headless = true, ExecutablePath = configured.ExecutablePath };
        if (configured.ExecutablePath is null && channel is not null)
        {
            launch.Channel = channel;
        }

        try
        {
            return await playwright.Chromium.LaunchAsync(launch);
        }
        catch (PlaywrightException ex) when (configured.ExecutablePath is null)
        {
            // The installed browser channel is unavailable, or Playwright's Chromium has not been downloaded yet.
            logger.LogWarning("Could not start {Browser} ({Message}); falling back to Playwright's Chromium.", channel ?? "Chromium", ex.Message.Split('\n')[0]);
            await EnsureChromiumInstalledAsync(cancellationToken);
            return await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        }
    }

    private async Task EnsureChromiumInstalledAsync(CancellationToken cancellationToken)
    {
        await InstallLock.WaitAsync(cancellationToken);
        try
        {
            if (_installChecked)
            {
                return;
            }

            logger.LogInformation("Installing Playwright Chromium (first run only)…");
            var exitCode = await Task.Run(() => Microsoft.Playwright.Program.Main(["install", "chromium"]), cancellationToken);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"Installing Playwright Chromium failed with exit code {exitCode}.");
            }

            _installChecked = true;
        }
        finally
        {
            InstallLock.Release();
        }
    }
}
