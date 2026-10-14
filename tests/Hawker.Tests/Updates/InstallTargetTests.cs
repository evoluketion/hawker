using Hawker.Web.Updates;

namespace Hawker.Tests.Updates;

public sealed class InstallTargetTests : IDisposable
{
    private readonly string _app = Path.Combine(Path.GetTempPath(), $"hawker-install-test-{Guid.NewGuid():N}");
    private readonly string _executable = OperatingSystem.IsWindows() ? "Hawker.Web.exe" : "Hawker.Web";

    public InstallTargetTests() => Directory.CreateDirectory(Path.Combine(_app, "wwwroot"));

    private (InstallTarget? Target, string? Reason) Detect(bool development = false, string? process = null, bool windowsService = false, bool systemd = false) =>
        InstallTarget.Detect(development, _app, process ?? Path.Combine(_app, _executable), windowsService, systemd);

    [Fact]
    public void An_unpacked_release_that_started_itself_can_update()
    {
        var (target, reason) = Detect();

        Assert.Null(reason);
        Assert.Equal(Path.TrimEndingDirectorySeparator(_app), target!.AppDirectory);
        Assert.Equal(_executable, target.ExecutableName);
        Assert.Equal(OperatingSystem.IsWindows() ? "hawker-win-x64.zip" : "hawker-linux-x64.tar.gz", target.PackageName);
    }

    [Fact]
    public void Running_from_source_cannot()
    {
        Assert.Contains("source code", Detect(development: true).Reason);
        Assert.Contains("source code", Detect(process: "/usr/bin/dotnet").Reason);

        Directory.Delete(Path.Combine(_app, "wwwroot"));
        Assert.Contains("source code", Detect().Reason);
    }

    [Fact]
    public void Services_are_updated_by_hand()
    {
        Assert.Contains("Windows service", Detect(windowsService: true).Reason);
        Assert.Contains("systemd service", Detect(systemd: true).Reason);
        Assert.Null(Detect(systemd: true).Target);
    }

    [Theory]
    [InlineData("0::/system.slice/hawker.service\n", true)]
    [InlineData("0::/user.slice/user-1000.slice/user@1000.service/app.slice/hawker.service\n", true)]
    [InlineData("0::/user.slice/user-1000.slice/user@1000.service/app.slice/app-org.kde.konsole-87452.scope/tab(87504).scope\n", false)]
    [InlineData("0::/user.slice/user-1000.slice/session-2.scope\n", false)]
    [InlineData("12:pids:/\n0::/init.scope\n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_service_units_own_control_group_counts_as_a_service(string? controlGroup, bool service) =>
        Assert.Equal(service, InstallTarget.IsServiceControlGroup(controlGroup));

    public void Dispose() => Directory.Delete(_app, recursive: true);
}
