using System.ComponentModel;
using System.Diagnostics;
using Hawker.Web.Tray.Windows;

namespace Hawker.Web.Hosting;

/// <summary>
/// Reports why Hawker stopped before it started. The message always goes to the console; in headless mode, where
/// nobody sees the console, it is also shown on the desktop.
/// </summary>
public sealed class StartupNotice(bool headless, TextWriter console, Action<string, string> showOnDesktop)
{
    public static StartupNotice For(IConfiguration configuration) =>
        new(Headless.IsOn(configuration), Console.Error, ShowOnDesktop);

    public void Show(string title, string message)
    {
        console.WriteLine(message);
        if (headless)
        {
            showOnDesktop(title, message);
        }
    }

    private static void ShowOnDesktop(string title, string message)
    {
        if (OperatingSystem.IsWindows())
        {
            NativeMethods.MessageBox(0, message, title,
                NativeMethods.MB_ICONWARNING | NativeMethods.MB_TOPMOST | NativeMethods.MB_SETFOREGROUND);
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo("notify-send") { ArgumentList = { "--app-name=Hawker", title, message } });
            process?.WaitForExit(TimeSpan.FromSeconds(5));
        }
        catch (Win32Exception)
        {
            // notify-send isn't installed; the console line is all there is.
        }
    }
}
