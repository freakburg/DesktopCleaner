using System.IO;
using System.Windows;
using DesktopCleaner.Core.Deletion;
using DesktopCleaner.Core.EmptyFolders;
using Microsoft.Win32;

namespace DesktopCleaner.App;

public sealed class EmptyVm
{
    public required EmptyFolder Folder { get; init; }
    public bool Remove { get; set; }
    public string Path => Folder.Path;
    public string Inside => Folder.NestedEmpty == 0 ? "-" : Folder.NestedEmpty.ToString();
    public string Changed => Folder.LastWriteUtc.ToLocalTime().ToString("d");
}

public partial class MainWindow
{
    private List<EmptyVm> _empties = [];

    private void EmBrowse_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog() == true) EmFolder.Text = dlg.FolderName;
    }

    private void EmAll_Click(object s, RoutedEventArgs e)
    {
        foreach (var v in _empties) v.Remove = true;
        EmList.ItemsSource = null; EmList.ItemsSource = _empties;
    }

    private async void EmScan_Click(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(EmFolder.Text)) EmBrowse_Click(s, e);
        if (!Directory.Exists(EmFolder.Text)) { EmStatus.Text = "Choose a folder to search first."; return; }
        EmScanBtn.IsEnabled = EmDeleteBtn.IsEnabled = EmAllBtn.IsEnabled = false;
        EmStatus.Text = "Looking...";
        try
        {
            var found = await new EmptyFolderFinder().ScanAsync(EmFolder.Text);
            _empties = found.Select(f => new EmptyVm { Folder = f }).ToList();
            EmList.ItemsSource = _empties;
            EmStatus.Text = found.Count == 0 ? "No empty folders here. Nice and tidy." : $"{found.Count} empty folders found. Nothing is ticked yet.";
            EmDeleteBtn.IsEnabled = EmAllBtn.IsEnabled = found.Count > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { EmStatus.Text = ex.Message; }
        finally { EmScanBtn.IsEnabled = true; }
    }

    private async void EmDelete_Click(object s, RoutedEventArgs e)
    {
        var chosen = _empties.Where(v => v.Remove).ToList();
        if (chosen.Count == 0) { EmStatus.Text = "Nothing is ticked."; return; }
        if (!ConfirmDialog.Ask(this, chosen.Count == 1 ? "Delete this empty folder?" : $"Delete {chosen.Count} empty folders?", chosen.Count, 0, chosen.Select(c => c.Path).ToList(), ConfirmKind.Recycle, "Delete folders")) return;
        EmScanBtn.IsEnabled = EmDeleteBtn.IsEnabled = EmAllBtn.IsEnabled = false;
        var bar = new DeleteBar(EmBar, EmStatus, "Deleting", chosen.Count);
        var folders = chosen.Select(c => c.Folder).ToList();
        var progress = bar.Batch();
        var res = await Task.Run(() => new EmptyFolderFinder(new ActivityLog()).Remove(folders, progress));
        bar.Close(); EmScanBtn.IsEnabled = true;
        var gone = res.Where(r => r.Status == EmptyFolderStatus.Removed).Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _empties = _empties.Where(v => !gone.Contains(v.Path)).ToList();
        EmList.ItemsSource = null; EmList.ItemsSource = _empties;
        EmDeleteBtn.IsEnabled = EmAllBtn.IsEnabled = _empties.Count > 0;
        EmStatus.Text = $"Removed {gone.Count} folders. {res.Count - gone.Count} skipped (no longer empty or protected).";
    }
}




