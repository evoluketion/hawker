using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Hawker.Core;
using Hawker.Web.Editions;
using Hawker.Web.Hosting;

namespace Hawker.Web.Updates;

public enum UpdateStatus
{
    Idle,
    Checking,
    Downloading,
    Restarting,
    Failed,
}

/// <summary>The program's own command line, as it was started, so an update can start it the same way.</summary>
public sealed record LaunchArguments(IReadOnlyList<string> Arguments);

/// <summary>
/// Looks for a newer release on GitHub and, only when asked, downloads it and hands over to the applier (see
/// <see cref="UpdateApplier"/>), then stops so the files can be replaced.
/// </summary>
public sealed class UpdateService(
    IHttpClientFactory httpFactory,
    HawkerPaths paths,
    IConfiguration configuration,
    IHostEnvironment environment,
    IHostApplicationLifetime lifetime,
    EditionRunQueue runs,
    LaunchArguments launch,
    ILogger<UpdateService> logger)
{
    public const string HttpClientName = "updates";
    public const string DefaultReleaseUrl = "https://api.github.com/repos/evoluketion/hawker/releases/latest";

    private readonly UpdateFiles _files = new(paths.DataDirectory);
    private readonly SemaphoreSlim _checking = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Lazy<(InstallTarget? Target, string? Reason)> _target = new(() => InstallTarget.Detect(environment));
    private int _installing;
    private Version? _prompted;

    /// <summary>Raised on a thread-pool thread whenever anything below changes.</summary>
    public event Action? Changed;

    public Version CurrentVersion => AppVersion.Current;
    public ReleaseInfo? Latest { get; private set; }
    public bool IsUpdateAvailable => Latest is { } latest && latest.Version > CurrentVersion;
    public DateTimeOffset? LastChecked { get; private set; }
    public string? CheckError { get; private set; }
    public UpdateStatus Status { get; private set; }
    public string? StatusMessage { get; private set; }

    /// <summary>How far the download has got, from 0 to 1, when its size is known.</summary>
    public double? Progress { get; private set; }

    /// <summary>The last update's outcome, when it happened just before this start.</summary>
    public UpdateResult? LastResult { get; private set; }

    public InstallTarget? Target => _target.Value.Target;

    /// <summary>Why the update has to be installed by hand, when it does.</summary>
    public string? CannotInstallReason => _target.Value.Reason;

    public bool CanInstall => IsUpdateAvailable && Target is not null && Status is not (UpdateStatus.Downloading or UpdateStatus.Restarting);

    public bool CheckAutomatically => configuration.GetValue("Hawker:Updates:Check", true);

    /// <summary>Raised to show the update dialog, for example from the settings page.</summary>
    public event Action? OpenRequested;

    public void RequestOpen() => OpenRequested?.Invoke();

    /// <summary>
    /// True once for each new version found, so it is offered as soon as someone opens the dashboard. Never while
    /// developing, where the version number is rarely the released one, and not for a version that just failed to
    /// install, which only the badge offers again.
    /// </summary>
    public bool TakePrompt()
    {
        lock (_gate)
        {
            if (!IsUpdateAvailable || Latest!.Version == _prompted || environment.IsDevelopment()
                || LastResult is { Succeeded: false } failed && AppVersion.Parse(failed.Version) == Latest.Version)
            {
                return false;
            }

            _prompted = Latest.Version;
            return true;
        }
    }

    /// <summary>Tells the applier this version is up, and picks up how the update that started it went.</summary>
    public void ReportStarted()
    {
        try
        {
            UpdateFiles.Write(_files.RunningPath, new RunningMarker(Environment.ProcessId, AppVersion.Display(CurrentVersion)));
            if (UpdateFiles.Read<UpdateResult>(_files.ResultPath) is { } result)
            {
                File.Delete(_files.ResultPath);
                var current = AppVersion.Display(CurrentVersion);
                if (DateTimeOffset.UtcNow - result.At < TimeSpan.FromDays(1) && (result.Succeeded ? result.Version : result.PreviousVersion) == current)
                {
                    LastResult = result;
                    logger.Log(result.Succeeded ? LogLevel.Information : LogLevel.Warning,
                        "Update to {Version}: {Outcome}", result.Version, result.Succeeded ? "installed" : result.Error);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not write {Path}; an update in progress may roll back", _files.RunningPath);
        }

        // Leftovers of the previous update; the applier that ran from them has finished by the time anyone checks again.
        TryDelete(_files.DownloadDirectory);
    }

    /// <summary>
    /// The unpacked download, the applier's copy and the backup of the replaced files stay until the applier has
    /// finished; once this version is running, they are no longer needed.
    /// </summary>
    public void RemoveLeftovers()
    {
        TryDelete(_files.StagingDirectory);
        TryDelete(_files.ApplierDirectory);
        TryDelete(_files.BackupDirectory);
    }

    public void DismissLastResult()
    {
        LastResult = null;
        Raise();
    }

    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!await _checking.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            if (Status is UpdateStatus.Downloading or UpdateStatus.Restarting)
            {
                return;
            }

            SetStatus(UpdateStatus.Checking, null);
            var http = httpFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, configuration["Hawker:Updates:ReleaseUrl"] ?? DefaultReleaseUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await http.SendAsync(request, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);
            var release = ReleaseInfo.Parse(json.RootElement);
            if (release is not null)
            {
                if (release.Version > CurrentVersion && Latest?.Version != release.Version)
                {
                    logger.LogInformation("Hawker {Version} is available (this is {Current})", release.Tag, AppVersion.Display(CurrentVersion));
                }

                Latest = release;
            }

            CheckError = null;
            LastChecked = DateTimeOffset.UtcNow;
            SetStatus(UpdateStatus.Idle, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Could not check for updates: {Error}", ex.Message);
            CheckError = ex is TaskCanceledException ? "GitHub didn't answer in time." : ex.Message;
            LastChecked = DateTimeOffset.UtcNow;
            SetStatus(UpdateStatus.Idle, null);
        }
        finally
        {
            _checking.Release();
        }
    }

    /// <summary>Downloads the latest release and restarts into it. Only ever called because someone asked.</summary>
    public async Task InstallAsync(CancellationToken cancellationToken = default)
    {
        if (Latest is not { } release || Target is not { } target || !IsUpdateAvailable || Interlocked.Exchange(ref _installing, 1) == 1)
        {
            return;
        }

        try
        {
            if (runs.IsRunning)
            {
                SetStatus(UpdateStatus.Failed, "A paper is being made. Install the update once it has finished.");
                return;
            }

            if (release.Asset(target.PackageName) is not { } asset)
            {
                SetStatus(UpdateStatus.Failed, $"Release {release.Tag} has no {target.PackageName} to install.");
                return;
            }

            SetStatus(UpdateStatus.Downloading, $"Downloading Hawker {release.Tag}…");
            var staged = await DownloadAndUnpackAsync(asset, target, cancellationToken);

            if (runs.IsRunning)
            {
                SetStatus(UpdateStatus.Failed, "A paper started while the update downloaded. Install it once the paper has finished.");
                return;
            }

            var plan = new UpdatePlan(target.AppDirectory, staged, paths.DataDirectory, target.ExecutableName, Environment.ProcessId,
                AppVersion.Display(release.Version), AppVersion.Display(CurrentVersion), RestartArguments(launch.Arguments, configuration));
            UpdateFiles.Write(_files.PlanPath, plan);
            File.Delete(_files.ApplierStartedPath);
            var applier = await Task.Run(() => CopyApplier(target), cancellationToken);
            StartApplier(Path.Combine(applier, target.ExecutableName), _files.PlanPath);

            // Hawker only stops once the applier is there to bring it back.
            if (!await WaitForApplierAsync(cancellationToken))
            {
                SetStatus(UpdateStatus.Failed, "The update installer didn't start, so nothing was changed. The details are in Hawker's log.");
                return;
            }

            logger.LogInformation("Restarting into Hawker {Version}; see {Log} for how the update goes", release.Tag, _files.LogPath);
            SetStatus(UpdateStatus.Restarting, $"Installing Hawker {release.Tag}. Hawker will restart, and this page will reload by itself.");

            // Long enough for open pages to show the message before the connection drops.
            await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
            lifetime.StopApplication();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not install Hawker {Version}", release.Tag);
            SetStatus(UpdateStatus.Failed, $"The update couldn't be installed: {ex.Message}");
        }
        finally
        {
            if (Status != UpdateStatus.Restarting)
            {
                Interlocked.Exchange(ref _installing, 0);
            }
        }
    }

    /// <summary>
    /// The same command line again, except that the dashboard isn't opened a second time (open pages reload), and on
    /// Linux a copy started from a terminal comes back headless, since there is no terminal for it any more.
    /// </summary>
    public static IReadOnlyList<string> RestartArguments(IReadOnlyList<string> original, IConfiguration configuration)
    {
        var arguments = original.ToList();
        if (OperatingSystem.IsLinux() && !Headless.IsOn(configuration))
        {
            arguments.Add(Headless.Switch);
        }

        arguments.Add("--Hawker:OpenDashboardOnStart=false");
        return arguments;
    }

    private async Task<string> DownloadAndUnpackAsync(ReleaseAsset asset, InstallTarget target, CancellationToken cancellationToken)
    {
        TryDelete(_files.DownloadDirectory);
        TryDelete(_files.StagingDirectory);
        Directory.CreateDirectory(_files.DownloadDirectory);
        var archive = Path.Combine(_files.DownloadDirectory, asset.Name);

        var http = httpFactory.CreateClient(HttpClientName);
        using (var response = await http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? (asset.Size > 0 ? asset.Size : null);
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var file = File.Create(archive);
            var buffer = new byte[81920];
            long read = 0;
            var lastReport = Stopwatch.StartNew();
            int n;
            while ((n = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, n), cancellationToken);
                read += n;
                if (total > 0 && lastReport.ElapsedMilliseconds > 250)
                {
                    Progress = Math.Min(1, (double)read / total.Value);
                    lastReport.Restart();
                    Raise();
                }
            }
        }

        Progress = null;
        if (asset.Sha256 is { } expected)
        {
            await using var file = File.OpenRead(archive);
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken));
            if (actual != expected)
            {
                throw new InvalidDataException("The download doesn't match the release's checksum.");
            }
        }

        SetStatus(UpdateStatus.Downloading, "Unpacking…");
        var unpacked = Path.Combine(_files.StagingDirectory, "unpacked");
        await Task.Run(() => Unpack(archive, unpacked), cancellationToken);
        return PackageRoot(unpacked, target.ExecutableName)
            ?? throw new InvalidDataException($"The download has no {target.ExecutableName} in it.");
    }

    /// <summary>Unpacks a release archive. Both formats refuse entries that would land outside the folder.</summary>
    public static void Unpack(string archive, string destination)
    {
        Directory.CreateDirectory(destination);
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archive, destination);
            return;
        }

        using var file = File.OpenRead(archive);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, destination, overwriteFiles: false);
    }

    /// <summary>The folder holding the program: the archive's top folder (hawker-0.3.0-win-x64), or the archive itself.</summary>
    public static string? PackageRoot(string unpacked, string executableName)
    {
        if (File.Exists(Path.Combine(unpacked, executableName)))
        {
            return unpacked;
        }

        return Directory.EnumerateDirectories(unpacked).FirstOrDefault(d => File.Exists(Path.Combine(d, executableName)));
    }

    /// <summary>
    /// Copies this version's program files (the top-level files: the runtime and Hawker itself, without the browser
    /// driver or web assets) to run the applier from.
    /// </summary>
    private string CopyApplier(InstallTarget target)
    {
        TryDelete(_files.ApplierDirectory);
        Directory.CreateDirectory(_files.ApplierDirectory);
        foreach (var file in Directory.EnumerateFiles(target.AppDirectory))
        {
            File.Copy(file, Path.Combine(_files.ApplierDirectory, Path.GetFileName(file)));
        }

        return _files.ApplierDirectory;
    }

    private async Task<bool> WaitForApplierAsync(CancellationToken cancellationToken)
    {
        for (var waited = TimeSpan.Zero; waited < TimeSpan.FromSeconds(30); waited += TimeSpan.FromMilliseconds(250))
        {
            if (UpdateFiles.Read<RunningMarker>(_files.ApplierStartedPath) is not null)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        logger.LogWarning("The update installer didn't report in; see {Log}", _files.LogPath);
        return false;
    }

    private static void StartApplier(string executable, string planPath)
    {
        ProcessStartInfo info;
        if (OperatingSystem.IsWindows())
        {
            info = new ProcessStartInfo(executable) { CreateNoWindow = true };
        }
        else
        {
            // Its own session, with no terminal, so closing the terminal Hawker ran in doesn't stop the update halfway.
            File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);
            info = new ProcessStartInfo("setsid") { ArgumentList = { "sh", "-c", "exec \"$0\" \"$@\" </dev/null >/dev/null 2>&1", executable } };
        }

        info.UseShellExecute = false;
        info.WorkingDirectory = Path.GetDirectoryName(executable)!;
        info.ArgumentList.Add(UpdateApplier.Switch);
        info.ArgumentList.Add(planPath);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("The update installer didn't start.");
    }

    private void SetStatus(UpdateStatus status, string? message)
    {
        Status = status;
        StatusMessage = message;
        Raise();
    }

    private void Raise()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "A listener failed to handle an update change");
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still in use; it is cleared before the next download.
        }
    }
}
