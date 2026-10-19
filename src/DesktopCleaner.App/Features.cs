using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using DesktopCleaner.Core.Caches;
using DesktopCleaner.Core.Deletion;
using DesktopCleaner.Core.Photos;
using Microsoft.Win32;

namespace DesktopCleaner.App;

public sealed class CacheVm
{
    public required CacheScanResult Result { get; init; }
    public bool Remove { get; set; }
    public string Name => Result.Target.Name;
    public string SizeText => Format.Bytes(Result.Bytes);
    public string Description => Result.Target.Description;
}

public sealed class PhotoVm
{
    public required PhotoInfo Info { get; init; }
    public bool IsKeep { get; init; }
    public bool Remove { get; set; }
    public string Path => Info.Path;
    public string Name => Info.Name;
    public string Details => $"{Info.Width} x {Info.Height}  -  {Format.Bytes(Info.Size)}";
    public Visibility KeepVisibility => IsKeep ? Visibility.Visible : Visibility.Collapsed;
    public System.Windows.Media.Brush Border => IsKeep ? System.Windows.Media.Brushes.SeaGreen : System.Windows.Media.Brushes.Transparent;

    private BitmapImage? _thumb;
    public BitmapImage? Thumbnail => _thumb ??= Load();

    private BitmapImage? Load()
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(Info.Path);
            bmp.DecodePixelWidth = 200;            // small decode keeps memory low for big photos
            bmp.CacheOption = BitmapCacheOption.OnLoad; // load now and release the file so it can be removed
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or UriFormatException) { return null; }
    }
}

public sealed class PhotoGroupVm
{
    public required SimilarPhotoGroup Group { get; init; }
    public required List<PhotoVm> Photos { get; init; }
    public string Title => $"{Photos.Count} similar photos";
    public string Summary => $"Removing all but the best one frees {Format.Bytes(Group.ReclaimableBytes)}.";
}

public sealed class HistoryVm
{
    public required ActivityEntry Entry { get; init; }
    public string When => Entry.TimeUtc.ToLocalTime().ToString("MMM d, h:mm tt");
    public string Path => Entry.Path;
    public string SizeText => Entry.Bytes > 0 ? Format.Bytes(Entry.Bytes) : "";
    public string What => Entry.Status switch
    {
        DeleteStatus.Recycled => "In the Recycle Bin (can be put back)",
        DeleteStatus.Deleted => "Cleared for good",
        DeleteStatus.FreedFromPc => "Online-only now (still in OneDrive)",
        DeleteStatus.SkippedChanged => "Skipped: file had changed",
        DeleteStatus.SkippedMissing => "Skipped: already gone",
        DeleteStatus.SkippedProtected => "Skipped: protected folder",
        DeleteStatus.SkippedLastCopy => "Skipped: last copy kept",
        _ => "Could not remove",
    };
}

public partial class MainWindow
{
    private void InitFeatures()
    {
        PhFolder.Text = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    }

    private void Tabs_Changed(object s, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (e.Source != Tabs) return; // ignore selection changes bubbling up from lists inside tabs
        if (Tabs.SelectedItem == HistTab) RefreshHistory();
    }

    // ---------- App caches ----------
    private List<CacheVm> _caches = [];

    private async void CaScan_Click(object s, RoutedEventArgs e) => await CaScanAsync();

    private async Task CaScanAsync()
    {
        CaScanBtn.IsEnabled = CaClearBtn.IsEnabled = CaAllBtn.IsEnabled = false;
        CaBar.IsIndeterminate = true; CaStatus.Text = "Looking at your browsers and apps...";
        try
        {
            var found = await new CacheCleaner().ScanAsync();
            _caches = found.Where(f => f.Bytes > 0).Select(f => new CacheVm { Result = f }).ToList();
            CaList.ItemsSource = _caches;
            CaTotal.Text = _caches.Count == 0 ? "" : $"{Format.Bytes(_caches.Sum(c => c.Result.Bytes))} can be cleared";
            CaStatus.Text = _caches.Count == 0 ? "No caches worth clearing right now." : "Select the ones to clear, then press Clear selected caches.";
            CaClearBtn.IsEnabled = CaAllBtn.IsEnabled = _caches.Count > 0;
        }
        finally { CaScanBtn.IsEnabled = true; CaBar.IsIndeterminate = false; }
    }

    private void CaAll_Click(object s, RoutedEventArgs e)
    {
        foreach (var c in _caches) c.Remove = true;
        CaList.ItemsSource = null; CaList.ItemsSource = _caches;
    }

    private async void CaClear_Click(object s, RoutedEventArgs e)
    {
        var chosen = _caches.Where(c => c.Remove).ToList();
        if (chosen.Count == 0) { CaStatus.Text = "Select at least one first."; return; }
        if (!ConfirmDialog.Ask(this, chosen.Count == 1 ? "Clear this cache?" : $"Clear {chosen.Count} caches?", chosen.Count, chosen.Sum(c => c.Result.Bytes),
                chosen.Select(c => $"{c.Name}  ({Format.Bytes(c.Result.Bytes)})").ToList(), ConfirmKind.Permanent, "Clear for good")) return;
        CaClearBtn.IsEnabled = CaScanBtn.IsEnabled = CaAllBtn.IsEnabled = false;
        var cleaner = new CacheCleaner(new ActivityLog());
        var bar = new DeleteBar(CaBar, CaStatus, "Clearing", chosen.Sum(c => c.Result.Files));
        long freed = 0; int stuck = 0;
        foreach (var c in chosen)
        {
            var r = await cleaner.ClearAsync(c.Result.Target, default, bar.Batch());
            bar.BatchDone(c.Result.Files);
            freed += r.FreedBytes; stuck += r.Outcomes.Count(o => !o.Succeeded);
        }
        bar.Close(); CaScanBtn.IsEnabled = true;
        var message = $"Cleared {Format.Bytes(freed)}." + (stuck > 0 ? $" {stuck} files were still in use. Close those apps and try again." : "");
        await CaScanAsync();      // refresh the list...
        CaStatus.Text = message;  // ...but keep the result message visible
    }

    // ---------- Similar photos ----------
    private CancellationTokenSource? _phCts;
    private List<PhotoGroupVm> _photoGroups = [];

    private void PhBrowse_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog() == true) PhFolder.Text = dlg.FolderName;
    }

    private void PhStop_Click(object s, RoutedEventArgs e) => _phCts?.Cancel();

    private async void PhScan_Click(object s, RoutedEventArgs e)
    {
        if (!Directory.Exists(PhFolder.Text)) { PhStatus.Text = "Choose a folder of photos first."; return; }
        var distance = PhStrict.SelectedIndex switch { 0 => 2, 2 => 10, _ => 6 };
        _phCts = new CancellationTokenSource();
        PhScanBtn.IsEnabled = PhDeleteBtn.IsEnabled = PhSelectBtn.IsEnabled = false; PhStopBtn.IsEnabled = true;
        PhList.ItemsSource = null;
        var progress = new Progress<PhotoProgress>(p =>
        {
            PhBar.Value = p.Found == 0 ? 0 : (double)p.Hashed / p.Found;
            PhStatus.Text = $"Looking at photos... {p.Hashed:N0} of {p.Found:N0}";
        });
        try
        {
            var r = await new SimilarPhotoFinder().ScanAsync(PhFolder.Text, new PhotoScanOptions { MaxDistance = distance }, progress, _phCts.Token);
            _photoGroups = r.Groups.Select(g => new PhotoGroupVm
            {
                Group = g,
                Photos = g.Photos.OrderByDescending(p => p.Pixels).ThenByDescending(p => p.Size)
                    .Select(p => new PhotoVm { Info = p, IsKeep = p == g.SuggestedKeep }).ToList(),
            }).ToList();
            PhList.ItemsSource = _photoGroups;
            PhStatus.Text = _photoGroups.Count == 0
                ? $"No similar photos found among {r.PhotosScanned:N0} photos."
                : $"{_photoGroups.Count} groups of similar photos. You could free {Format.Bytes(r.ReclaimableBytes)}." + (r.Issues.Count > 0 ? $" ({r.Issues.Count} files could not be read.)" : "");
        }
        catch (OperationCanceledException) { PhStatus.Text = "Stopped. Nothing was changed."; }
        finally
        {
            PhScanBtn.IsEnabled = true; PhStopBtn.IsEnabled = false; PhBar.Value = 0;
            PhDeleteBtn.IsEnabled = PhSelectBtn.IsEnabled = _photoGroups.Count > 0;
        }
    }

    private void PhSelect_Click(object s, RoutedEventArgs e)
    {
        foreach (var p in _photoGroups.SelectMany(g => g.Photos)) p.Remove = !p.IsKeep;
        PhList.ItemsSource = null; PhList.ItemsSource = _photoGroups;
    }

    private async void PhDelete_Click(object s, RoutedEventArgs e)
    {
        var picks = _photoGroups.Select(g => (g, paths: g.Photos.Where(p => p.Remove).Select(p => p.Path).ToList())).Where(x => x.paths.Count > 0).ToList();
        int n = picks.Sum(x => x.paths.Count);
        if (n == 0) { PhStatus.Text = "Select at least one photo first."; return; }
        var photoSizes = _photoGroups.SelectMany(g => g.Photos).Where(p => p.Remove).ToList();
        if (!ConfirmDialog.Ask(this, n == 1 ? "Delete this photo?" : $"Delete {n} photos?", n, photoSizes.Sum(p => p.Info.Size), photoSizes.Select(p => p.Path).ToList(), ConfirmKind.Recycle, "Delete photos")) return;
        var remover = new PhotoRemover(new ActivityLog());
        PhScanBtn.IsEnabled = PhDeleteBtn.IsEnabled = PhSelectBtn.IsEnabled = false;
        var bar = new DeleteBar(PhBar, PhStatus, "Deleting", n);
        long freed = 0; int skipped = 0; var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (g, paths) in picks)
        {
            var progress = bar.Batch();
            foreach (var o in await Task.Run(() => remover.Remove(g.Group, paths, progress)))
                if (o.Succeeded) { freed += o.Bytes; gone.Add(o.Path); } else skipped++;
            bar.BatchDone(paths.Count);
        }
        bar.Close(); PhScanBtn.IsEnabled = true;
        foreach (var g in _photoGroups) g.Photos.RemoveAll(p => gone.Contains(p.Path));
        _photoGroups = _photoGroups.Where(g => g.Photos.Count > 1).ToList();
        PhList.ItemsSource = null; PhList.ItemsSource = _photoGroups;
        PhDeleteBtn.IsEnabled = PhSelectBtn.IsEnabled = _photoGroups.Count > 0;
        PhStatus.Text = $"Freed {Format.Bytes(freed)}." + (skipped > 0 ? $" {skipped} left alone because they changed or are protected." : "");
    }

    // ---------- History ----------
    private void RefreshHistory()
    {
        var all = new ActivityLog().ReadAll();
        var shown = HiShowAll.IsChecked == true ? all : all.Where(e => e.Succeeded).ToList();
        HiList.ItemsSource = shown.Take(500).Select(e => new HistoryVm { Entry = e }).ToList();
        HiStatus.Text = shown.Count == 0 ? "Nothing has been removed yet." : $"{shown.Count:N0} entries" + (shown.Count > 500 ? " (showing the newest 500)." : ".");
    }

    private void HiRefresh_Click(object s, RoutedEventArgs e) { if (HiList is not null) RefreshHistory(); }

    private async void HiRestore_Click(object s, RoutedEventArgs e)
    {
        if (HiList.SelectedItem is not HistoryVm vm) { HiStatus.Text = "Select a file in the list first."; return; }
        if (!vm.Entry.CanRestore)
        {
            HiStatus.Text = vm.Entry.Status switch
            {
                DeleteStatus.Deleted => "Cleared caches cannot be put back. The app will rebuild them.",
                DeleteStatus.FreedFromPc => "That file is still in OneDrive. Just open it and it will download again.",
                _ => "Only files in the Recycle Bin can be put back.",
            };
            return;
        }
        var res = await RecycleBinRestorer.RestoreAsync(vm.Path);
        HiStatus.Text = res.Status switch
        {
            RestoreStatus.Restored => $"Put back: {vm.Path}",
            RestoreStatus.AlreadyExists => "A file with that name is already in that folder, so nothing was changed.",
            RestoreStatus.NotInRecycleBin => "It is no longer in the Recycle Bin (it may have been emptied).",
            _ => "Windows could not put it back: " + res.Detail,
        };
    }
}











