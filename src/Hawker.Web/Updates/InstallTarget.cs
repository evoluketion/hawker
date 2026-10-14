using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace Hawker.Web.Updates;

/// <summary>The installed copy of Hawker an update replaces.</summary>
/// <param name="PackageName">The release download for this platform.</param>
public sealed record InstallTarget(string AppDirectory, string ExecutableName, string PackageName)
{
    /// <summary>
    /// Finds the running copy, or says why it can't update itself: only an unzipped release package that started
    /// itself (not a service, and not <c>dotnet run</c>) and can write to its own folder can.
    /// </summary>
    public static (InstallTarget? Target, string? Reason) Detect(IHostEnvironment environment) => Detect(
        environment.IsDevelopment(),
        AppContext.BaseDirectory,
        Environment.ProcessPath,
        OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService(),
        IsSystemdService());

    public static (InstallTarget? Target, string? Reason) Detect(bool development, string appDirectory, string? processPath, bool windowsService, bool systemdService)
    {
        if (PackageForThisPlatform() is not { } package)
        {
            return (null, "Updates install themselves only on 64-bit Windows and Linux. Download the new version from the release page.");
        }

        var executable = OperatingSystem.IsWindows() ? "Hawker.Web.exe" : "Hawker.Web";
        if (development || !Directory.Exists(Path.Combine(appDirectory, "wwwroot"))
            || !string.Equals(Path.GetFileName(processPath), executable, StringComparison.OrdinalIgnoreCase))
        {
            return (null, "Hawker is running from its source code. Update it with git pull.");
        }

        if (windowsService)
        {
            return (null, "Hawker runs as a Windows service. Stop the service, unzip the new version over this folder, then start it again.");
        }

        if (systemdService)
        {
            return (null, "Hawker runs as a systemd service. Stop the service, unpack the new version over this folder, then start it again.");
        }

        if (!CanWrite(appDirectory))
        {
            return (null, $"Hawker can't write to its folder ({appDirectory}). Download the new version and unpack it over this one.");
        }

        return (new InstallTarget(Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDirectory)), executable, package), null);
    }

    /// <summary>
    /// systemd adopts any orphaned process, such as one started by <c>start-hawker.sh --headless</c>, so a systemd
    /// parent alone doesn't make a service. A service's control group is the service unit itself; a program started
    /// from a desktop or terminal lives in a scope.
    /// </summary>
    public static bool IsSystemdService() => SystemdHelpers.IsSystemdService() && IsServiceControlGroup(ReadControlGroup());

    public static bool IsServiceControlGroup(string? controlGroup) =>
        controlGroup?.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Split(':', 3) is [_, _, var path]
        && path.TrimEnd('/').EndsWith(".service", StringComparison.Ordinal);

    private static string? ReadControlGroup()
    {
        try
        {
            return OperatingSystem.IsLinux() ? File.ReadAllText("/proc/self/cgroup") : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string? PackageForThisPlatform() => RuntimeInformation.OSArchitecture != Architecture.X64 ? null
        : OperatingSystem.IsWindows() ? "hawker-win-x64.zip"
        : OperatingSystem.IsLinux() ? "hawker-linux-x64.tar.gz"
        : null;

    private static bool CanWrite(string directory)
    {
        var probe = Path.Combine(directory, $".hawker-write-test-{Environment.ProcessId}");
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
