using System.Diagnostics;

namespace Hawker.Web.Updates;

public interface IStartedProcess
{
    int Id { get; }
    bool HasExited { get; }
    void Kill();
}

public interface IUpdateProcesses
{
    /// <returns>False if the process is still running when the timeout passes.</returns>
    bool WaitForExit(int processId, TimeSpan timeout);

    IStartedProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory);
}

/// <summary>
/// Installs a downloaded update. It runs from a copy of the installed version's program files (started as
/// <c>Hawker.Web --apply-update plan.json</c>), so nothing it needs is in the folder it replaces, and a broken release
/// can't stop it putting things back: it waits for Hawker to exit, copies the new files over the old ones, keeping the
/// user's appsettings.json, and starts Hawker again. If the new version doesn't report itself running, the old files
/// and database come back and the old version is started.
/// </summary>
public sealed class UpdateApplier(UpdatePlan plan, IUpdateProcesses processes)
{
    public const string Switch = "--apply-update";

    /// <summary>Kept from the installed copy: users edit it.</summary>
    public const string UserSettingsFile = "appsettings.json";

    private static readonly string[] DatabaseFiles = ["hawker.db", "hawker.db-wal", "hawker.db-shm"];

    private readonly UpdateFiles _files = new(plan.DataDirectory);
    private readonly List<string> _added = [];

    /// <summary>A paper being made can hold Hawker up while it stops.</summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public static int Run(string planPath)
    {
        if (UpdateFiles.Read<UpdatePlan>(planPath) is not { } plan)
        {
            return 1;
        }

        return new UpdateApplier(plan, new SystemProcesses()).Apply().Succeeded ? 0 : 1;
    }

    public UpdateResult Apply()
    {
        Log($"Updating Hawker {plan.PreviousVersion} to {plan.Version} in {plan.AppDirectory}");
        Attempt("tell Hawker the update has begun", () => UpdateFiles.Write(_files.ApplierStartedPath, new RunningMarker(Environment.ProcessId, plan.PreviousVersion)));
        if (!processes.WaitForExit(plan.WaitForProcessId, StopTimeout))
        {
            // Nothing has changed, and the old version is still running.
            return Finish(false, $"Hawker {plan.PreviousVersion} didn't stop, so nothing was changed.");
        }

        try
        {
            Backup();
            Copy();
        }
        catch (Exception ex)
        {
            Log($"Copying the new files failed: {ex}");
            RollBack();
            return Finish(false, $"The new files couldn't be copied in: {ex.Message}");
        }

        // Reported by whichever version ends up running, so it is written before the new version starts.
        var result = Finish(true, null);
        var error = StartNewVersion(out var started);
        if (error is null)
        {
            Log($"Hawker {plan.Version} is running");
            return result;
        }

        Log(error);
        if (started is { HasExited: false })
        {
            started.Kill();
            processes.WaitForExit(started.Id, TimeSpan.FromSeconds(30));
        }

        RollBack();
        return Finish(false, error);
    }

    private string? StartNewVersion(out IStartedProcess? started)
    {
        started = null;
        try
        {
            File.Delete(_files.RunningPath);
            started = processes.Start(Path.Combine(plan.AppDirectory, plan.ExecutableName), plan.Arguments, plan.AppDirectory);
        }
        catch (Exception ex)
        {
            return $"Hawker {plan.Version} couldn't be started: {ex.Message}";
        }

        var deadline = DateTime.UtcNow + StartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (UpdateFiles.Read<RunningMarker>(_files.RunningPath) is { } marker && marker.ProcessId == started.Id)
            {
                return null;
            }

            if (started.HasExited)
            {
                return $"Hawker {plan.Version} stopped as soon as it started.";
            }

            Thread.Sleep(PollInterval);
        }

        return $"Hawker {plan.Version} didn't finish starting within {StartTimeout.TotalMinutes:0} minutes.";
    }

    private void Backup()
    {
        if (Directory.Exists(_files.BackupDirectory))
        {
            Directory.Delete(_files.BackupDirectory, recursive: true);
        }

        foreach (var relative in NewFiles())
        {
            var installed = Path.Combine(plan.AppDirectory, relative);
            if (File.Exists(installed))
            {
                CopyFile(installed, Path.Combine(_files.BackupDirectory, "app", relative));
            }
            else
            {
                _added.Add(relative);
            }
        }

        // The new version may change the database on its first start, which the old version might not understand.
        foreach (var name in DatabaseFiles.Where(n => File.Exists(Path.Combine(plan.DataDirectory, n))))
        {
            CopyFile(Path.Combine(plan.DataDirectory, name), Path.Combine(_files.BackupDirectory, "db", name));
        }
    }

    private void Copy()
    {
        foreach (var relative in NewFiles())
        {
            CopyFile(Path.Combine(plan.StagedDirectory, relative), Path.Combine(plan.AppDirectory, relative));
        }

        Log("New files copied in");
    }

    private IEnumerable<string> NewFiles() =>
        Directory.EnumerateFiles(plan.StagedDirectory, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(plan.StagedDirectory, f))
            .Where(r => !(string.Equals(r, UserSettingsFile, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(plan.AppDirectory, r))))
            .OrderBy(r => r, StringComparer.Ordinal);

    private void RollBack()
    {
        Log("Putting the previous version back");
        Attempt("restore the program files", () =>
        {
            var saved = Path.Combine(_files.BackupDirectory, "app");
            if (Directory.Exists(saved))
            {
                foreach (var file in Directory.EnumerateFiles(saved, "*", SearchOption.AllDirectories))
                {
                    CopyFile(file, Path.Combine(plan.AppDirectory, Path.GetRelativePath(saved, file)));
                }
            }

            foreach (var relative in _added)
            {
                Retry(() => File.Delete(Path.Combine(plan.AppDirectory, relative)));
            }
        });

        Attempt("restore the database", () =>
        {
            var saved = Path.Combine(_files.BackupDirectory, "db");
            foreach (var name in DatabaseFiles)
            {
                var backup = Path.Combine(saved, name);
                var live = Path.Combine(plan.DataDirectory, name);
                if (File.Exists(backup))
                {
                    CopyFile(backup, live);
                }
                else
                {
                    Retry(() => File.Delete(live));
                }
            }
        });

        Attempt($"start Hawker {plan.PreviousVersion} again", () =>
            processes.Start(Path.Combine(plan.AppDirectory, plan.ExecutableName), plan.Arguments, plan.AppDirectory));
    }

    private UpdateResult Finish(bool succeeded, string? error)
    {
        var result = new UpdateResult(succeeded, plan.Version, plan.PreviousVersion, error, DateTimeOffset.UtcNow);
        if (!succeeded)
        {
            Log($"Update failed: {error}");
        }

        Attempt("record the result", () => UpdateFiles.Write(_files.ResultPath, result));
        return result;
    }

    private void Attempt(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log($"Couldn't {what}: {ex}");
        }
    }

    private static void CopyFile(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        Retry(() => File.Copy(from, to, overwrite: true));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(to, File.GetUnixFileMode(from));
        }
    }

    /// <summary>Antivirus scanners and a program that has only just exited can hold a file for a moment on Windows.</summary>
    private static void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20)
            {
                Thread.Sleep(500);
            }
        }
    }

    private void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(_files.Directory);
            File.AppendAllText(_files.LogPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging is a convenience; the update goes ahead without it.
        }
    }

    private sealed class SystemProcesses : IUpdateProcesses
    {
        public bool WaitForExit(int processId, TimeSpan timeout)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return process.WaitForExit(timeout);
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        public IStartedProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory)
        {
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = workingDirectory };
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            return new Started(Process.Start(info) ?? throw new InvalidOperationException($"{executable} didn't start."));
        }

        private sealed class Started(Process process) : IStartedProcess
        {
            public int Id => process.Id;
            public bool HasExited => process.HasExited;
            public void Kill() => process.Kill(entireProcessTree: true);
        }
    }
}
