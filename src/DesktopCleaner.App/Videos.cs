using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DesktopCleaner.Core.Deletion;
using DesktopCleaner.Core.Videos;
using Microsoft.Win32;

namespace DesktopCleaner.App;

public sealed class VideoVm
{
    public required VideoFile File { get; init; }
    public bool Remove { get; set; }
    public string Name => File.Name;
    public string Folder => File.Folder;
    public string SizeText => Format.Bytes(File.Size);
    public string TypeText => File.Extension.TrimStart('.').ToUpperInvariant();
    public string ModifiedText => File.LastWriteUtc.ToLocalTime().ToString("MMM d, yyyy");
}

public partial class MainWindow
{
    private List<VideoVm> _videosAll = [];
    private List<VideoVm> _videosShown = [];
    private CancellationTokenSource? _vdCts;
    private string? _vdCustomFolder;
    private bool _vdBusyScopeChange;
    private bool _vdFilterReady;

    private void VdScope_Changed(object s, SelectionChangedEventArgs e)
    {
        if (_vdBusyScopeChange || VdFolder is null) return;
        if (VdScope.SelectedIndex == 2)
        {
            var dlg = new OpenFolderDialog();
            if (dlg.ShowDialog() == true) _vdCustomFolder = dlg.FolderName;
            else { _vdBusyScopeChange = true; VdScope.SelectedIndex = 0; _vdBusyScopeChange = false; }
        }
        VdFolder.Text = VdScope.SelectedIndex switch { 0 => VideoLocations.UserFolder, 1 => string.Join(", ", VideoLocations.AllDrives()), _ => _vdCustomFolder ?? "" };
    }

    /// <summary>Opens the Videos tab on a folder and starts looking (used by the Home tab's "Review videos" button).</summary>
    public void ReviewVideosIn(string folder)
    {
        _vdCustomFolder = folder;
        _vdBusyScopeChange = true; VdScope.SelectedIndex = 2; _vdBusyScopeChange = false;
        VdFolder.Text = folder;
        Tabs.SelectedItem = VideoTab;
        VdScan_Click(this, new RoutedEventArgs());
    }

    private void VdStop_Click(object s, RoutedEventArgs e) => _vdCts?.Cancel();

    private async void VdScan_Click(object s, RoutedEventArgs e)
    {
        if (VdScan_IsRunning()) return;
        IReadOnlyList<string> roots = VdScope.SelectedIndex switch
        {
            1 => VideoLocations.AllDrives(),
            2 => _vdCustomFolder is null ? [] : [_vdCustomFolder],
            _ => [VideoLocations.UserFolder],
        };
        if (roots.Count == 0) { VdStatus.Text = "Choose a folder first."; return; }
        var excluded = VdScope.SelectedIndex == 1 ? VideoLocations.SystemFolders() : null;

        _vdCts = new CancellationTokenSource();
        VdScanBtn.IsEnabled = VdScope.IsEnabled = VdDeleteBtn.IsEnabled = VdSelectBtn.IsEnabled = false; VdStopBtn.IsEnabled = true; VdFilters.IsEnabled = false;
        VdBar.IsIndeterminate = true; VdList.ItemsSource = null;
        var progress = new Progress<VideoProgress>(p =>
            VdStatus.Text = $"Looking... {p.FilesSeen:N0} files checked, {p.VideosFound:N0} videos found ({Format.Bytes(p.VideoBytes)}). Press Stop any time to see what has been found.");
        VideoScan? scan = null;
        try { scan = await new VideoFinder().ScanAsync(roots, excluded, progress, _vdCts.Token); }
        catch (OperationCanceledException) { VdStatus.Text = "Stopped. Nothing was changed."; }
        finally { VdBar.IsIndeterminate = false; VdScanBtn.IsEnabled = VdScope.IsEnabled = true; VdStopBtn.IsEnabled = false; }
        if (scan is null) return;

        _videosAll = scan.Videos.Select(v => new VideoVm { File = v }).ToList();
        BuildVideoTypeFilter(scan);
        VdFilters.IsEnabled = _videosAll.Count > 0;
        ApplyVideoFilter();
        VdStatus.Text = _videosAll.Count == 0 ? "No videos found here." :
            $"Found {_videosAll.Count:N0} videos using {Format.Bytes(scan.TotalBytes)}." +
            (scan.CloudOnlySkipped > 0 ? $" {scan.CloudOnlySkipped:N0} online-only OneDrive videos were left out (they use no space here)." : "");
    }

    private bool VdScan_IsRunning() => !VdScanBtn.IsEnabled;

    private void BuildVideoTypeFilter(VideoScan scan)
    {
        _vdFilterReady = false;
        var items = new List<string> { $"All video types ({scan.Videos.Count:N0})" };
        items.AddRange(scan.ByExtension.Select(x => $"{x.Extension.TrimStart('.').ToUpperInvariant()} only ({x.Count:N0}, {Format.Bytes(x.Bytes)})"));
        VdType.ItemsSource = items;
        VdType.SelectedIndex = 0;
        _vdTypeKeys = ["", .. scan.ByExtension.Select(x => x.Extension)];
        _vdFilterReady = true;
    }

    private List<string> _vdTypeKeys = [""];

    private void VdFilter_Changed(object s, SelectionChangedEventArgs e) { if (_vdFilterReady) ApplyVideoFilter(); }

    private void ApplyVideoFilter()
    {
        if (!_vdFilterReady && VdType.Items.Count == 0) { VdList.ItemsSource = null; VdSummary.Text = ""; return; }
        string ext = VdType.SelectedIndex >= 0 && VdType.SelectedIndex < _vdTypeKeys.Count ? _vdTypeKeys[VdType.SelectedIndex] : "";
        long minBytes = VdSize.SelectedIndex switch { 1 => 50L << 20, 2 => 100L << 20, 3 => 500L << 20, 4 => 1L << 30, _ => 0 };
        IEnumerable<VideoVm> q = _videosAll.Where(v => (ext.Length == 0 || v.File.Extension == ext) && v.File.Size >= minBytes);
        q = VdSort.SelectedIndex switch
        {
            1 => q.OrderBy(v => v.File.Size),
            2 => q.OrderByDescending(v => v.File.LastWriteUtc),
            3 => q.OrderBy(v => v.File.LastWriteUtc),
            4 => q.OrderBy(v => v.File.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => q.OrderByDescending(v => v.File.Size),
        };
        _videosShown = q.ToList();
        foreach (var hidden in _videosAll.Except(_videosShown)) hidden.Remove = false;   // never delete something the filter is hiding
        VdList.ItemsSource = _videosShown;
        VdSummary.Text = _videosAll.Count == 0 ? "" : $"{_videosShown.Count:N0} shown, {Format.Bytes(_videosShown.Sum(v => v.File.Size))}";
        VdSelectBtn.IsEnabled = VdDeleteBtn.IsEnabled = _videosShown.Count > 0;
    }

    private void VdSelect_Click(object s, RoutedEventArgs e)
    {
        foreach (var v in _videosShown) v.Remove = true;
        VdList.ItemsSource = null; VdList.ItemsSource = _videosShown;
    }

    private void VdList_Changed(object s, SelectionChangedEventArgs e) =>
        VdPlayBtn.IsEnabled = VdShowBtn.IsEnabled = VdList.SelectedItem is VideoVm;

    private void VdPlay_Click(object s, RoutedEventArgs e)
    {
        if (VdList.SelectedItem is not VideoVm v || !File.Exists(v.File.Path)) return;
        try { Process.Start(new ProcessStartInfo(v.File.Path) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { VdStatus.Text = "Could not open it: " + ex.Message; }
    }

    private void VdShow_Click(object s, RoutedEventArgs e)
    {
        if (VdList.SelectedItem is VideoVm v && File.Exists(v.File.Path))
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { $"/select,{v.File.Path}" }, UseShellExecute = true });
    }

    private async void VdDelete_Click(object s, RoutedEventArgs e)
    {
        var chosen = _videosShown.Where(v => v.Remove).ToList();
        if (chosen.Count == 0) { VdStatus.Text = "Select at least one video first."; return; }
        if (!ConfirmDialog.Ask(this, chosen.Count == 1 ? "Delete this video?" : $"Delete {chosen.Count} videos?", chosen.Count, chosen.Sum(c => c.File.Size),
                chosen.Select(c => c.File.Path).ToList(), ConfirmKind.Recycle, "Delete videos")) return;

        VdScanBtn.IsEnabled = VdDeleteBtn.IsEnabled = VdSelectBtn.IsEnabled = false;
        var bar = new DeleteBar(VdBar, VdStatus, "Deleting", chosen.Count);
        var files = chosen.Select(c => c.File).ToList();
        var progress = bar.Batch();
        var res = await Task.Run(() => new VideoRemover(new ActivityLog()).Remove(files, progress));
        bar.Close(); VdScanBtn.IsEnabled = true;
        var gone = res.Where(r => r.Succeeded).Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _videosAll = _videosAll.Where(v => !gone.Contains(v.File.Path)).ToList();
        ApplyVideoFilter();
        VdStatus.Text = $"Freed {Format.Bytes(res.Sum(r => r.Bytes))}." + (res.Any(r => !r.Succeeded) ? $" {res.Count(r => !r.Succeeded)} left alone because they changed or were in use." : "");
    }
}

