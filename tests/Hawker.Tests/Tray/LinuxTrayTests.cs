using System.Collections.Concurrent;
using Hawker.Web.Tray;
using Hawker.Web.Tray.Linux;
using Microsoft.Extensions.Logging.Abstractions;
using Tmds.DBus.Protocol;

namespace Hawker.Tests.Tray;

public sealed class LinuxTrayTests
{
    private readonly List<string> _clicked = [];

    [Fact]
    public void Menu_items_get_ids_after_the_root()
    {
        var menu = Menu();

        Assert.Equal([1, 2, 3, 4], menu.ChildIds);
        Assert.Equal("submenu", menu.Properties(0)!["children-display"]);
        Assert.Null(menu.Properties(5));
    }

    [Fact]
    public void Menu_properties_carry_labels_separators_and_disabled_items()
    {
        var menu = Menu();

        Assert.Equal(new Dictionary<string, object> { ["label"] = "Hawker 1.2.3", ["enabled"] = false }, menu.Properties(1));
        Assert.Equal(new Dictionary<string, object> { ["label"] = "Open dashboard", ["enabled"] = true }, menu.Properties(2));
        Assert.Equal(new Dictionary<string, object> { ["type"] = "separator" }, menu.Properties(3));
    }

    [Fact]
    public void Clicking_runs_only_enabled_items()
    {
        var menu = Menu();

        Assert.True(menu.Click(1));
        Assert.True(menu.Click(2));
        Assert.True(menu.Click(4));
        Assert.False(menu.Click(9));

        Assert.Equal(["open", "quit"], _clicked);
    }

    [Fact]
    public void Replacing_the_items_bumps_the_revision()
    {
        var menu = Menu();
        var before = menu.Revision;

        menu.Replace([new TrayMenuItem("Quit anyway", () => _clicked.Add("quit anyway"))], connection: null);

        Assert.Equal(before + 1, menu.Revision);
        Assert.Equal("Quit anyway", menu.Properties(1)!["label"]);
        Assert.Null(menu.Properties(2));
    }

    [Fact]
    public void Without_a_session_bus_there_is_no_tray()
    {
        using var tray = new StatusNotifierTrayIcon(NullLogger<StatusNotifierTrayIcon>.Instance, busAddress: null, StatusNotifierTrayIcon.DefaultWatcherName);

        Assert.False(tray.Show("Hawker", [], () => { }));
    }

    [Fact]
    [Trait("Category", "System")]
    public void Without_a_watcher_on_the_bus_there_is_no_tray()
    {
        using var tray = new StatusNotifierTrayIcon(NullLogger<StatusNotifierTrayIcon>.Instance, DBusAddress.Session, UniqueWatcherName());

        Assert.False(tray.Show("Hawker", [], () => { }));
    }

    [Fact]
    [Trait("Category", "System")]
    public async Task Registers_with_the_watcher_and_again_when_it_restarts()
    {
        var watcherName = UniqueWatcherName();
        using var tray = new StatusNotifierTrayIcon(NullLogger<StatusNotifierTrayIcon>.Instance, DBusAddress.Session, watcherName);

        using (var first = await FakeWatcher.StartAsync(watcherName))
        {
            Assert.True(tray.Show("Hawker", [], () => { }));
            Assert.Equal($"org.kde.StatusNotifierItem-{Environment.ProcessId}-1", await first.NextRegistrationAsync());
        }

        using var second = await FakeWatcher.StartAsync(watcherName);
        Assert.Equal($"org.kde.StatusNotifierItem-{Environment.ProcessId}-1", await second.NextRegistrationAsync());
    }

    private DbusMenu Menu() => new(
        [
            new TrayMenuItem("Hawker 1.2.3", null, Enabled: false),
            new TrayMenuItem("Open dashboard", () => _clicked.Add("open")),
            TrayMenuItem.Separator,
            new TrayMenuItem("Quit Hawker", () => _clicked.Add("quit")),
        ],
        action => action());

    private static string UniqueWatcherName() => $"org.hawker.Test.Watcher{Guid.NewGuid():N}";

    /// <summary>Owns a watcher name on the session bus and records RegisterStatusNotifierItem calls.</summary>
    private sealed class FakeWatcher : IPathMethodHandler, IDisposable
    {
        private readonly DBusConnection _connection;
        private readonly BlockingCollection<string> _registrations = [];

        private FakeWatcher(DBusConnection connection) => _connection = connection;

        public string Path => "/StatusNotifierWatcher";

        public bool HandlesChildPaths => false;

        public static async Task<FakeWatcher> StartAsync(string name)
        {
            var connection = new DBusConnection(DBusAddress.Session!);
            await connection.ConnectAsync();
            var watcher = new FakeWatcher(connection);
            connection.AddMethodHandler(watcher);
            await connection.RequestNameAsync(name);
            return watcher;
        }

        public Task<string> NextRegistrationAsync() =>
            Task.Run(() => _registrations.TryTake(out var item, TimeSpan.FromSeconds(10)) ? item : "(none within 10 s)");

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            if (context.Request.MemberAsString == "RegisterStatusNotifierItem")
            {
                _registrations.Add(context.Request.GetBodyReader().ReadString());
            }

            var writer = context.CreateReplyWriter("");
            try
            {
                context.Reply(writer.CreateMessage());
            }
            finally
            {
                writer.Dispose();
            }

            return ValueTask.CompletedTask;
        }

        public void Dispose() => _connection.Dispose();
    }
}
