using System.IO;
using System.Windows;
using DesktopCleaner.Core.Deletion;
using DesktopCleaner.Core.Projects;
using DesktopCleaner.Core.Videos;
using Microsoft.Win32;

namespace DesktopCleaner.App;

public sealed class ClutterVm
{
    public required ClutterItem Item { get; init; }
    public bool Remove { get; set; }
    public string ProjectName => Item.ProjectName;
    public string KindText => Item.KindText;
    public string Rebuild => Item.Rebuild;
    public string FolderPath => Item.FolderPath;
    public string SizeText => Format.Bytes(Item.Bytes);
    public string LastUsedText
    {
        get
        {
            var d = (int)(DateTime.UtcNow - Item.ProjectLastActivityUtc).TotalDays;
            return d < 60 ? $"{d} days ago" : d < 730 ? $"{d / 30} months ago" : $"{d / 365} years ago";
        }
    }
}

public partial class MainWindow
{
    private List<ClutterVm> _clutter = [];
    private CancellationTokenSource? _pjCts;
    private string _pjRoot = VideoLocations.UserFolder;
    private int _pjDays = 90;

    private int SelectedProjectDays() => PjAge.SelectedIndex switch { 0 => 30, 2 => 180, 3 => 365, _ => 90 };

    private void PjBrowse_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog() == true) { _pjRoot = dlg.FolderName; PjFolder.Text = _pjRoot; }
    }

    private void PjStop_Click(object s, RoutedEventArgs e) => _pjCts?.Cancel();

    private async void PjScan_Click(object s, RoutedEventArgs e)
    {
        if (!PjScanBtn.IsEnabled) return;
        PjFolder.Text = _pjRoot; _pjDays = SelectedProjectDays();
        _pjCts = new CancellationTokenSource();
        PjScanBtn.IsEnabled = PjDeleteBtn.IsEnabled = PjAllBtn.IsEnabled = PjAge.IsEnabled = false; PjStopBtn.IsEnabled = true;
        PjBar.IsIndeterminate = true; PjList.ItemsSource = null; PjTotal.Text = "";
        var progress = new Progress<ClutterProgress>(p => PjStatus.Text = $"Looking... {p.FoldersChecked:N0} folders checked, {p.ItemsFound:N0} project folders found.");
        IReadOnlyList<ClutterItem>? found = null;
        try { found = await new ProjectClutterFinder().ScanAsync([_pjRoot], _pjDays, null, progress, _pjCts.Token); }
        catch (OperationCanceledException) { PjStatus.Text = "Stopped. Nothing was changed."; }
        finally { PjBar.IsIndeterminate = false; PjScanBtn.IsEnabled = PjAge.IsEnabled = true; PjStopBtn.IsEnabled = false; }
        if (found is null) return;
        _clutter = found.Select(i => new ClutterVm { Item = i }).ToList();
        PjList.ItemsSource = _clutter;
        PjTotal.Text = _clutter.Count == 0 ? "" : $"{Format.Bytes(found.Sum(i => i.Bytes))} in {found.Count} folders";
        PjStatus.Text = _clutter.Count == 0 ? "Nothing to clean up. Every project here was used recently, or has no big build folders." : "Select the ones to delete. Hover a row to see its full path.";
        PjDeleteBtn.IsEnabled = PjAllBtn.IsEnabled = _clutter.Count > 0;
    }

    private void PjAll_Click(object s, RoutedEventArgs e)
    {
        foreach (var c in _clutter) c.Remove = true;
        PjList.ItemsSource = null; PjList.ItemsSource = _clutter;
    }

    private async void PjDelete_Click(object s, RoutedEventArgs e)
    {
        var chosen = _clutter.Where(c => c.Remove).ToList();
        if (chosen.Count == 0) { PjStatus.Text = "Select at least one folder first."; return; }
        if (!ConfirmDialog.Ask(this, chosen.Count == 1 ? "Delete this build folder?" : $"Delete {chosen.Count} build folders?", chosen.Count, chosen.Sum(c => c.Item.Bytes),
                chosen.Select(c => c.Item.FolderPath).ToList(), ConfirmKind.Permanent, "Delete for good")) return;
        PjScanBtn.IsEnabled = PjDeleteBtn.IsEnabled = PjAllBtn.IsEnabled = false;
        var bar = new DeleteBar(PjBar, PjStatus, "Deleting", chosen.Count);
        var items = chosen.Select(c => c.Item).ToList();
        var progress = bar.Batch();
        var days = _pjDays;
        var res = await Task.Run(() => new ProjectClutterRemover(new ActivityLog()).Remove(items, days, progress));
        bar.Close(); PjScanBtn.IsEnabled = true;
        var gone = res.Where(r => r.Succeeded).Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _clutter = _clutter.Where(c => !gone.Contains(c.Item.FolderPath)).ToList();
        PjList.ItemsSource = _clutter;
        PjTotal.Text = _clutter.Count == 0 ? "" : $"{Format.Bytes(_clutter.Sum(c => c.Item.Bytes))} in {_clutter.Count} folders";
        PjDeleteBtn.IsEnabled = PjAllBtn.IsEnabled = _clutter.Count > 0;
        PjStatus.Text = $"Freed {Format.Bytes(res.Sum(r => r.Bytes))}." + (res.Any(r => !r.Succeeded) ? $" {res.Count(r => !r.Succeeded)} left alone (in use, or the project was used since the scan)." : "");
    }
}
