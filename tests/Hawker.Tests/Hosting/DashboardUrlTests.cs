using Hawker.Web.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Hawker.Tests.Hosting;

public sealed class DashboardUrlTests
{
    [Fact]
    public void Defaults_to_the_standard_local_address()
    {
        Assert.Equal("http://localhost:5080", DashboardUrl.For(Config()));
    }

    [Fact]
    public void Uses_the_configured_endpoint()
    {
        var config = Config(("Kestrel:Endpoints:Http:Url", "http://localhost:6000"));

        Assert.Equal("http://localhost:6000", DashboardUrl.For(config));
    }

    [Fact]
    public async Task Opens_the_dashboard_once_started_when_the_option_is_on()
    {
        var browser = new FakeBrowser();
        var config = Config(("Hawker:OpenDashboardOnStart", "true"), ("Kestrel:Endpoints:Http:Url", "http://localhost:6000"));

        await new OpenDashboardOnStart(config, browser, new StartedLifetime()).StartAsync(CancellationToken.None);

        Assert.Equal(["http://localhost:6000"], browser.Opened);
    }

    [Fact]
    public async Task Leaves_the_browser_alone_when_the_option_is_off()
    {
        var browser = new FakeBrowser();

        await new OpenDashboardOnStart(Config(), browser, new StartedLifetime()).StartAsync(CancellationToken.None);

        Assert.Empty(browser.Opened);
    }

    [Fact]
    public async Task Waits_for_the_server_to_start_before_opening()
    {
        var browser = new FakeBrowser();
        var lifetime = new StartedLifetime(started: false);

        await new OpenDashboardOnStart(Config(("Hawker:OpenDashboardOnStart", "true")), browser, lifetime).StartAsync(CancellationToken.None);
        Assert.Empty(browser.Opened);

        lifetime.Start();
        Assert.Equal(["http://localhost:5080"], browser.Opened);
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private sealed class FakeBrowser : IBrowserLauncher
    {
        public List<string> Opened { get; } = [];

        public void Open(string url) => Opened.Add(url);
    }

    private sealed class StartedLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();

        public StartedLifetime(bool started = true)
        {
            if (started)
            {
                _started.Cancel();
            }
        }

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void Start() => _started.Cancel();

        public void StopApplication()
        {
        }
    }
}
