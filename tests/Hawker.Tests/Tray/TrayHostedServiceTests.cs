using Hawker.Web.Editions;
using Hawker.Web.Hosting;
using Hawker.Web.Tray;
using Hawker.Tests.Updates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hawker.Tests.Tray;

public sealed class TrayHostedServiceTests : IDisposable
{
    private readonly FakeTray _tray = new();
    private readonly FakeBrowser _browser = new();
    private readonly FakeLifetime _lifetime = new();
    private readonly EditionRunQueue _runs = new();
    private readonly UpdateFixture _updates = new(_ => UpdateFixture.Json(UpdateFixture.Release("0.0.1")));

    [Fact]
    public async Task Shows_nothing_unless_headless()
    {
        await Service(Config()).StartAsync(CancellationToken.None);

        Assert.Null(_tray.Menu);
    }

    [Fact]
    public async Task Shows_the_menu_once_started_when_headless()
    {
        await Service(Config(("Hawker:Headless", "true"))).StartAsync(CancellationToken.None);

        Assert.NotNull(_tray.Menu);
        Assert.Matches(@"^Hawker \d+\.\d+\.\d+$", _tray.Menu[0].Text);
        Assert.False(_tray.Menu[0].Enabled);
        Assert.Equal(["Open dashboard", "", "Quit Hawker"], _tray.Menu.Skip(1).Select(i => i.Text));
        Assert.True(_tray.Menu[2].IsSeparator);
    }

    [Fact]
    public async Task Open_dashboard_and_the_icon_click_open_the_configured_address()
    {
        await Service(Config(("Hawker:Headless", "true"), ("Kestrel:Endpoints:Http:Url", "http://localhost:6000"))).StartAsync(CancellationToken.None);

        _tray.Menu![1].OnClick!();
        _tray.OnActivate!();

        Assert.Equal(["http://localhost:6000", "http://localhost:6000"], _browser.Opened);
    }

    [Fact]
    public async Task A_newer_release_adds_an_update_entry_that_opens_its_page_when_it_cant_install_itself()
    {
        await Service(Config(("Hawker:Headless", "true"))).StartAsync(CancellationToken.None);
        Assert.Equal(4, _tray.Menu!.Count);

        _updates.Respond = _ => UpdateFixture.Json(UpdateFixture.Release("v99.1.0"));
        await _updates.Service.CheckAsync();

        // Tests run under dotnet, which is never an installable copy.
        Assert.Equal(["Open dashboard", "Update v99.1.0 available…", "", "Quit Hawker"], _tray.Menu.Skip(1).Select(i => i.Text));
        _tray.Menu[2].OnClick!();
        Assert.Equal(["https://github.com/evoluketion/hawker/releases/tag/v99.1.0"], _browser.Opened);
        Assert.Equal(0, _tray.Questions);
    }

    [Fact]
    public async Task Quit_stops_the_app()
    {
        await Service(Config(("Hawker:Headless", "true"))).StartAsync(CancellationToken.None);

        _tray.Menu![3].OnClick!();

        Assert.True(_lifetime.Stopped);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Quit_mid_run_asks_first_and_stops_only_on_yes(bool answer)
    {
        await Service(Config(("Hawker:Headless", "true"))).StartAsync(CancellationToken.None);
        _runs.SetRunning(true);
        _tray.Answer = answer;

        _tray.Menu![3].OnClick!();

        Assert.Equal(1, _tray.Questions);
        Assert.Equal(answer, _lifetime.Stopped);
    }

    [Fact]
    public async Task Quit_while_idle_does_not_ask()
    {
        await Service(Config(("Hawker:Headless", "true"))).StartAsync(CancellationToken.None);

        _tray.Menu![3].OnClick!();

        Assert.Equal(0, _tray.Questions);
        Assert.True(_lifetime.Stopped);
    }

    [Fact]
    public async Task A_desktop_without_a_tray_keeps_running()
    {
        _tray.Available = false;

        await Service(Config(("Hawker:Headless", "true"))).StartAsync(CancellationToken.None);

        Assert.False(_lifetime.Stopped);
    }

    [Fact]
    public async Task Stopping_removes_the_icon()
    {
        var service = Service(Config(("Hawker:Headless", "true")));
        await service.StartAsync(CancellationToken.None);

        await service.StopAsync(CancellationToken.None);

        Assert.True(_tray.Disposed);
    }

    [Theory]
    [InlineData("--headless")]
    [InlineData("--HEADLESS")]
    [InlineData("--Hawker:Headless=true")]
    public void The_headless_switch_turns_the_setting_on(string arg)
    {
        var config = new ConfigurationBuilder().AddCommandLine(Headless.TranslateArgs(["--urls", "x", arg])).Build();

        Assert.True(Headless.IsOn(config));
        Assert.Equal("x", config["urls"]);
    }

    [Fact]
    public void Headless_is_off_by_default()
    {
        Assert.False(Headless.IsOn(new ConfigurationBuilder().AddCommandLine([]).Build()));
    }

    [Fact]
    public async Task Headless_never_opens_the_browser_on_start()
    {
        var config = Config(("Hawker:Headless", "true"), ("Hawker:OpenDashboardOnStart", "true"));

        await new OpenDashboardOnStart(config, _browser, _lifetime).StartAsync(CancellationToken.None);

        Assert.False(OpenDashboardOnStart.IsOn(config));
        Assert.Empty(_browser.Opened);
    }

    private TrayHostedService Service(IConfiguration config) =>
        new(_tray, _runs, _updates.Service, config, _browser, _lifetime, NullLogger<TrayHostedService>.Instance);

    public void Dispose() => _updates.Dispose();

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private sealed class FakeTray : ITrayIcon
    {
        public bool Available { get; set; } = true;
        public IReadOnlyList<TrayMenuItem>? Menu { get; private set; }
        public Action? OnActivate { get; private set; }
        public bool Disposed { get; private set; }
        public bool Answer { get; set; }
        public int Questions { get; private set; }

        public bool Confirm(string title, string message, string confirmLabel)
        {
            Questions++;
            return Answer;
        }

        public void SetMenu(IReadOnlyList<TrayMenuItem> menu) => Menu = menu;

        public bool Show(string tooltip, IReadOnlyList<TrayMenuItem> menu, Action onActivate)
        {
            Menu = menu;
            OnActivate = onActivate;
            return Available;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeBrowser : IBrowserLauncher
    {
        public List<string> Opened { get; } = [];

        public void Open(string url) => Opened.Add(url);
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => new(canceled: true);
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public bool Stopped { get; private set; }

        public void StopApplication() => Stopped = true;
    }
}
