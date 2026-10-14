using Hawker.Web.Updates;

namespace Hawker.Tests.Updates;

public sealed class UpdateApplierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hawker-applier-test-{Guid.NewGuid():N}");
    private readonly FakeProcesses _processes = new();

    public UpdateApplierTests()
    {
        Write(App, "Hawker.Web", "old program");
        Write(App, "Hawker.Core.dll", "old core");
        Write(App, "appsettings.json", "the user's settings");
        Write(App, "Start Hawker.cmd", "old script");
        Write(App, "wwwroot/app.css", "old css");
        Write(Staged, "Hawker.Web", "new program");
        Write(Staged, "Hawker.Core.dll", "new core");
        Write(Staged, "appsettings.json", "shipped settings");
        Write(Staged, "wwwroot/app.css", "new css");
        Write(Staged, "wwwroot/new.js", "added in the new version");
        Write(Data, "hawker.db", "old database");
        Write(Data, "hawker.db-wal", "old wal");
    }

    private string App => Path.Combine(_root, "app");
    private string Staged => Path.Combine(_root, "data", "updates", "staging", "unpacked", "hawker-0.3.0-linux-x64");
    private string Data => Path.Combine(_root, "data");

    private UpdatePlan Plan => new(App, Staged, Data, "Hawker.Web", WaitForProcessId: 4242, "0.3.0", "0.2.1", ["--headless", "--Hawker:OpenDashboardOnStart=false"]);

    private UpdateApplier Applier => new(Plan, _processes)
    {
        StopTimeout = TimeSpan.FromSeconds(1),
        StartTimeout = TimeSpan.FromSeconds(2),
        PollInterval = TimeSpan.FromMilliseconds(20),
    };

    [Fact]
    public void Replaces_the_program_keeps_the_users_settings_and_restarts_it_the_same_way()
    {
        _processes.OnStart = process =>
        {
            // The new version comes up and changes the database, as a migration would.
            Write(Data, "hawker.db", "migrated database");
            UpdateFiles.Write(new UpdateFiles(Data).RunningPath, new RunningMarker(process.Id, "0.3.0"));
        };

        var result = Applier.Apply();

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("new program", Read(App, "Hawker.Web"));
        Assert.Equal("new core", Read(App, "Hawker.Core.dll"));
        Assert.Equal("new css", Read(App, "wwwroot/app.css"));
        Assert.Equal("added in the new version", Read(App, "wwwroot/new.js"));
        Assert.Equal("the user's settings", Read(App, "appsettings.json"));
        Assert.Equal("old script", Read(App, "Start Hawker.cmd"));
        Assert.Equal("migrated database", Read(Data, "hawker.db"));

        var started = Assert.Single(_processes.Started);
        Assert.Equal(Path.Combine(App, "Hawker.Web"), started.Executable);
        Assert.Equal(["--headless", "--Hawker:OpenDashboardOnStart=false"], started.Arguments);
        Assert.Equal([4242], _processes.WaitedFor);

        var recorded = UpdateFiles.Read<UpdateResult>(new UpdateFiles(Data).ResultPath)!;
        Assert.True(recorded.Succeeded);
        Assert.Equal(("0.3.0", "0.2.1"), (recorded.Version, recorded.PreviousVersion));
    }

    [Fact]
    public void A_fresh_install_without_settings_gets_the_shipped_ones()
    {
        File.Delete(Path.Combine(App, "appsettings.json"));
        _processes.OnStart = process => UpdateFiles.Write(new UpdateFiles(Data).RunningPath, new RunningMarker(process.Id, "0.3.0"));

        Assert.True(Applier.Apply().Succeeded);
        Assert.Equal("shipped settings", Read(App, "appsettings.json"));
    }

    [Fact]
    public void A_new_version_that_exits_at_once_is_rolled_back_with_its_database()
    {
        _processes.OnStart = process =>
        {
            if (_processes.Started.Count == 1)
            {
                Write(Data, "hawker.db", "half-migrated database");
                Write(Data, "hawker.db-shm", "new shm");
                process.HasExited = true;
            }
        };

        var result = Applier.Apply();

        Assert.False(result.Succeeded);
        Assert.Contains("stopped as soon as it started", result.Error);
        AssertOldVersionRestored();
        Assert.Equal(2, _processes.Started.Count);
        Assert.False(UpdateFiles.Read<UpdateResult>(new UpdateFiles(Data).ResultPath)!.Succeeded);
    }

    [Fact]
    public void A_new_version_that_never_reports_in_is_stopped_and_rolled_back()
    {
        var result = Applier.Apply();

        Assert.False(result.Succeeded);
        Assert.Contains("didn't finish starting", result.Error);
        Assert.True(_processes.Started[0].Killed);
        AssertOldVersionRestored();
        Assert.Equal(2, _processes.Started.Count);
        Assert.Equal(Path.Combine(App, "Hawker.Web"), _processes.Started[1].Executable);
    }

    [Fact]
    public void Nothing_changes_if_the_old_version_never_stops()
    {
        _processes.StillRunning = true;

        var result = Applier.Apply();

        Assert.False(result.Succeeded);
        Assert.Equal("old program", Read(App, "Hawker.Web"));
        Assert.False(File.Exists(Path.Combine(App, "wwwroot/new.js")));
        Assert.Empty(_processes.Started);
    }

    private void AssertOldVersionRestored()
    {
        Assert.Equal("old program", Read(App, "Hawker.Web"));
        Assert.Equal("old core", Read(App, "Hawker.Core.dll"));
        Assert.Equal("old css", Read(App, "wwwroot/app.css"));
        Assert.Equal("the user's settings", Read(App, "appsettings.json"));
        Assert.False(File.Exists(Path.Combine(App, "wwwroot/new.js")));
        Assert.Equal("old database", Read(Data, "hawker.db"));
        Assert.Equal("old wal", Read(Data, "hawker.db-wal"));
        Assert.False(File.Exists(Path.Combine(Data, "hawker.db-shm")));
    }

    private static void Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static string Read(string root, string relative) => File.ReadAllText(Path.Combine(root, relative));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class FakeProcesses : IUpdateProcesses
    {
        public List<FakeProcess> Started { get; } = [];
        public List<int> WaitedFor { get; } = [];
        public bool StillRunning { get; set; }
        public Action<FakeProcess>? OnStart { get; set; }

        public bool WaitForExit(int processId, TimeSpan timeout)
        {
            WaitedFor.Add(processId);
            return !StillRunning;
        }

        public IStartedProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory)
        {
            var process = new FakeProcess(1000 + Started.Count, executable, arguments);
            Started.Add(process);
            OnStart?.Invoke(process);
            return process;
        }
    }

    private sealed class FakeProcess(int id, string executable, IReadOnlyList<string> arguments) : IStartedProcess
    {
        public int Id => id;
        public string Executable => executable;
        public IReadOnlyList<string> Arguments => arguments;
        public bool HasExited { get; set; }
        public bool Killed { get; private set; }

        public void Kill()
        {
            Killed = true;
            HasExited = true;
        }
    }
}
