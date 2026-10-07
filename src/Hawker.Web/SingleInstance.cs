namespace Hawker.Web;

/// <summary>
/// An exclusive lock file in the data folder, held for the life of the process. A second copy of Hawker using the same
/// data must not start: its scheduler would run the same editions and could print duplicate papers.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly FileStream _lock;

    private SingleInstance(FileStream lockFile) => _lock = lockFile;

    /// <returns>The held lock, or null if another Hawker process already holds it.</returns>
    public static SingleInstance? TryAcquire(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "hawker.lock");
        try
        {
            // FileShare.None keeps other processes from opening it; the OS releases it if Hawker crashes.
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            return new SingleInstance(stream);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _lock.Dispose();
}
