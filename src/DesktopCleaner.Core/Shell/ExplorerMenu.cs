using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DesktopCleaner.Core.Shell;

/// <summary>Adds "Check with Desktop Cleaner" to the folder right-click menu. Per-user (HKCU), so no admin rights are needed.</summary>
[SupportedOSPlatform("windows")]
public sealed class ExplorerMenu(string classesKey = @"Software\Classes")
{
    private const string Name = "DesktopCleaner";
    public const string Label = "Check with Desktop Cleaner";

    public bool IsEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey($@"{classesKey}\Directory\shell\{Name}");
            return k is not null;
        }
    }

    public void Enable(string exePath)
    {
        Write($@"{classesKey}\Directory\shell\{Name}", exePath, "%1");           // right-click a folder
        Write($@"{classesKey}\Directory\Background\shell\{Name}", exePath, "%V"); // right-click empty space inside a folder
    }

    public void Disable()
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"{classesKey}\Directory\shell\{Name}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree($@"{classesKey}\Directory\Background\shell\{Name}", throwOnMissingSubKey: false);
    }

    private static void Write(string keyPath, string exePath, string placeholder)
    {
        using var k = Registry.CurrentUser.CreateSubKey(keyPath);
        k.SetValue("", Label);
        k.SetValue("Icon", $"\"{exePath}\"");
        using var cmd = k.CreateSubKey("command");
        cmd.SetValue("", $"\"{exePath}\" --folder \"{placeholder}\"");
    }
}
