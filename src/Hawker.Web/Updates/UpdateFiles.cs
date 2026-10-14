using System.Text.Json;

namespace Hawker.Web.Updates;

/// <summary>What the update applier is told to do.</summary>
/// <param name="ExecutableName">The program's file name, inside both the app and staged folders.</param>
/// <param name="Arguments">What the program is started with afterwards, the same way it was started before.</param>
public sealed record UpdatePlan(
    string AppDirectory,
    string StagedDirectory,
    string DataDirectory,
    string ExecutableName,
    int WaitForProcessId,
    string Version,
    string PreviousVersion,
    IReadOnlyList<string> Arguments);

/// <summary>How the last update went, left for whichever version starts afterwards to report.</summary>
public sealed record UpdateResult(bool Succeeded, string Version, string PreviousVersion, string? Error, DateTimeOffset At);

/// <summary>Written by Hawker once it is up, so the applier knows the new version started.</summary>
public sealed record RunningMarker(int ProcessId, string Version);

/// <summary>The updates folder inside the data folder, and the files the app and applier share there.</summary>
public sealed class UpdateFiles(string dataDirectory)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Directory { get; } = Path.Combine(dataDirectory, "updates");
    public string DownloadDirectory => Path.Combine(Directory, "download");
    public string StagingDirectory => Path.Combine(Directory, "staging");
    public string BackupDirectory => Path.Combine(Directory, "backup");
    public string ApplierDirectory => Path.Combine(Directory, "applier");
    public string ApplierStartedPath => Path.Combine(Directory, "applier-started.json");
    public string PlanPath => Path.Combine(Directory, "plan.json");
    public string ResultPath => Path.Combine(Directory, "last-result.json");
    public string RunningPath => Path.Combine(Directory, "running.json");
    public string LogPath => Path.Combine(Directory, "update.log");

    public static T? Read<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes through a temporary file, so a reader never sees half of it.</summary>
    public static void Write<T>(string path, T value)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
        File.Move(temporary, path, overwrite: true);
    }
}
