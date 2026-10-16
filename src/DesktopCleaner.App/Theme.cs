using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;

namespace DesktopCleaner.App;

/// <summary>Follows the Windows light/dark setting: switches WPF's Fluent theme and our own few custom brushes, live.</summary>
public static class Theme
{
    public static bool WindowsPrefersLight()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is not int v || v != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return true; }
    }

    /// <param name="forceLight">null follows Windows; true/false is for testing with --theme light|dark.</param>
    public static void Apply(Application app, bool? forceLight = null)
    {
        bool light = forceLight ?? WindowsPrefersLight();
        app.ThemeMode = light ? ThemeMode.Light : ThemeMode.Dark;
        Set(app, "MutedBrush", light ? "#555555" : "#B0B0B0");
        Set(app, "CardBrush", light ? "#FAFAFA" : "#2A2A2A");
        Set(app, "CardBorderBrush", light ? "#DDDDDD" : "#454545");
        Set(app, "TrackBrush", light ? "#EEEEEE" : "#3A3A3A");
    }

    public static void Watch(Application app) =>
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General) app.Dispatcher.BeginInvoke(() => Apply(app));
        };

    private static void Set(Application app, string key, string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        app.Resources[key] = b;
    }
}

