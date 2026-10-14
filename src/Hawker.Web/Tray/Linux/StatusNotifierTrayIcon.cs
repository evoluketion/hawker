using System.ComponentModel;
using System.Diagnostics;
using Tmds.DBus.Protocol;

namespace Hawker.Web.Tray.Linux;

/// <summary>
/// The tray icon on Linux desktops, as a StatusNotifierItem on the session bus. KDE Plasma, Xfce, Cinnamon and most
/// others host these; stock GNOME only does with the AppIndicator extension.
/// </summary>
public sealed class StatusNotifierTrayIcon : ITrayIcon
{
    public const string DefaultWatcherName = "org.kde.StatusNotifierWatcher";
    private const string ItemPath = "/StatusNotifierItem";
    private const string ItemInterface = "org.kde.StatusNotifierItem";
    private static readonly int[] IconSizes = [22, 32, 48];
    private static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(10);

    private readonly ILogger<StatusNotifierTrayIcon> _logger;
    private readonly string? _busAddress;
    private readonly string _watcherName;
    private readonly CancellationTokenSource _stopping = new();
    private DBusConnection? _connection;
    private NameOwnerWatcher? _watcher;
    private string _serviceName = "";
    private bool _disposed;
    private readonly Lock _confirmGate = new();
    private IReadOnlyList<TrayMenuItem>? _itemsBeforeConfirm;
    private int _confirmGeneration;

    public StatusNotifierTrayIcon(ILogger<StatusNotifierTrayIcon> logger)
        : this(logger, DBusAddress.Session, DefaultWatcherName)
    {
    }

    /// <param name="busAddress">The session bus; null when there is none.</param>
    /// <param name="watcherName">The bus name of the tray host's watcher.</param>
    public StatusNotifierTrayIcon(ILogger<StatusNotifierTrayIcon> logger, string? busAddress, string watcherName)
    {
        _logger = logger;
        _busAddress = busAddress;
        _watcherName = watcherName;
    }

    public DbusMenu? Menu { get; private set; }

    /// <summary>Runs a dialog tool and returns its exit code, or null when the tool isn't installed.</summary>
    public Func<string, IReadOnlyList<string>, int?> RunDialog { get; init; } = RunDialogProcess;

    public bool Show(string tooltip, IReadOnlyList<TrayMenuItem> menu, Action onActivate) =>
        ShowAsync(tooltip, menu, onActivate).GetAwaiter().GetResult();

    private async Task<bool> ShowAsync(string tooltip, IReadOnlyList<TrayMenuItem> menu, Action onActivate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Menu = new DbusMenu(menu, Dispatch);
        if (string.IsNullOrEmpty(_busAddress))
        {
            _logger.LogInformation("There is no D-Bus session bus, so no tray icon can be shown");
            return false;
        }

        _connection = new DBusConnection(_busAddress);
        await _connection.ConnectAsync();

        _connection.AddMethodHandlers([new Item(this, tooltip, onActivate), Menu]);

        // The spec's naming scheme, so hosts that expect a well-known name find one.
        _serviceName = $"org.kde.StatusNotifierItem-{Environment.ProcessId}-1";
        await _connection.RequestNameAsync(_serviceName);

        _watcher = await _connection.WatchNameOwnerAsync(_watcherName);
        var owner = _watcher.GetCurrentOwner();
        var registered = owner is not null && await TryRegisterAsync();
        if (owner is null)
        {
            _logger.LogInformation(
                "No StatusNotifierWatcher ({Watcher}) is running, so the desktop has no tray to show Hawker's icon in. On GNOME, install the AppIndicator extension",
                _watcherName);
        }

        _ = KeepRegisteredAsync(owner, _stopping.Token);
        return registered;
    }

    /// <summary>A restarted tray host forgets its items, so the icon registers again with each new watcher.</summary>
    private async Task KeepRegisteredAsync(string? owner, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                if (owner is not null)
                {
                    using var changed = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken, _watcher!.GetOwnerChangedCancellationToken(owner));
                    await Task.Delay(Timeout.Infinite, changed.Token).ContinueWith(_ => { }, TaskScheduler.Default);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                owner = await _watcher!.WaitForOwnerAsync(cancellationToken);
                await TryRegisterAsync();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or DBusConnectionClosedException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The tray icon stopped following the tray host");
        }
    }

    private async Task<bool> TryRegisterAsync()
    {
        try
        {
            var connection = _connection!;
            var writer = connection.GetMessageWriter();
            MessageBuffer message;
            try
            {
                writer.WriteMethodCallHeader(_watcherName, "/StatusNotifierWatcher", _watcherName, "RegisterStatusNotifierItem", "s");
                writer.WriteString(_serviceName);
                message = writer.CreateMessage();
            }
            finally
            {
                writer.Dispose();
            }

            await connection.CallMethodAsync(message);
            return true;
        }
        catch (Exception ex) when (ex is DBusErrorReplyException or DBusConnectionException)
        {
            _logger.LogWarning(ex, "The tray host refused Hawker's icon");
            return false;
        }
    }

    /// <summary>
    /// Asks with zenity or kdialog. Without either, the clicked item is relabelled for a few seconds and clicking it
    /// again confirms.
    /// </summary>
    public bool Confirm(string title, string message, string confirmLabel)
    {
        lock (_confirmGate)
        {
            if (_itemsBeforeConfirm is not null)
            {
                RestoreMenu();
                return true;
            }
        }

        (string Tool, string[] Args)[] dialogs =
        [
            ("zenity", ["--question", "--title", title, "--text", message]),
            ("kdialog", ["--title", title, "--warningyesno", message]),
        ];
        foreach (var (tool, args) in dialogs)
        {
            // Both answer 0 for yes and 1 for no; any other code means the dialog couldn't be shown.
            switch (RunDialog(tool, args))
            {
                case 0:
                    return true;
                case 1:
                    return false;
            }
        }

        AskForSecondClick(confirmLabel);
        return false;
    }

    private void AskForSecondClick(string confirmLabel)
    {
        if (Menu is not { LastClicked: { } id } menu)
        {
            return;
        }

        int generation;
        lock (_confirmGate)
        {
            _itemsBeforeConfirm = menu.Items;
            generation = ++_confirmGeneration;
            menu.Replace(menu.Items.Select((item, i) => i == id - 1 ? item with { Text = confirmLabel } : item).ToList(), _connection);
        }

        _ = Task.Delay(ConfirmWindow).ContinueWith(_ =>
        {
            lock (_confirmGate)
            {
                if (_confirmGeneration == generation && _itemsBeforeConfirm is not null)
                {
                    RestoreMenu();
                }
            }
        }, TaskScheduler.Default);
    }

    private void RestoreMenu()
    {
        Menu!.Replace(_itemsBeforeConfirm!, _connection);
        _itemsBeforeConfirm = null;
    }

    private static int? RunDialogProcess(string tool, IReadOnlyList<string> args)
    {
        var startInfo = new ProcessStartInfo(tool) { UseShellExecute = false };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private void Dispatch(Action action) => ThreadPool.QueueUserWorkItem(_ =>
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A tray menu action failed");
        }
    });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping.Cancel();
        // Closing the connection drops the bus name, which removes the icon from the tray.
        _watcher?.Dispose();
        _connection?.Dispose();
        _stopping.Dispose();
    }

    /// <summary>The <c>org.kde.StatusNotifierItem</c> object: the icon itself, its tooltip and its menu path.</summary>
    private sealed class Item(StatusNotifierTrayIcon tray, string tooltip, Action onActivate) : IPathMethodHandler
    {
        private static readonly string[] PropertyNames =
        [
            "Category", "Id", "Title", "Status", "WindowId", "IconName", "IconPixmap", "OverlayIconName",
            "AttentionIconName", "AttentionIconPixmap", "ToolTip", "ItemIsMenu", "Menu", "IconThemePath",
        ];

        private readonly (int Size, byte[] Argb)[] _pixmaps = IconSizes.Select(s => (s, TrayIconImage.RenderArgb(s))).ToArray();

        public string Path => ItemPath;

        public bool HandlesChildPaths => false;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            var request = context.Request;
            if (context.IsPropertiesInterfaceRequest)
            {
                var reader = request.GetBodyReader();
                var iface = reader.ReadString();
                if (iface != ItemInterface)
                {
                    context.ReplyError("org.freedesktop.DBus.Error.UnknownInterface", $"No interface {iface}.");
                }
                else if (request.MemberAsString == "GetAll")
                {
                    DbusMenu.Reply(context, "a{sv}", (ref MessageWriter w) =>
                    {
                        var dict = w.WriteDictionaryStart();
                        foreach (var name in PropertyNames)
                        {
                            w.WriteDictionaryEntryStart();
                            w.WriteString(name);
                            WriteProperty(ref w, name);
                        }

                        w.WriteDictionaryEnd(dict);
                    });
                }
                else if (request.MemberAsString == "Get" && reader.ReadString() is var name && PropertyNames.Contains(name))
                {
                    DbusMenu.Reply(context, "v", (ref MessageWriter w) => WriteProperty(ref w, name));
                }
                else
                {
                    context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", "Properties are read-only.");
                }

                return ValueTask.CompletedTask;
            }

            if (request.InterfaceAsString != ItemInterface)
            {
                context.ReplyUnknownMethodError();
                return ValueTask.CompletedTask;
            }

            switch (request.MemberAsString)
            {
                case "Activate":
                    tray.Dispatch(onActivate);
                    break;
                case "ContextMenu" or "SecondaryActivate" or "Scroll" or "ProvideXdgActivationToken":
                    // The host shows the menu from the Menu property; the rest have nothing to do.
                    break;
                default:
                    context.ReplyUnknownMethodError();
                    return ValueTask.CompletedTask;
            }

            DbusMenu.Reply(context, "", (ref MessageWriter _) => { });
            return ValueTask.CompletedTask;
        }

        private void WriteProperty(ref MessageWriter writer, string name)
        {
            switch (name)
            {
                case "Category":
                    writer.WriteVariantString("ApplicationStatus");
                    break;
                case "Id":
                    writer.WriteVariantString("hawker");
                    break;
                case "Title":
                    writer.WriteVariantString(tooltip);
                    break;
                case "Status":
                    writer.WriteVariantString("Active");
                    break;
                case "WindowId":
                    writer.WriteVariantInt32(0);
                    break;
                case "IconPixmap":
                    writer.WriteSignature("a(iiay)");
                    WritePixmaps(ref writer, _pixmaps);
                    break;
                case "AttentionIconPixmap":
                    writer.WriteSignature("a(iiay)");
                    WritePixmaps(ref writer, []);
                    break;
                case "ToolTip":
                    writer.WriteSignature("(sa(iiay)ss)");
                    writer.WriteStructureStart();
                    writer.WriteString("");
                    WritePixmaps(ref writer, []);
                    writer.WriteString(tooltip);
                    writer.WriteString("");
                    break;
                case "ItemIsMenu":
                    writer.WriteVariantBool(false);
                    break;
                case "Menu":
                    writer.WriteVariantObjectPath(DbusMenu.ObjectPath);
                    break;
                default:
                    // IconName, OverlayIconName, AttentionIconName, IconThemePath: unused, so empty.
                    writer.WriteVariantString("");
                    break;
            }
        }

        private static void WritePixmaps(ref MessageWriter writer, (int Size, byte[] Argb)[] pixmaps)
        {
            var array = writer.WriteArrayStart(DBusType.Struct);
            foreach (var (size, argb) in pixmaps)
            {
                writer.WriteStructureStart();
                writer.WriteInt32(size);
                writer.WriteInt32(size);
                writer.WriteArray(argb);
            }

            writer.WriteArrayEnd(array);
        }
    }
}
