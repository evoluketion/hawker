using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static Hawker.Web.Tray.Windows.NativeMethods;

namespace Hawker.Web.Tray.Windows;

/// <summary>
/// The notification-area icon, driven by Shell_NotifyIcon from a dedicated STA thread that owns a hidden window and
/// its message loop. Menu and click handlers run on the thread pool, so a handler that stops the app never blocks or
/// joins the tray thread.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsTrayIcon(ILogger<WindowsTrayIcon> logger) : ITrayIcon
{
    private const string ClassName = "HawkerTrayIcon";
    private const uint CallbackMessage = WM_APP + 1;
    private const uint IconId = 1;

    /// <summary>The window procedure is a static callback; Hawker shows one tray icon per process.</summary>
    private static WindowsTrayIcon? s_current;

    private readonly ILogger<WindowsTrayIcon> _logger = logger;
    private readonly Lock _gate = new();
    private Thread? _thread;
    private volatile nint _hwnd;
    private nint _icon;
    private uint _taskbarCreated;
    private string _tooltip = "";
    private IReadOnlyList<TrayMenuItem> _menu = [];
    private Action _onActivate = () => { };
    private bool _disposed;

    public bool Show(string tooltip, IReadOnlyList<TrayMenuItem> menu, Action onActivate)
    {
        var shown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is not null)
            {
                throw new InvalidOperationException("The tray icon is already shown.");
            }

            _tooltip = tooltip;
            _menu = menu;
            _onActivate = onActivate;
            _thread = new Thread(() => Run(shown)) { IsBackground = true, Name = "Hawker tray" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        return shown.Task.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            thread = _thread;
        }

        if (thread is null)
        {
            return;
        }

        if (_hwnd != 0)
        {
            PostMessage(_hwnd, WM_CLOSE, 0, 0);
        }

        if (thread != Thread.CurrentThread)
        {
            thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    private void Run(TaskCompletionSource<bool> shown)
    {
        var instance = GetModuleHandle(null);
        var className = Marshal.StringToHGlobalUni(ClassName);
        s_current = this;
        try
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = instance,
                lpszClassName = className,
            };
            if (RegisterClassEx(windowClass) == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            // A hidden top-level window rather than a message-only one: only top-level windows receive the
            // TaskbarCreated broadcast that says Explorer restarted and the icon must be added again.
            _hwnd = CreateWindowEx(0, ClassName, "Hawker", 0, 0, 0, 0, 0, 0, 0, instance, 0);
            if (_hwnd == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            ChangeWindowMessageFilterEx(_hwnd, _taskbarCreated, MSGFLT_ALLOW, 0);
            _icon = CreateIcon(GetSystemMetrics(SM_CXSMICON));

            // When Explorer isn't running yet the add fails; TaskbarCreated adds the icon once it is.
            shown.TrySetResult(AddIcon());

            while (GetMessage(out var msg, 0, 0, 0) > 0)
            {
                TranslateMessage(msg);
                DispatchMessage(msg);
            }
        }
        catch (Exception ex)
        {
            if (!shown.TrySetException(ex))
            {
                _logger.LogError(ex, "The tray icon stopped");
            }
        }
        finally
        {
            if (_hwnd != 0)
            {
                DestroyWindow(_hwnd);
                _hwnd = 0;
            }

            if (_icon != 0)
            {
                DestroyIcon(_icon);
                _icon = 0;
            }

            UnregisterClass(ClassName, instance);
            Marshal.FreeHGlobal(className);
            s_current = null;
            shown.TrySetResult(false);
        }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        var tray = s_current;
        try
        {
            if (tray?.Handle(hwnd, msg, wParam, lParam) is { } result)
            {
                return result;
            }
        }
        catch (Exception ex)
        {
            // An exception must not cross back into native code: it would end the process.
            tray?._logger.LogError(ex, "The tray icon failed to handle a message");
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private nint? Handle(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == CallbackMessage)
        {
            // Version 4 puts the event in lParam's low word and the click's screen position in wParam.
            switch ((uint)(lParam & 0xFFFF))
            {
                case NIN_SELECT:
                    Dispatch(_onActivate);
                    break;
                case WM_CONTEXTMENU:
                    ShowMenu(hwnd, (short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF));
                    break;
            }

            return 0;
        }

        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            AddIcon();
            return 0;
        }

        switch (msg)
        {
            case WM_CLOSE:
                RemoveIcon();
                DestroyWindow(hwnd);
                _hwnd = 0;
                return 0;
            case WM_DESTROY:
                PostQuitMessage(0);
                return 0;
            default:
                return null;
        }
    }

    private void ShowMenu(nint hwnd, int x, int y)
    {
        var menu = CreatePopupMenu();
        try
        {
            for (var i = 0; i < _menu.Count; i++)
            {
                var item = _menu[i];
                if (item.IsSeparator)
                {
                    AppendMenu(menu, MF_SEPARATOR, 0, null);
                }
                else
                {
                    var flags = item.Enabled && item.OnClick is not null ? MF_STRING : MF_STRING | MF_GRAYED;
                    AppendMenu(menu, flags, (nuint)(i + 1), item.Text);
                }
            }

            // The menu only closes on a click elsewhere if its window is in the foreground, and the WM_NULL after it
            // closes lets a second right-click open it again straight away.
            SetForegroundWindow(hwnd);
            var command = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY, x, y, hwnd, 0);
            PostMessage(hwnd, WM_NULL, 0, 0);

            if (command > 0 && _menu[command - 1].OnClick is { } onClick)
            {
                Dispatch(onClick);
            }
        }
        finally
        {
            DestroyMenu(menu);
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

    private static nint CreateIcon(int size)
    {
        var pixels = TrayIconImage.RenderBgra(size);
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER),
            biWidth = size,
            biHeight = -size, // negative: rows run top to bottom, as rendered
            biPlanes = 1,
            biBitCount = 32,
        };

        // The colour bitmap's alpha does the masking; the 1-bit mask is all zeros, which Windows ignores here.
        var maskBits = new byte[(size + 15) / 16 * 2 * size];
        nint color = 0, mask = 0;
        try
        {
            color = CreateDIBSection(0, header, 0, out var bits, 0, 0);
            fixed (byte* m = maskBits)
            {
                mask = CreateBitmap(size, size, 1, 1, (nint)m);
            }

            if (color == 0 || mask == 0)
            {
                throw new InvalidOperationException("Could not create the tray icon bitmaps.");
            }

            Marshal.Copy(pixels, 0, bits, pixels.Length);
            var icon = CreateIconIndirect(new ICONINFO { fIcon = 1, hbmMask = mask, hbmColor = color });
            return icon != 0 ? icon : throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        finally
        {
            // CreateIconIndirect copies both bitmaps.
            if (color != 0)
            {
                DeleteObject(color);
            }

            if (mask != 0)
            {
                DeleteObject(mask);
            }
        }
    }

    private bool AddIcon()
    {
        var data = NewIconData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _icon;
        var tip = _tooltip.AsSpan(0, Math.Min(_tooltip.Length, 127));
        tip.CopyTo(new Span<char>(data.szTip, 128));

        if (!Shell_NotifyIcon(NIM_ADD, data))
        {
            return false;
        }

        // Version 4 reports clicks as NIN_SELECT and WM_CONTEXTMENU, with the anchor point in wParam.
        data.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, data);
        return true;
    }

    private void RemoveIcon()
    {
        var data = NewIconData();
        Shell_NotifyIcon(NIM_DELETE, data);
    }

    private NOTIFYICONDATAW NewIconData() => new()
    {
        cbSize = (uint)sizeof(NOTIFYICONDATAW),
        hWnd = _hwnd,
        uID = IconId,
    };
}
