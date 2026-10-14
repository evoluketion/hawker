using Hawker.Web.Hosting;

namespace Hawker.Tests.Hosting;

public sealed class StartupNoticeTests
{
    private readonly StringWriter _console = new();
    private readonly List<(string Title, string Message)> _desktop = [];

    [Fact]
    public void Writes_only_to_the_console_when_not_headless()
    {
        new StartupNotice(headless: false, _console, (t, m) => _desktop.Add((t, m))).Show("Hawker could not start", "Port taken.");

        Assert.Equal("Port taken." + Environment.NewLine, _console.ToString());
        Assert.Empty(_desktop);
    }

    [Fact]
    public void Also_shows_it_on_the_desktop_when_headless()
    {
        new StartupNotice(headless: true, _console, (t, m) => _desktop.Add((t, m))).Show("Hawker could not start", "Port taken.");

        Assert.Equal("Port taken." + Environment.NewLine, _console.ToString());
        Assert.Equal([("Hawker could not start", "Port taken.")], _desktop);
    }
}
