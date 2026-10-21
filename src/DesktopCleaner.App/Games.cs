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

    private void GmUninstall_Click(object s, RoutedEventArgs e)
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
                Process.Start(new ProcessStartInfo("com.epicgames.launcher://apps") { UseShellExecute = true });
                GmStatus.Text = $"The Epic Games Launcher is opening. Find {g.Name} in your Library, click the three dots and choose Uninstall.";
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            GmStatus.Text = "Could not open the launcher: " + ex.Message;
        }
    }
}

