using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Hawker.Web.Hosting;

/// <summary>
/// The published Windows exe is a GUI-subsystem program, so it never opens a console by itself. Outside headless mode
/// it borrows the console of whatever started it (Start Hawker.cmd), or opens its own when double-clicked.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class ConsoleWindow
{
    private const uint AttachParentProcess = unchecked((uint)-1);

    /// <summary>Must run before anything touches <see cref="Console"/>, which binds its streams on first use.</summary>
    public static void Ensure()
    {
        if (GetConsoleWindow() != 0)
        {
            return;
        }

        if (!AttachConsole(AttachParentProcess))
        {
            AllocConsole();
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetConsoleWindow();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllocConsole();
}
