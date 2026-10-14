namespace Hawker.Web.Tray;

public sealed class NullTrayIcon : ITrayIcon
{
    public bool Show(string tooltip, IReadOnlyList<TrayMenuItem> menu, Action onActivate) => false;

    public void Dispose()
    {
    }
}
