using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DesktopCleaner.App;

/// <summary>Shows another program's window. A link such as com.epicgames.launcher:// is delivered to a launcher that is already running, but a launcher sitting minimized or in the tray never comes forward by itself.</summary>
public static class WindowTools
{
    private const int SwRestore = 9;
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);

    /// <summary>Restores and focuses the first visible window of the named process. Waits up to <paramref name="waitMs"/> for it to appear. Returns false if there is none.</summary>
    public static async Task<bool> BringToFrontAsync(string processName, int waitMs = 4000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(waitMs);
        do
        {
            foreach (var p in Process.GetProcessesByName(processName))
            {
                try
                {
                    p.Refresh();
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    if (IsIconic(p.MainWindowHandle)) ShowWindow(p.MainWindowHandle, SwRestore);
                    SetForegroundWindow(p.MainWindowHandle);
                    return true;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                finally { p.Dispose(); }
            }
            await Task.Delay(250);
        } while (DateTime.UtcNow < until);
        return false;
    }
}
