using Hawker.Web;

namespace Hawker.Tests;

public sealed class SingleInstanceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hawker-lock-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_second_copy_cannot_take_the_lock_until_the_first_releases_it()
    {
        using (var first = SingleInstance.TryAcquire(_dir))
        {
            Assert.NotNull(first);
            Assert.Null(SingleInstance.TryAcquire(_dir));
        }

        using var again = SingleInstance.TryAcquire(_dir);
        Assert.NotNull(again);
    }

    [Fact]
    public void Different_data_folders_do_not_block_each_other()
    {
        var other = Directory.CreateTempSubdirectory("hawker-lock-").FullName;
        try
        {
            using var a = SingleInstance.TryAcquire(_dir);
            using var b = SingleInstance.TryAcquire(other);
            Assert.NotNull(a);
            Assert.NotNull(b);
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }
}
