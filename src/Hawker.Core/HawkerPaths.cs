namespace Hawker.Core;

/// <summary>Where Hawker keeps its database, logs and archived PDFs.</summary>
public class HawkerPaths
{
    public HawkerPaths(string dataDirectory)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ArchiveDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    public string DataDirectory { get; }
    public string DatabasePath => Path.Combine(DataDirectory, "hawker.db");
    public string ArchiveDirectory => Path.Combine(DataDirectory, "archive");
    public string LogDirectory => Path.Combine(DataDirectory, "logs");
    public string KeysDirectory => Path.Combine(DataDirectory, "keys");
}
