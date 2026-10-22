using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DesktopCleaner.Core.Games;

namespace DesktopCleaner.App;

public sealed class GameVm
{
    public required GameInfo Game { get; init; }
    public string Name => Game.Name;
    public string LauncherText => Game.Launcher.ToString();
    public string SizeText => Game.SizeBytes > 0 ? Format.Bytes(Game.SizeBytes) : "unknown";
    public string InstallPath => Game.InstallPath;
    public string PlayedText
    {
        get
        {
            if (Game.Launcher == GameLauncher.Epic) return "Not recorded by Epic";
            if (Game.LastPlayedUtc is null) return "Never played";
            var d = Game.DaysSincePlayed(DateTime.UtcNow) ?? 0;
            return d < 1 ? "Today" : d < 2 ? "Yesterday" : d < 60 ? $"{d} days ago" : d < 730 ? $"{d / 30} months ago" : $"{d / 365} years ago";
        }
    }
}

public partial class MainWindow
{
    private List<GameVm> _gamesAll = [];
    private bool _gmReady;

    private async void GmScan_Click(object s, RoutedEventArgs e)
    {
        GmScanBtn.IsEnabled = false; GmStatus.Text = "Looking at your game launchers...";
        try
        {
            var games = await Task.Run(() => new GameFinder().Find());
            _gamesAll = games.Select(g => new GameVm { Game = g }).ToList();
            _gmReady = true;
            GmFilters.IsEnabled = _gamesAll.Count > 0;
            ApplyGameFilter();
            GmStatus.Text = _gamesAll.Count == 0 ? "No Steam or Epic Games installs were found on this PC." : "Select a game to uninstall it or open its folder.";
        }
        finally { GmScanBtn.IsEnabled = true; }
    }

    /// <summary>Where the Epic launcher is installed, taken from the way Windows registers its web link.</summary>
    private static string? EpicLauncherExe()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"com.epicgames.launcher\shell\open\command");
            var cmd = k?.GetValue("") as string;
            if (cmd is { Length: > 0 })
            {
                var path = cmd.Trim().StartsWith('"') ? cmd.Trim()[1..cmd.Trim().IndexOf('"', 1)] : cmd.Trim().Split(' ')[0];
                if (File.Exists(path)) return path;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        var fallback = @"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe";
        return File.Exists(fallback) ? fallback : null;
    }

    private void GmFilter_Changed(object s, RoutedEventArgs e) { if (_gmReady) ApplyGameFilter(); }

    private void ApplyGameFilter()
    {
        int minDays = GmAge.SelectedIndex switch { 1 => 30, 2 => 90, 3 => 180, 4 => 365, _ => 0 };
        bool tools = GmTools.IsChecked == true;
        var now = DateTime.UtcNow;
        IEnumerable<GameVm> q = _gamesAll.Where(g => (tools || !g.Game.IsTool) &&
            (minDays == 0 || (g.Game.Launcher == GameLauncher.Steam && (g.Game.DaysSincePlayed(now) ?? int.MaxValue) >= minDays)));
        q = GmSort.SelectedIndex switch
        {
            1 => q.OrderByDescending(g => g.Game.DaysSincePlayed(now) ?? (g.Game.Launcher == GameLauncher.Steam ? int.MaxValue : -1)).ThenByDescending(g => g.Game.SizeBytes),
            2 => q.OrderBy(g => g.Game.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => q.OrderByDescending(g => g.Game.SizeBytes),
        };
        var shown = q.ToList();
        GmList.ItemsSource = shown;
        var real = _gamesAll.Where(g => !g.Game.IsTool).ToList();
        long stale = real.Where(g => g.Game.Launcher == GameLauncher.Steam && (g.Game.DaysSincePlayed(now) ?? int.MaxValue) >= 180).Sum(g => g.Game.SizeBytes);
        GmSummary.Text = real.Count == 0 ? "" : $"{real.Count} games, {Format.Bytes(real.Sum(g => g.Game.SizeBytes))}" + (stale > 0 ? $"  -  {Format.Bytes(stale)} not played in 6 months" : "");
    }

    private void GmList_Changed(object s, SelectionChangedEventArgs e) =>
        GmUninstallBtn.IsEnabled = GmShowBtn.IsEnabled = GmList.SelectedItem is GameVm;

    private void GmShow_Click(object s, RoutedEventArgs e)
    {
        if (GmList.SelectedItem is not GameVm g) return;
        if (Directory.Exists(g.InstallPath)) Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { g.InstallPath }, UseShellExecute = true });
        else GmStatus.Text = "That folder is no longer there.";
    }

    private async void GmUninstall_Click(object s, RoutedEventArgs e)
    {
        if (GmList.SelectedItem is not GameVm g) return;
        try
        {
            if (g.Game.Launcher == GameLauncher.Steam && g.Game.AppId is { Length: > 0 } id)
            {
                // Steam shows its own "Uninstall?" confirmation; nothing happens unless you confirm there.
                Process.Start(new ProcessStartInfo($"steam://uninstall/{id}") { UseShellExecute = true });
                GmStatus.Text = $"Steam is asking you to confirm uninstalling {g.Name}. Press Find my games again afterwards.";
            }
            else
            {
                // Epic has no "uninstall this game" link, so open the launcher on its library page and bring its window forward
                // (a launcher that is already running minimized or in the tray would otherwise stay hidden).
                Process.Start(new ProcessStartInfo("com.epicgames.launcher://apps") { UseShellExecute = true });
                bool shown = await WindowTools.BringToFrontAsync("EpicGamesLauncher");
                if (!shown && EpicLauncherExe() is { } exe)
                {
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });   // starting it again makes a hidden instance show its window
                    shown = await WindowTools.BringToFrontAsync("EpicGamesLauncher", 8000);
                }
                GmStatus.Text = shown
                    ? $"The Epic Games Launcher is open. Find {g.Name} in your Library, click the three dots and choose Uninstall."
                    : "Could not bring the Epic Games Launcher to the front. Open it from the Start menu, then find " + g.Name + " in your Library and choose Uninstall.";
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            GmStatus.Text = "Could not open the launcher: " + ex.Message;
        }
    }
}




