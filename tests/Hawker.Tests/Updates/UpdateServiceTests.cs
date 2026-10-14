using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using Hawker.Web.Updates;
using Microsoft.Extensions.Configuration;

namespace Hawker.Tests.Updates;

public sealed class UpdateServiceTests : IDisposable
{
    private readonly UpdateFixture _fixture = new();

    private UpdateService Updates => _fixture.Service;

    [Fact]
    public async Task Finds_a_newer_release_and_offers_it_once()
    {
        var changes = 0;
        Updates.Changed += () => changes++;

        await Updates.CheckAsync();

        Assert.Equal(UpdateService.DefaultReleaseUrl, _fixture.Requested.Single().AbsoluteUri);
        Assert.True(Updates.IsUpdateAvailable);
        Assert.Equal("99.0.0", Updates.Latest!.Tag);
        Assert.NotNull(Updates.LastChecked);
        Assert.Null(Updates.CheckError);
        Assert.True(changes > 0);
        Assert.True(Updates.TakePrompt());
        Assert.False(Updates.TakePrompt());

        // A still newer release found later is offered again.
        _fixture.Respond = _ => UpdateFixture.Json(UpdateFixture.Release("99.1.0"));
        await Updates.CheckAsync();
        Assert.True(Updates.TakePrompt());
    }

    [Fact]
    public async Task An_older_or_equal_release_is_not_an_update()
    {
        _fixture.Respond = _ => UpdateFixture.Json(UpdateFixture.Release("0.0.1"));

        await Updates.CheckAsync();

        Assert.False(Updates.IsUpdateAvailable);
        Assert.False(Updates.TakePrompt());
    }

    [Fact]
    public async Task Never_prompts_while_developing()
    {
        using var development = new UpdateFixture(environment: "Development");

        await development.Service.CheckAsync();

        Assert.True(development.Service.IsUpdateAvailable);
        Assert.False(development.Service.TakePrompt());
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "{\"message\":\"API rate limit exceeded\"}")]
    [InlineData(HttpStatusCode.OK, "<html>not json</html>")]
    public async Task A_failed_check_is_reported_not_thrown(HttpStatusCode status, string body)
    {
        _fixture.Respond = _ => UpdateFixture.Json(body, status);

        await Updates.CheckAsync();

        Assert.NotNull(Updates.CheckError);
        Assert.False(Updates.IsUpdateAvailable);
        Assert.Equal(UpdateStatus.Idle, Updates.Status);
    }

    [Fact]
    public async Task Never_downloads_when_this_copy_cant_install_itself()
    {
        await Updates.CheckAsync();

        // Tests run under dotnet, so there is no installed copy to replace.
        Assert.Null(Updates.Target);
        Assert.NotNull(Updates.CannotInstallReason);
        await Updates.InstallAsync();

        Assert.Single(_fixture.Requested);
        Assert.False(_fixture.Lifetime.Stopped);
    }

    [Fact]
    public void Picks_up_how_the_update_that_started_it_went()
    {
        var files = new UpdateFiles(_fixture.Paths.DataDirectory);
        var current = AppVersion.Display(Updates.CurrentVersion);
        UpdateFiles.Write(files.ResultPath, new UpdateResult(true, current, "0.0.1", null, DateTimeOffset.UtcNow));

        Updates.ReportStarted();

        Assert.Equal(Environment.ProcessId, UpdateFiles.Read<RunningMarker>(files.RunningPath)!.ProcessId);
        Assert.True(Updates.LastResult!.Succeeded);
        Assert.False(File.Exists(files.ResultPath));
    }

    [Fact]
    public void A_rolled_back_update_is_reported_by_the_version_that_came_back()
    {
        var files = new UpdateFiles(_fixture.Paths.DataDirectory);
        var current = AppVersion.Display(Updates.CurrentVersion);
        UpdateFiles.Write(files.ResultPath, new UpdateResult(false, "99.0.0", current, "It stopped as soon as it started.", DateTimeOffset.UtcNow));

        Updates.ReportStarted();

        Assert.False(Updates.LastResult!.Succeeded);
        Assert.Equal("It stopped as soon as it started.", Updates.LastResult.Error);
    }

    [Fact]
    public async Task A_version_that_just_failed_to_install_is_not_offered_again_at_startup()
    {
        var files = new UpdateFiles(_fixture.Paths.DataDirectory);
        UpdateFiles.Write(files.ResultPath, new UpdateResult(false, "99.0.0", AppVersion.Display(Updates.CurrentVersion), "It stopped.", DateTimeOffset.UtcNow));
        Updates.ReportStarted();

        await Updates.CheckAsync();
        Assert.True(Updates.IsUpdateAvailable);
        Assert.False(Updates.TakePrompt());

        _fixture.Respond = _ => UpdateFixture.Json(UpdateFixture.Release("99.0.1"));
        await Updates.CheckAsync();
        Assert.True(Updates.TakePrompt());
    }

    [Fact]
    public void Old_or_unrelated_results_are_dropped_quietly()
    {
        var files = new UpdateFiles(_fixture.Paths.DataDirectory);
        UpdateFiles.Write(files.ResultPath, new UpdateResult(true, "98.0.0", "97.0.0", null, DateTimeOffset.UtcNow));

        Updates.ReportStarted();

        Assert.Null(Updates.LastResult);
        Assert.False(File.Exists(files.ResultPath));
    }

    [Fact]
    public void Restarts_the_same_way_without_opening_the_dashboard_twice()
    {
        var config = new ConfigurationBuilder().Build();

        var arguments = UpdateService.RestartArguments(["--Hawker:OpenDashboardOnStart=true", "--urls", "http://localhost:6000"], config);

        // On Linux the terminal it ran in is gone, so it comes back with its tray icon instead.
        string[] expected = OperatingSystem.IsLinux()
            ? ["--Hawker:OpenDashboardOnStart=true", "--urls", "http://localhost:6000", "--headless", "--Hawker:OpenDashboardOnStart=false"]
            : ["--Hawker:OpenDashboardOnStart=true", "--urls", "http://localhost:6000", "--Hawker:OpenDashboardOnStart=false"];
        Assert.Equal(expected, arguments);
    }

    [Fact]
    public void Headless_copies_restart_headless()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection([new("Hawker:Headless", "true")]).Build();

        Assert.Equal(["--headless", "--Hawker:OpenDashboardOnStart=false"], UpdateService.RestartArguments(["--headless"], config));
    }

    [Fact]
    public void Unpacks_a_zip_release_and_finds_the_program_in_its_top_folder()
    {
        var work = Path.Combine(_fixture.Paths.DataDirectory, "zip");
        var source = Path.Combine(work, "source", "hawker-0.3.0-win-x64");
        Directory.CreateDirectory(Path.Combine(source, "wwwroot"));
        File.WriteAllText(Path.Combine(source, "Hawker.Web.exe"), "program");
        File.WriteAllText(Path.Combine(source, "wwwroot", "app.css"), "css");
        ZipFile.CreateFromDirectory(Path.Combine(work, "source"), Path.Combine(work, "hawker-win-x64.zip"));

        UpdateService.Unpack(Path.Combine(work, "hawker-win-x64.zip"), Path.Combine(work, "out"));

        Assert.Equal(Path.Combine(work, "out", "hawker-0.3.0-win-x64"), UpdateService.PackageRoot(Path.Combine(work, "out"), "Hawker.Web.exe"));
        Assert.Null(UpdateService.PackageRoot(Path.Combine(work, "out"), "Hawker.Web"));
    }

    [Fact]
    public void Unpacks_a_tar_gz_release_keeping_the_program_runnable()
    {
        var work = Path.Combine(_fixture.Paths.DataDirectory, "tar");
        var source = Path.Combine(work, "source", "hawker-0.3.0-linux-x64");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "Hawker.Web"), "#!/bin/sh");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(source, "Hawker.Web"), (UnixFileMode)0b111_101_101);
        }

        var archive = Path.Combine(work, "hawker-linux-x64.tar.gz");
        using (var file = File.Create(archive))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        {
            TarFile.CreateFromDirectory(Path.Combine(work, "source"), gzip, includeBaseDirectory: false);
        }

        UpdateService.Unpack(archive, Path.Combine(work, "out"));

        var root = UpdateService.PackageRoot(Path.Combine(work, "out"), "Hawker.Web");
        Assert.Equal(Path.Combine(work, "out", "hawker-0.3.0-linux-x64"), root);
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(Path.Combine(root!, "Hawker.Web")).HasFlag(UnixFileMode.UserExecute));
        }
    }

    public void Dispose() => _fixture.Dispose();
}
