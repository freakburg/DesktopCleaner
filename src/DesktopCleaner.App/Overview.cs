using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using DesktopCleaner.Core.Cleanup;
using DesktopCleaner.Core.Deletion;
using DesktopCleaner.Core.Caches;
using DesktopCleaner.Core.Downloads;
using DesktopCleaner.Core.EmptyFolders;
using DesktopCleaner.Core.Cloud;
using DesktopCleaner.Core.History;
using DesktopCleaner.Core.Storage;
using Microsoft.Win32;

namespace DesktopCleaner.App;

public sealed class BarVm
{
    public required string Name { get; init; }
    public string Path { get; init; } = "";
    public double Percent { get; init; }
    public required string Label { get; init; }
    public required Brush Brush { get; init; }
    public FolderNode? Node { get; init; }
    public bool Synthetic { get; init; }
    public Visibility DrillVisibility => Node is { Children.Count: > 0 } ? Visibility.Visible : Visibility.Hidden;
    public Visibility ShowVisibility => Synthetic || Path.Length == 0 ? Visibility.Hidden : Visibility.Visible;
}

public sealed class BigFileVm
{
    public required string Name { get; init; }
    public required string Folder { get; init; }
    public required string Path { get; init; }
    public double Percent { get; init; }
    public required string Label { get; init; }
    public required Brush Brush { get; init; }
}

public sealed class FixVm
{
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public string SizeText { get; init; } = "";
    public string ButtonText { get; init; } = "";
    public Func<Task>? Action { get; init; }
    public Visibility ButtonVisibility => Action is null ? Visibility.Collapsed : Visibility.Visible;
}

public partial class MainWindow
{
    private static readonly Dictionary<string, Brush> CategoryColors = new()
    {
        ["Videos"] = Brush("#8E44AD"), ["Pictures"] = Brush("#2E86C1"), ["Music & audio"] = Brush("#16A085"),
        ["Documents"] = Brush("#D68910"), ["Zip & archives"] = Brush("#CA6F1E"), ["Apps & installers"] = Brush("#C0392B"),
        ["Code & dev files"] = Brush("#566573"), ["Temporary & logs"] = Brush("#7F8C8D"), [FileCategories.Other] = Brush("#95A5A6"),
    };
    private static readonly Brush FolderBrush = Brush("#2E86C1");
    private static Brush Brush(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }

    private CancellationTokenSource? _ovCts;
    private bool _scopeReady;

    private void InitOverview()
    {
        string P(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var profile = P(Environment.SpecialFolder.UserProfile);
        OvScope.ItemsSource = new (string Label, string? Path)[]
        {
            ("Downloads", Path.Combine(profile, "Downloads")), ("Documents", P(Environment.SpecialFolder.MyDocuments)),
            ("Desktop", P(Environment.SpecialFolder.DesktopDirectory)), ("Pictures", P(Environment.SpecialFolder.MyPictures)),
            ("Videos", P(Environment.SpecialFolder.MyVideos)), ("Everything in my user folder (slower)", profile),
            ("Another folder...", null),
        }.Select(i => new ScopeItem(i.Label, i.Path)).ToList();
        OvScope.DisplayMemberPath = nameof(ScopeItem.Label);
        OvScope.SelectedIndex = 0;
        _scopeReady = true;
        OvFolder.Text = ((ScopeItem)OvScope.SelectedItem).Path;
    }

    private sealed record ScopeItem(string Label, string? Path);

    private void OvScope_Changed(object s, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_scopeReady || OvScope.SelectedItem is not ScopeItem item) return;
        if (item.Path is not null) { OvFolder.Text = item.Path; return; }
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog() == true) OvFolder.Text = dlg.FolderName; else { OvScope.SelectedIndex = 0; }
    }

    private void OvCancel_Click(object s, RoutedEventArgs e) => _ovCts?.Cancel();

    private StorageReport? _report;
    private StorageReport? _lastReport;
    private List<FolderNode> _trail = [];
    private FolderNode Current => _trail[^1];

    private static readonly Brush[] MapPalette =
        new[] { "#2E86C1", "#16A085", "#D68910", "#8E44AD", "#CA6F1E", "#C0392B", "#566573", "#27AE60", "#2874A6", "#B03A2E" }.Select(h => Brush(h)).ToArray();

    private record WhereItem(string Name, string Path, long Bytes, FolderNode? Node, bool Synthetic);

    private List<WhereItem> WhereItems()
    {
        var cur = Current;
        var items = cur.Children.Select(c => new WhereItem(c.Name, c.Path, c.Bytes, c, false)).ToList();
        if (cur.OwnBytes > 0) items.Add(new WhereItem("Files directly in this folder", "", cur.OwnBytes, null, true));
        if (cur.OtherBytes > 0) items.Add(new WhereItem("Many small folders", "", cur.OtherBytes, null, true));
        return items.OrderByDescending(i => i.Bytes).ToList();
    }

    private void RenderWhere()
    {
        // breadcrumb: earlier levels are links, the current one is plain text
        Crumbs.Children.Clear();
        for (int i = 0; i < _trail.Count; i++)
        {
            int level = i;
            if (i > 0) Crumbs.Children.Add(new System.Windows.Controls.TextBlock { Text = "  >  ", Foreground = Brush("#888"), VerticalAlignment = VerticalAlignment.Center });
            if (i == _trail.Count - 1)
                Crumbs.Children.Add(new System.Windows.Controls.TextBlock { Text = _trail[i].Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            else
            {
                var b = new System.Windows.Controls.Button { Content = _trail[i].Name, Padding = new Thickness(6, 1, 6, 1) };
                b.Click += (_, _) => { _trail = _trail.Take(level + 1).ToList(); RenderWhere(); };
                Crumbs.Children.Add(b);
            }
        }

        var items = WhereItems();
        var total = Math.Max(1, Current.Bytes);
        int n = 0;
        FolderList.ItemsSource = items.Select(it => new BarVm
        {
            Name = it.Name, Path = it.Path, Node = it.Node, Synthetic = it.Synthetic,
            Percent = 100.0 * it.Bytes / total, Label = $"{Format.Bytes(it.Bytes)}  ({100.0 * it.Bytes / total:F0}%)",
            Brush = it.Synthetic ? Brush("#95A5A6") : MapPalette[n++ % MapPalette.Length],
        }).ToList();
        if (OvViewMap.IsChecked == true) RenderMap();
    }

    private void Drill(FolderNode node)
    {
        if (node.Children.Count == 0) return;
        _trail.Add(node);
        // A folder that only holds one subfolder tells you nothing; step straight through it.
        while (node is { Children.Count: 1, OwnBytes: 0, OtherBytes: 0 })
        {
            node = node.Children[0];
            _trail.Add(node);
        }
        RenderWhere();
    }

    private void Drill_Click(object s, RoutedEventArgs e)
    {
        if (((FrameworkElement)s).DataContext is BarVm { Node: { } node }) Drill(node);
    }

    private void OvView_Changed(object s, RoutedEventArgs e)
    {
        if (FolderList is null || MapBorder is null) return; // fires during InitializeComponent
        bool map = OvViewMap.IsChecked == true;
        FolderList.Visibility = map ? Visibility.Collapsed : Visibility.Visible;
        MapBorder.Visibility = map ? Visibility.Visible : Visibility.Collapsed;
        if (map && _report is not null) RenderMap();
    }

    private void MapBorder_SizeChanged(object s, SizeChangedEventArgs e)
    {
        if (OvViewMap.IsChecked == true && _report is not null) RenderMap();
    }

    private void RenderMap()
    {
        MapCanvas.Children.Clear();
        double w = MapBorder.ActualWidth - 2, h = MapBorder.ActualHeight - 2;
        if (w < 10 || h < 10 || _trail.Count == 0) return;
        var items = WhereItems();
        var total = Math.Max(1, Current.Bytes);
        var byKey = new Dictionary<string, (WhereItem Item, Brush Brush)>();
        int n = 0;
        foreach (var it in items)
            byKey[it.Synthetic ? "~" + it.Name : it.Path] = (it, it.Synthetic ? Brush("#95A5A6") : MapPalette[n++ % MapPalette.Length]);

        foreach (var r in Treemap.Layout(items.Select(i => new TreemapItem(i.Synthetic ? "~" + i.Name : i.Path, i.Bytes)), w, h))
        {
            var (it, brush) = byKey[r.Key];
            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(5, 3, 3, 3) };
            if (r.Width > 64 && r.Height > 34)
            {
                panel.Children.Add(new System.Windows.Controls.TextBlock { Text = it.Name, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                panel.Children.Add(new System.Windows.Controls.TextBlock { Text = Format.Bytes(it.Bytes), Foreground = Brushes.White, Opacity = 0.9 });
            }
            var tile = new System.Windows.Controls.Border
            {
                Width = Math.Max(1, r.Width), Height = Math.Max(1, r.Height), Background = brush,
                BorderBrush = Brushes.White, BorderThickness = new Thickness(1.5), Child = panel, ClipToBounds = true,
                ToolTip = $"{it.Name}\n{Format.Bytes(it.Bytes)} ({100.0 * it.Bytes / total:F0}% of this folder)" + (it.Node is { Children.Count: > 0 } ? "\nClick to look inside" : ""),
                Cursor = it.Node is { Children.Count: > 0 } ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow,
            };
            if (it.Node is { } node) tile.MouseLeftButtonUp += (_, _) => Drill(node);
            System.Windows.Controls.Canvas.SetLeft(tile, r.X); System.Windows.Controls.Canvas.SetTop(tile, r.Y);
            MapCanvas.Children.Add(tile);
        }
    }

    private void ShowFile_Click(object s, RoutedEventArgs e)
    {
        if (((FrameworkElement)s).DataContext is BigFileVm vm && File.Exists(vm.Path))
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { $"/select,{vm.Path}" }, UseShellExecute = true });
    }
    // ---------- since your last check ----------
    private void RecordAndShowGrowth(string root, StorageReport report)
    {
        try
        {
            var store = new SnapshotStore();
            var history = store.For(root);
            var previous = history.LastOrDefault();
            var snap = SnapshotStore.From(report);
            store.Save(snap);
            var all = history.Append(snap).ToList();

            GrowthBox.Visibility = Visibility.Visible;
            var cmp = SnapshotStore.Compare(previous, snap);
            if (cmp is null)
            {
                GrowthTitle.Text = "First check of this folder";
                GrowthDetail.Text = "Next time you press Check, we will show what grew or shrank since today.";
            }
            else
            {
                var days = cmp.DaysAgo(DateTime.UtcNow);
                var when = days < 1 ? "earlier today" : days < 2 ? "yesterday" : $"{(int)days} days ago";
                GrowthTitle.Text = $"Since your last check ({when})";
                var dir = System.IO.Path.GetFileName(root.TrimEnd('\\')) is { Length: > 0 } n ? n : root;
                var headline = Math.Abs(cmp.TotalDelta) < 1024 * 1024 ? $"{dir} is about the same size."
                    : cmp.TotalDelta > 0 ? $"{dir} grew by {Format.Bytes(cmp.TotalDelta)}." : $"{dir} shrank by {Format.Bytes(-cmp.TotalDelta)}.";
                var movers = cmp.FolderChanges.Where(c => Math.Abs(c.Delta) >= 1024 * 1024).Take(3)
                    .Select(c => $"{c.Name} ({(c.Delta > 0 ? "+" : "-")}{Format.Bytes(Math.Abs(c.Delta))})").ToList();
                GrowthDetail.Text = headline + (movers.Count > 0 ? " Biggest changes: " + string.Join(", ", movers) + "." : "");
            }
            DrawSparkline(all);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { GrowthBox.Visibility = Visibility.Collapsed; }
    }

    private void DrawSparkline(IReadOnlyList<SpaceSnapshot> points)
    {
        Spark.Children.Clear();
        SparkNote.Text = "";
        if (points.Count < 2) { Spark.Visibility = SparkNote.Visibility = Visibility.Collapsed; return; }
        Spark.Visibility = SparkNote.Visibility = Visibility.Visible;
        var recent = points.TakeLast(30).ToList();
        double min = recent.Min(p => p.TotalBytes), max = recent.Max(p => p.TotalBytes), span = Math.Max(1, max - min);
        var poly = new System.Windows.Shapes.Polyline { Stroke = Brush("#2E86C1"), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
        for (int i = 0; i < recent.Count; i++)
            poly.Points.Add(new Point(i * Spark.Width / (recent.Count - 1), Spark.Height - 4 - (recent[i].TotalBytes - min) / span * (Spark.Height - 8)));
        Spark.Children.Add(poly);
        SparkNote.Text = $"Size at each of your last {recent.Count} checks: {Format.Bytes(recent[0].TotalBytes)} to {Format.Bytes(recent[^1].TotalBytes)}";
    }

    // ---------- save report ----------
    private void OvSave_Click(object s, RoutedEventArgs e)
    {
        if (_lastReport is null) return;
        var dlg = new SaveFileDialog
        {
            Title = "Save report", FileName = $"storage-report-{DateTime.Now:yyyyMMdd}",
            Filter = "Printable page (*.html)|*.html|Spreadsheet (*.csv)|*.csv", DefaultExt = ".html",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            bool csv = dlg.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
            File.WriteAllText(dlg.FileName, csv ? ReportExporter.ToCsv(_lastReport) : ReportExporter.ToHtml(_lastReport), new System.Text.UTF8Encoding(true));
            OvStatus.Text = $"Saved {dlg.FileName}." + (csv ? "" : " Open it and use Print > Save as PDF if you want a PDF.");
            if (!csv) Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { OvStatus.Text = "Could not save: " + ex.Message; }
    }

    // ---------- started from the Explorer right-click menu ----------
    public void CheckFolder(string path)
    {
        if (!Directory.Exists(path)) { OvStatus.Text = "That folder could not be found."; return; }
        var items = ((IEnumerable<ScopeItem>)OvScope.ItemsSource).ToList();
        var name = System.IO.Path.GetFileName(path.TrimEnd('\\'));
        items.Insert(0, new ScopeItem($"Folder: {(name.Length > 0 ? name : path)}", path));
        OvScope.ItemsSource = items;
        OvScope.SelectedIndex = 0;
        OvFolder.Text = path;
        Tabs.SelectedIndex = 0;
        OvScan_Click(this, new RoutedEventArgs());
    }
    private void OpenFolder_Click(object s, RoutedEventArgs e)
    {
        if (((FrameworkElement)s).DataContext is BarVm { Path.Length: > 0 } vm && Directory.Exists(vm.Path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{vm.Path}\"") { UseShellExecute = true });
    }

    private async void Fix_Click(object s, RoutedEventArgs e)
    {
        if (((FrameworkElement)s).DataContext is FixVm { Action: { } action }) await action();
    }

    private async void OvScan_Click(object s, RoutedEventArgs e)
    {
        var root = OvFolder.Text;
        if (!Directory.Exists(root)) { OvStatus.Text = "That folder could not be found. Pick another place to look."; return; }

        OvScanBtn.IsEnabled = OvScope.IsEnabled = false; OvCancelBtn.IsEnabled = true;
        OvBar.IsIndeterminate = true;
        _ovCts = new CancellationTokenSource();
        var progress = new Progress<StorageProgress>(p => OvStatus.Text = $"Looking through your files... {p.FilesSeen:N0} so far ({Format.Bytes(p.Bytes)}). Big folders can take a few minutes.");
        try
        {
            var report = await new StorageAnalyzer().AnalyzeAsync(root, progress: progress, ct: _ovCts.Token);
            OvStatus.Text = "Almost done - looking for things you can clear...";
            var fixes = await BuildFixesAsync(report, root);
            Show(report, fixes);
            _lastReport = report; OvSaveBtn.IsEnabled = true;
            RecordAndShowGrowth(root, report);
            OvStatus.Text = $"Done. {Format.Bytes(report.TotalBytes)} in {report.FileCount:N0} files.";
        }
        catch (OperationCanceledException) { OvStatus.Text = "Stopped. Nothing was changed."; }
        finally { OvBar.IsIndeterminate = false; OvScanBtn.IsEnabled = OvScope.IsEnabled = true; OvCancelBtn.IsEnabled = false; }
    }

    private void Show(StorageReport report, List<FixVm> fixes)
    {
        var total = Math.Max(1, report.TotalBytes);
        CatList.ItemsSource = report.ByCategory.Select(c => new BarVm
        {
            Name = c.Name, Percent = 100.0 * c.Bytes / total,
            Label = $"{Format.Bytes(c.Bytes)}  ({100.0 * c.Bytes / total:F0}%)",
            Brush = CategoryColors.GetValueOrDefault(c.Name, FolderBrush),
        }).ToList();
        _report = report; _trail = [report.Root];
        RenderWhere();
        var biggest = Math.Max(1, report.LargestFiles.FirstOrDefault()?.Size ?? 1);
        FileList.ItemsSource = report.LargestFiles.Select(f => new BigFileVm
        {
            Name = f.Name, Folder = f.Folder, Path = f.Path, Percent = 100.0 * f.Size / biggest, Label = Format.Bytes(f.Size),
            Brush = CategoryColors.GetValueOrDefault(FileCategories.For(System.IO.Path.GetExtension(f.Path)), FolderBrush),
        }).ToList();
        FilesHeader.Visibility = FilesHint.Visibility = report.LargestFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;        FixList.ItemsSource = fixes;
        FixHeader.Visibility = CatHeader.Visibility = FolderHeader.Visibility = WhereHint.Visibility = Visibility.Visible;
    }

    private async Task<List<FixVm>> BuildFixesAsync(StorageReport report, string root)
    {
        var fixes = new List<FixVm>();

        var rule = CleanupRule.DefaultTemp();
        // The four checks are independent and mostly disk-bound, so run them side by side.
        var tempTask = new CleanupEngine().PreviewAsync(rule);
        var dlTask = new DownloadsReviewer().ScanAsync();
        var cacheTask = new CacheCleaner().ScanAsync();
        var emptyTask = new EmptyFolderFinder().ScanAsync(root);
        await Task.WhenAll(tempTask, dlTask, cacheTask, emptyTask);
        var temp = tempTask.Result;
        if (temp.Candidates.Count > 0)
            fixes.Add(new FixVm
            {
                Title = "Leftover temporary files",
                Detail = $"{temp.Candidates.Count:N0} scrap files that apps left behind more than {rule.OlderThanDays} days ago. Safe to clear, and you can undo from the Recycle Bin.",
                SizeText = Format.Bytes(temp.RecoverableBytes), ButtonText = "Clear these",
                Action = () => ClearTempAsync(rule, temp),
            });

        var dl = dlTask.Result;
        if (dl.Count > 0)
            fixes.Add(new FixVm
            {
                Title = "Old setup files, ZIPs and big downloads",
                Detail = $"{dl.Count} files in your Downloads folder are worth a look. You choose what goes. Nothing is ticked for you.",
                SizeText = Format.Bytes(dl.Sum(i => i.Size)), ButtonText = "Review",
                Action = () => { Tabs.SelectedItem = DlTab; DlScan_Click(this, new RoutedEventArgs()); return Task.CompletedTask; },
            });

        var caches = cacheTask.Result.Where(c => c.Bytes > 0).ToList();
        if (caches.Count > 0)
            fixes.Add(new FixVm
            {
                Title = "Browser and app caches",
                Detail = $"Saved copies of pages and pictures from {string.Join(", ", caches.Take(3).Select(c => c.Target.Name))}{(caches.Count > 3 ? " and more" : "")}. They rebuild by themselves, so clearing them is safe.",
                SizeText = Format.Bytes(caches.Sum(c => c.Bytes)), ButtonText = "Review",
                Action = () => { Tabs.SelectedItem = CacheTab; CaScan_Click(this, new RoutedEventArgs()); return Task.CompletedTask; },
            });

        var empties = emptyTask.Result;
        if (empties.Count > 0)
            fixes.Add(new FixVm
            {
                Title = "Empty folders",
                Detail = $"{empties.Count:N0} folders with nothing in them. Harmless clutter you can tidy away.",
                ButtonText = "Review",
                Action = () => { EmFolder.Text = root; Tabs.SelectedItem = EmptyTab; EmScan_Click(this, new RoutedEventArgs()); return Task.CompletedTask; },
            });

        fixes.Add(new FixVm
        {
            Title = "Duplicate files",
            Detail = "Finds files that are exact copies of each other, even with different names. You decide which copy to remove.",
            ButtonText = "Look for duplicates",
            Action = () => { FolderBox.Text = root; Tabs.SelectedItem = DupTab; Scan_Click(this, new RoutedEventArgs()); return Task.CompletedTask; },
        });

        if (report.CloudOnlyFiles > 0)
            fixes.Add(new FixVm
            {
                Title = "Some files are online-only (OneDrive)",
                Detail = $"{report.CloudOnlyFiles:N0} files ({Format.Bytes(report.CloudOnlyBytes)}) live only in the cloud. They use no space on this PC, so they are not counted here and we never download them.",
            });
        else if (OneDriveSpace.Roots().Count > 0 && OneDriveSpace.IsInsideOneDrive(System.IO.Path.Combine(root, "x")))
            fixes.Add(new FixVm
            {
                Title = "OneDrive can free up space",
                Detail = "Big OneDrive files that are also saved on this PC can be set to online-only. They stay safe in the cloud.",
                ButtonText = "See files", Action = () => { Tabs.SelectedItem = OneDriveTab; OdScan_Click(this, new RoutedEventArgs()); return Task.CompletedTask; },
            });

        if (report.ByCategory.FirstOrDefault(c => c.Name != FileCategories.Other) is { } top && top.Bytes > report.TotalBytes * 0.25)
            fixes.Add(new FixVm
            {
                Title = $"{top.Name} are your biggest space user",
                Detail = $"They make up {100.0 * top.Bytes / report.TotalBytes:F0}% of this folder. Look through the biggest folders below for things you no longer need.",
                SizeText = Format.Bytes(top.Bytes),
            });
        return fixes;
    }

    private async Task ClearTempAsync(CleanupRule rule, RuleReport preview)
    {
        if (!ConfirmDialog.Ask(this, "Clear leftover temporary files?", preview.Candidates.Count, preview.RecoverableBytes,
                preview.Candidates.OrderByDescending(c => c.Size).Select(c => c.Path).ToList(), ConfirmKind.Recycle, "Delete files")) return;
        OvBar.IsIndeterminate = true;
        var r = await new CleanupEngine(new ActivityLog()).RunAsync(rule);
        OvBar.IsIndeterminate = false;
        OvStatus.Text = $"Freed {Format.Bytes(r.FreedBytes)}. Skipped {r.Skipped.Count + r.Outcomes.Count(o => o.Status != DeleteStatus.Recycled)} files that were in use or changed.";
        FixList.ItemsSource = (FixList.ItemsSource as List<FixVm>)?.Where(f => f.Title != "Leftover temporary files").ToList();
    }
}









