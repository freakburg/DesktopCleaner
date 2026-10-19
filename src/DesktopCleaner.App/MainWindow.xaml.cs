using System.IO;
using System.Threading;
using System.Windows;
using DesktopCleaner.Core.Deletion;
using DesktopCleaner.Core.Downloads;
using DesktopCleaner.Core.Duplicates;
using Microsoft.Win32;

namespace DesktopCleaner.App;

public sealed class FileVm
{
    public required string Path { get; init; }
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? Path;
    public string Modified { get; init; } = "";
    public bool Remove { get; set; }
}

public sealed class GroupVm
{
    public required DuplicateGroup Group { get; init; }
    public required List<FileVm> Files { get; init; }
    public string Title => $"{System.IO.Path.GetFileName(Group.Files[0].Path)}  and {Group.Files.Count - 1} more identical {(Group.Files.Count == 2 ? "copy" : "copies")}";
    public string Summary => $"{Format.Bytes(Group.FileSize)} each. Removing the extra copies frees {Format.Bytes(Group.WastedBytes)}.";
}

public sealed class DownloadVm
{
    public required DownloadItem Item { get; init; }
    public bool Remove { get; set; }
    public string Path => Item.Path;
    public string Name => System.IO.Path.GetFileName(Item.Path);
    public string Folder
    {
        get
        {
            var dir = System.IO.Path.GetDirectoryName(Item.Path) ?? "";
            var rel = System.IO.Path.GetRelativePath(DownloadsOptions.DefaultFolder, dir);
            return rel == "." ? "(Downloads)" : rel;
        }
    }
    public string SizeText => Format.Bytes(Item.Size);
    public string Why => string.Join(", ", new[]
    {
        Item.Reasons.HasFlag(ReviewReason.OldInstaller) ? "Old setup file" : null,
        Item.Reasons.HasFlag(ReviewReason.OldArchive) ? "Old ZIP/archive" : null,
        Item.Reasons.HasFlag(ReviewReason.Large) ? "Very large" : null,
    }.Where(r => r is not null));
    public string AgeText => Item.AgeDays(DateTime.UtcNow) switch
    {
        < 1 => "today", 1 => "yesterday", < 60 => $"{Item.AgeDays(DateTime.UtcNow)} days ago",
        < 730 => $"{Item.AgeDays(DateTime.UtcNow) / 30} months ago", _ => $"{Item.AgeDays(DateTime.UtcNow) / 365} years ago",
    };
}

static class Format
{
    public static string Bytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{b / 1024.0:F0} KB",
        _ => $"{b} B",
    };
}

public partial class MainWindow : Window
{
    private CancellationTokenSource? _cts;
    private List<GroupVm> _groups = [];

    public MainWindow() { InitializeComponent(); InitOverview(); InitSchedule(); InitFeatures(); InitSettings(); }

    private void Browse_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog() == true) FolderBox.Text = dlg.FolderName;
    }

    private void Cancel_Click(object s, RoutedEventArgs e) => _cts?.Cancel();

    private async void Scan_Click(object s, RoutedEventArgs e)
    {
        if (!Directory.Exists(FolderBox.Text)) { StatusText.Text = "Choose a folder first."; return; }
        _cts = new CancellationTokenSource();
        SetBusy(true);
        Tree.ItemsSource = null;
        var progress = new Progress<ScanProgress>(p =>
        {
            Bar.IsIndeterminate = p.Phase == ScanPhase.Enumerating;
            if (p.FilesToHash > 0) Bar.Value = (double)p.FilesHashed / p.FilesToHash;
            StatusText.Text = p.Phase == ScanPhase.Enumerating
                ? $"Looking through your files... {p.FilesSeen:N0} so far" : $"Comparing files... {p.FilesHashed:N0} of {p.FilesToHash:N0}";
        });
        try
        {
            var r = await new DuplicateScanner().ScanAsync(FolderBox.Text, progress: progress, ct: _cts.Token);
            _groups = r.Groups.Select(g => new GroupVm
            {
                Group = g,
                // Pre-check every copy except the first; user reviews before anything is removed.
                Files = g.Files.Select((f, i) => new FileVm { Path = f.Path, Modified = f.LastWriteUtc.ToLocalTime().ToString("g"), Remove = i > 0 }).ToList(),
            }).ToList();
            Tree.ItemsSource = _groups;
            StatusText.Text = _groups.Count == 0 ? "No duplicates found. Nice and tidy." : $"Found {_groups.Count} sets of duplicates. You can free {Format.Bytes(r.WastedBytes)}."+(r.Issues.Count > 0 ? $" ({r.Issues.Count} files could not be read and were left alone.)" : "");
        }
        catch (OperationCanceledException) { StatusText.Text = "Stopped. Nothing was changed."; }
        finally { SetBusy(false); Bar.IsIndeterminate = false; Bar.Value = 0; DeleteBtn.IsEnabled = _groups.Count > 0; }
    }

    private async void Delete_Click(object s, RoutedEventArgs e)
    {
        var picks = _groups.Select(g => (g, paths: g.Files.Where(f => f.Remove).Select(f => f.Path).ToList())).Where(x => x.paths.Count > 0).ToList();
        int n = picks.Sum(x => x.paths.Count);
        if (n == 0) return;
        long dupBytes = picks.Sum(x => x.g.Group.FileSize * x.paths.Count);
        if (!ConfirmDialog.Ask(this, n == 1 ? "Delete this duplicate?" : $"Delete {n} duplicate files?", n, dupBytes, picks.SelectMany(x => x.paths).ToList(), ConfirmKind.Recycle, "Delete copies")) return;

        SetBusy(true);
        var remover = new DuplicateRemover(new ActivityLog());
        var bar = new DeleteBar(Bar, StatusText, "Deleting", n);
        long freed = 0; int skipped = 0;
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (g, paths) in picks)
        {
            var progress = bar.Batch();
            // run on a worker thread so moving files to the Recycle Bin never freezes the window
            foreach (var o in await Task.Run(() => remover.RemoveAsync(g.Group, paths, default, progress)))
            {
                if (o.Status == DeleteStatus.Recycled) { freed += o.Bytes; done.Add(o.Path); } else skipped++;
            }
            bar.BatchDone(paths.Count);
        }
        bar.Close();
        foreach (var g in _groups) g.Files.RemoveAll(f => done.Contains(f.Path));
        _groups = _groups.Where(g => g.Files.Count > 1).ToList();
        Tree.ItemsSource = null; Tree.ItemsSource = _groups;
        StatusText.Text = $"Freed {Format.Bytes(freed)}." + (skipped > 0 ? $" {skipped} left alone because they changed, were in use, or are protected." : "");
        SetBusy(false);
        DeleteBtn.IsEnabled = _groups.Count > 0;
    }

    private List<DownloadVm> _downloads = [];

    private async void DlScan_Click(object s, RoutedEventArgs e)
    {
        if (!int.TryParse(DlDays.Text, out var days) || !long.TryParse(DlLarge.Text, out var mb)) { DlStatus.Text = "Please type whole numbers for days and MB."; return; }
        DlScanBtn.IsEnabled = false; DlDeleteBtn.IsEnabled = false;
        DlFolder.Text = DownloadsOptions.DefaultFolder;
        var items = await new DownloadsReviewer().ScanAsync(null, new DownloadsOptions { OldAfterDays = days, LargeThresholdBytes = mb * 1024 * 1024 });
        _downloads = items.Select(i => new DownloadVm { Item = i }).ToList();
        DlList.ItemsSource = _downloads;
        DlStatus.Text = items.Count == 0 ? "Nothing worth a look. Your Downloads are tidy." : $"{items.Count} files worth a look, {Format.Bytes(items.Sum(i => i.Size))} in total. Select the ones you want to remove.";
        DlScanBtn.IsEnabled = true; DlDeleteBtn.IsEnabled = items.Count > 0;
    }

    private async void DlDelete_Click(object s, RoutedEventArgs e)
    {
        var chosen = _downloads.Where(d => d.Remove).ToList();
        if (chosen.Count == 0) { DlStatus.Text = "Select at least one file first."; return; }
        if (!ConfirmDialog.Ask(this, chosen.Count == 1 ? "Delete this file?" : $"Delete {chosen.Count} files?", chosen.Count, chosen.Sum(c => c.Item.Size), chosen.Select(c => c.Path).ToList(), ConfirmKind.Recycle, "Delete files")) return;
        DlScanBtn.IsEnabled = DlDeleteBtn.IsEnabled = false;
        var bar = new DeleteBar(DlBar, DlStatus, "Deleting", chosen.Count);
        var items = chosen.Select(c => c.Item).ToList();
        var progress = bar.Batch();
        var res = await Task.Run(() => new DownloadsReviewer(new ActivityLog()).Remove(items, progress));
        bar.Close(); DlScanBtn.IsEnabled = true;
        var gone = res.Where(r => r.Status == DeleteStatus.Recycled).Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _downloads = _downloads.Where(d => !gone.Contains(d.Path)).ToList();
        DlList.ItemsSource = null; DlList.ItemsSource = _downloads;
        DlDeleteBtn.IsEnabled = _downloads.Count > 0;
        DlStatus.Text = $"Freed {Format.Bytes(res.Sum(r => r.Bytes))}." + (res.Any(r => r.Status != DeleteStatus.Recycled) ? $" {res.Count(r => r.Status != DeleteStatus.Recycled)} left alone because they changed or are protected." : "");
    }

    private void SetBusy(bool busy)
    {
        ScanBtn.IsEnabled = BrowseBtn.IsEnabled = !busy;
        CancelBtn.IsEnabled = busy && _cts is not null;
        DeleteBtn.IsEnabled = false;
    }
}















