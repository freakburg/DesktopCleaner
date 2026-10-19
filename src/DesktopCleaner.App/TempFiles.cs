using System.IO;
using System.Windows;
using DesktopCleaner.Core.Cleanup;
using DesktopCleaner.Core.Deletion;

namespace DesktopCleaner.App;

public sealed class TempVm
{
    public required TempEntry Entry { get; init; }
    public bool Remove { get; set; }
    public string Name => Entry.EmptyFolder ? Entry.Name + "  (empty folder)" : Entry.IsFolder ? Entry.Name + "  (folder)" : Entry.Name;
    public string SizeText => Entry.EmptyFolder ? "empty" : Format.Bytes(Entry.Bytes);
    public string FilesText => Entry.EmptyFolder ? "-" : Entry.FileCount.ToString("N0");
    /// <summary>What this row counts as when deleting: each file, or the one empty folder.</summary>
    public int ItemCount => Entry.EmptyFolder ? 1 : Entry.FileCount;
    public string ChangedText => Entry.NewestUtc.ToLocalTime().ToString("MMM d, yyyy");
}

public partial class MainWindow
{
    private List<TempVm> _temp = [];
    private int _tempDays = TempDefaultDays;
    public const int TempDefaultDays = 7;

    private int SelectedTempDays() => TmAge.SelectedIndex switch { 0 => 1, 1 => 3, 3 => 30, 4 => 0, _ => TempDefaultDays };

    private async void TmScan_Click(object s, RoutedEventArgs e) => await TmScanAsync();

    private async Task TmScanAsync()
    {
        TmScanBtn.IsEnabled = TmClearBtn.IsEnabled = TmAllBtn.IsEnabled = TmAge.IsEnabled = false;
        TmBar.IsIndeterminate = true; TmStatus.Text = "Looking through your temp folder...";
        TmFolder.Text = "Folder: " + TempCleaner.DefaultRoot;
        try
        {
            _tempDays = SelectedTempDays();
            var scan = await new TempCleaner().ScanAsync(_tempDays);
            _temp = scan.Entries.Select(x => new TempVm { Entry = x }).ToList();
            TmList.ItemsSource = _temp;
            TmTotal.Text = _temp.Count == 0 ? "" : $"{Format.Bytes(scan.TotalBytes)} in {scan.FileCount:N0} files" + (scan.EmptyFolders > 0 ? $" + {scan.EmptyFolders:N0} empty folders" : "");
            TmNote.Visibility = scan.KeptNewer > 0 ? Visibility.Visible : Visibility.Collapsed;
            TmNote.Text = $"{scan.KeptNewer:N0} files newer than this are being left alone because a program may still be using them. Choose a shorter time, or Everything, to include them.";
            TmStatus.Text = _temp.Count == 0 ? (scan.KeptNewer > 0 ? "Nothing old enough to clear." : "Nothing to clear. Your temp folder is already tidy.") : "Select what to delete, then press Delete selected items.";
            TmClearBtn.IsEnabled = TmAllBtn.IsEnabled = _temp.Count > 0;
        }
        finally { TmScanBtn.IsEnabled = TmAge.IsEnabled = true; TmBar.IsIndeterminate = false; }
    }

    private void TmAll_Click(object s, RoutedEventArgs e)
    {
        foreach (var t in _temp) t.Remove = true;
        TmList.ItemsSource = null; TmList.ItemsSource = _temp;
    }

    private async void TmClear_Click(object s, RoutedEventArgs e)
    {
        var chosen = _temp.Where(t => t.Remove).ToList();
        if (chosen.Count == 0) { TmStatus.Text = "Select at least one item first."; return; }
        if (!ConfirmDialog.Ask(this, chosen.Count == 1 ? "Delete this temp item?" : $"Delete {chosen.Count} temp items?", chosen.Sum(c => c.ItemCount), chosen.Sum(c => c.Entry.Bytes),
                chosen.Select(c => c.Entry.Name).ToList(), ConfirmKind.Permanent, "Delete for good")) return;

        TmScanBtn.IsEnabled = TmClearBtn.IsEnabled = TmAllBtn.IsEnabled = false;
        var bar = new DeleteBar(TmBar, TmStatus, "Deleting", chosen.Sum(c => c.ItemCount));
        string message;
        try
        {
            var result = await new TempCleaner(new ActivityLog()).ClearAsync(chosen.Select(c => c.Entry), _tempDays, default, bar.Batch());
            message = $"Freed {Format.Bytes(result.FreedBytes)}." +
                      (result.LeftAlone > 0 ? $" {result.LeftAlone:N0} files were in use or had changed, so they were left alone. Close other programs and try again." : "");
        }
        finally { bar.Close(); TmScanBtn.IsEnabled = true; }
        await TmScanAsync();      // refresh the list...
        TmStatus.Text = message;  // ...but keep the result message visible
    }
}










