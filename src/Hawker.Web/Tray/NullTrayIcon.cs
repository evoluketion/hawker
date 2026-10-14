namespace Hawker.Web.Tray;

public sealed class NullTrayIcon : ITrayIcon
{
    public bool Show(string tooltip, IReadOnlyList<TrayMenuItem> menu, Action onActivate) => false;

    public void SetMenu(IReadOnlyList<TrayMenuItem> menu)
    {
    }

    public bool Confirm(string title, string message, string confirmLabel) => true;

    public void Dispose()
    {
    }
}
