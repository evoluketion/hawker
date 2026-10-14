namespace Hawker.Web.Updates;

/// <summary>Opens the update dialog in this browser tab only: one per connection, so other open tabs aren't affected.</summary>
public sealed class UpdateDialog
{
    public event Action? OpenRequested;

    public void Open() => OpenRequested?.Invoke();
}
