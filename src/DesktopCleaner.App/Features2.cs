using System.Diagnostics;
using System.IO;
using System.Windows;
using DesktopCleaner.Core.Apps;
using DesktopCleaner.Core.Cloud;
using DesktopCleaner.Core.Deletion;
using DesktopCleaner.Core.Settings;
using DesktopCleaner.Core.Shell;

namespace DesktopCleaner.App;

public sealed class AppVm
{
    public required InstalledApp App { get; init; }
    public string Name => App.Name;
    public string Publisher => App.Publisher ?? "";
    public string SizeText => App.SizeBytes > 0 ? Format.Bytes(App.SizeBytes) : "unknown";
    public string InstalledText => App.InstalledOn is { } d
        ? $"{d:MMM yyyy}" + (App.YearsSinceInstall(DateTime.Now) is { } y and >= 1 ? $" ({y} yr ago)" : "")
        : "";
}

public sealed class OneDriveVm
{
    public required LocalCloudFile File { get; init; }
    public bool Remove { get; set; }
    public string Name => File.Name;
    public string Folder => File.Folder;
    public string SizeText => Format.Bytes(File.Size);
}

public partial class MainWindow
{
    private bool _settingsLoading;

    private void InitSettings()
    {
        _settingsLoading = true;
        try
        {
            var s = AppSettings.Load();
            SetLowDisk.IsChecked = s.LowDiskAlert; SetLowPct.Text = s.LowDiskPercent.ToString(); SetLowGb.Text = s.LowDiskGigabytes.ToString();
            SetExplorer.IsChecked = new ExplorerMenu().IsEnabled;
        }
        finally { _settingsLoading = false; }
    }

    private void SetExplorer_Changed(object s, RoutedEventArgs e)
    {
        if (_settingsLoading) return;
        try
        {
            var menu = new ExplorerMenu();
            if (SetExplorer.IsChecked == true) menu.Enable(Environment.ProcessPath!); else menu.Disable();
            SetStatus.Text = SetExplorer.IsChecked == true ? "Added. Right-click any folder to try it." : "Removed from the right-click menu.";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            SetStatus.Text = "Windows would not let us change the menu: " + ex.Message;
        }
    }

    private void SetLowDisk_Changed(object s, RoutedEventArgs e)
    {
        if (_settingsLoading) return;
        var cur = AppSettings.Load();
        new AppSettings
        {
            LowDiskAlert = SetLowDisk.IsChecked == true,
            LowDiskPercent = int.TryParse(SetLowPct.Text, out var p) && p is > 0 and < 100 ? p : cur.LowDiskPercent,
            LowDiskGigabytes = int.TryParse(SetLowGb.Text, out var g) && g is > 0 and < 100000 ? g : cur.LowDiskGigabytes,
        }.Save();
        SetStatus.Text = "Saved.";
    }

    // ---------- Installed apps ----------
    private async void AppsScan_Click(object s, RoutedEventArgs e)
    {
        AppsScanBtn.IsEnabled = false; AppsStatus.Text = "Reading your installed programs...";
        try
        {
            var list = await Task.Run(() => InstalledApps.List());
            AppsList.ItemsSource = list.Select(a => new AppVm { App = a }).ToList();
            AppsStatus.Text = $"{list.Count} programs. Measuring the ones Windows has no size for...";
            var filled = await InstalledApps.FillMissingSizesAsync(list);
            AppsList.ItemsSource = filled.Select(a => new AppVm { App = a }).ToList();
            AppsStatus.Text = $"{filled.Count} programs using about {Format.Bytes(filled.Sum(a => a.SizeBytes))} in total.";
        }
        finally { AppsScanBtn.IsEnabled = true; }
    }

    private void AppsOpen_Click(object s, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });

    // ---------- OneDrive ----------
    private List<OneDriveVm> _od = [];

    private async void OdScan_Click(object s, RoutedEventArgs e)
    {
        if (OneDriveSpace.Roots().Count == 0) { OdStatus.Text = "OneDrive does not seem to be set up on this PC."; return; }
        OdScanBtn.IsEnabled = OdFreeBtn.IsEnabled = false; OdBar.IsIndeterminate = true; OdStatus.Text = "Looking through OneDrive...";
        try
        {
            var found = await OneDriveSpace.FindFreeableAsync();
            _od = found.Select(f => new OneDriveVm { File = f }).ToList();
            OdList.ItemsSource = _od;
            OdTotal.Text = _od.Count == 0 ? "" : $"{Format.Bytes(_od.Sum(f => f.File.Size))} could be freed";
            OdStatus.Text = _od.Count == 0 ? "Nothing to free up. Your OneDrive files are already online-only or pinned." : "Tick the files to set to online-only. They stay in OneDrive.";
            OdFreeBtn.IsEnabled = _od.Count > 0;
        }
        finally { OdScanBtn.IsEnabled = true; OdBar.IsIndeterminate = false; }
    }

    private async void OdFree_Click(object s, RoutedEventArgs e)
    {
        var chosen = _od.Where(f => f.Remove).ToList();
        if (chosen.Count == 0) { OdStatus.Text = "Tick at least one file first."; return; }
        if (MessageBox.Show($"Set {chosen.Count} file(s) ({Format.Bytes(chosen.Sum(c => c.File.Size))}) to online-only?\n\nThey stay safe in OneDrive and download again when you open them. You need an internet connection to open them later.",
                "Free up space", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        OdFreeBtn.IsEnabled = OdScanBtn.IsEnabled = false; OdBar.IsIndeterminate = true;
        var log = new ActivityLog();
        var res = await Task.Run(() => OneDriveSpace.FreeUp(chosen.Select(c => c.File), log));
        OdBar.IsIndeterminate = false; OdScanBtn.IsEnabled = true;
        var done = res.Where(r => r.Succeeded).Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _od = _od.Where(f => !done.Contains(f.File.Path)).ToList();
        OdList.ItemsSource = _od; OdFreeBtn.IsEnabled = _od.Count > 0;
        OdStatus.Text = $"Set {done.Count} file(s) to online-only ({Format.Bytes(res.Sum(r => r.Bytes))}). OneDrive will remove the local copies shortly." +
                        (res.Count - done.Count > 0 ? $" {res.Count - done.Count} were left alone." : "");
    }
}
