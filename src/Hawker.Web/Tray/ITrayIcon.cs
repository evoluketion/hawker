namespace Hawker.Web.Tray;

/// <param name="OnClick">Runs on a thread-pool thread, so it may block or stop the app.</param>
public sealed record TrayMenuItem(string Text, Action? OnClick, bool Enabled = true)
{
    public static readonly TrayMenuItem Separator = new("", null, Enabled: false);

    public bool IsSeparator => Text.Length == 0;
}

/// <summary>A notification-area icon with a context menu. Dispose removes it; disposing twice is safe.</summary>
public interface ITrayIcon : IDisposable
{
    /// <param name="onActivate">The icon's primary click. Runs on a thread-pool thread.</param>
    /// <returns>False when the desktop has no tray to show it in.</returns>
    bool Show(string tooltip, IReadOnlyList<TrayMenuItem> menu, Action onActivate);

    /// <summary>Replaces the menu of an icon that is already shown.</summary>
    void SetMenu(IReadOnlyList<TrayMenuItem> menu);

    /// <summary>Asks a yes/no question. Called from a menu item's handler, and blocks until answered.</summary>
    /// <param name="confirmLabel">
    /// Where no dialog can be shown, the clicked menu item briefly takes this label and a second click confirms.
    /// </param>
    bool Confirm(string title, string message, string confirmLabel);
}
