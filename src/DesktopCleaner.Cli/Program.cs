using DesktopCleaner.Core.Caches;
using DesktopCleaner.Core.Videos;
using DesktopCleaner.Core.Cloud;
using DesktopCleaner.Core;
using DesktopCleaner.Core.Apps;
using DesktopCleaner.Core.Cleanup;
using DesktopCleaner.Core.Photos;
using DesktopCleaner.Core.Deletion;
using DesktopCleaner.Core.Downloads;
using DesktopCleaner.Core.EmptyFolders;
using DesktopCleaner.Core.Duplicates;
using DesktopCleaner.Core.Scheduling;
using DesktopCleaner.Core.Storage;
using System.Diagnostics;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("""
        Usage:
          dcleaner dupes <folder> [--min-size <bytes>] [--delete-extras]
          dcleaner cleanup [--apply] [--report]     dry run by default; --apply sends matches to the Recycle Bin
          dcleaner downloads [folder] [--days 30] [--large-mb 500] [--remove]
                                                    flag old installers/archives and large files; --remove asks per file
          dcleaner videos [folder] [--all-drives] [--min-mb N] [--type .mp4]   list video files, biggest first
          dcleaner temp [--days 7] [--apply]        temp-folder files older than N days (0 = everything); dry run unless --apply (deleted for good)
          dcleaner caches [--apply]                 browser/app caches; dry run by default, --apply clears them (cannot be undone, they rebuild themselves)
          dcleaner photos <folder> [--strict]       find similar photos (resized/re-saved copies)
          dcleaner onedrive [--min-mb 5]            big OneDrive files that are also stored on this PC (can be set to online-only)
          dcleaner apps                             installed programs, biggest first
          dcleaner history                          list what was removed; items sent to the Recycle Bin can be restored
          dcleaner restore <full path>              put a removed file back from the Recycle Bin
          dcleaner empty <folder> [--remove]        list empty folders; --remove asks before sending them to the Recycle Bin
          dcleaner storage <folder> [--top 10] [--threads N]    what is using space, by file type and folder
          dcleaner schedule [--day MON] [--time 09:00] | schedule --remove
                                                    weekly dry-run scan that writes a report (never deletes)
        Rules live in %LOCALAPPDATA%\DesktopCleaner\rules.json (default: user temp files older than 14 days).
        """);
    return 1;
}

if (args[0] == "cleanup") return await RunCleanup(args);
if (args[0] == "schedule") return RunSchedule(args);
if (args[0] == "downloads") return await RunDownloads(args);
if (args[0] == "storage") return await RunStorage(args);
if (args[0] == "empty") return await RunEmpty(args);
if (args[0] == "caches") return await RunCaches(args);
if (args[0] == "temp") return await RunTemp(args);
if (args[0] == "videos") return await RunVideos(args);
if (args[0] == "photos") return await RunPhotos(args);
if (args[0] == "history") return RunHistory();
if (args[0] == "onedrive") return await RunOneDrive(args);
if (args[0] == "apps") return RunApps();
if (args[0] == "restore") return await RunRestore(args);
if (args[0] != "dupes" || args.Length < 2) { Console.Error.WriteLine("Unknown command. Use --help."); return 1; }
long min = 1; bool delete = false;
for (int i = 2; i < args.Length; i++)
{
    if (args[i] == "--min-size" && i + 1 < args.Length) min = long.Parse(args[++i]);
    else if (args[i] == "--delete-extras") delete = true;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var progress = new Progress<ScanProgress>(p =>
    Console.Error.Write($"\r{p.Phase}: {p.FilesSeen} files, hashed {p.FilesHashed}/{p.FilesToHash}   "));
ScanResult result;
try { result = await new DuplicateScanner().ScanAsync(args[1], new ScanOptions { MinimumSize = min }, progress, cts.Token); }
catch (OperationCanceledException) { Console.Error.WriteLine("\nCancelled."); return 130; }
catch (DirectoryNotFoundException) { Console.Error.WriteLine("Folder not found."); return 2; }
Console.Error.WriteLine();

foreach (var g in result.Groups)
{
    Console.WriteLine($"\n{g.Files.Count} copies x {Fmt(g.FileSize)}  (wasted {Fmt(g.WastedBytes)})");
    foreach (var f in g.Files) Console.WriteLine($"  {f.Path}");
}
Console.WriteLine($"\n{result.Groups.Count} groups, {Fmt(result.WastedBytes)} recoverable, {result.FilesScanned} files scanned, {result.Issues.Count} unreadable.");

if (delete && result.Groups.Count > 0)
{
    Console.Write("Send all but the first copy of each group to the Recycle Bin? [y/N] ");
    if (Console.ReadLine()?.Trim().ToLowerInvariant() != "y") return 0;
    var remover = new DuplicateRemover(new ActivityLog());
    long freed = 0;
    foreach (var g in result.Groups)
        foreach (var o in await remover.RemoveAsync(g, g.Files.Skip(1).Select(f => f.Path)))
        {
            freed += o.Bytes;
            if (o.Status != DeleteStatus.Recycled) Console.WriteLine($"  {o.Status}: {o.Path}");
        }
    Console.WriteLine($"Freed {Fmt(freed)}. Log: {new ActivityLog().FilePath}");
}
return 0;

static string Fmt(long b) => b switch
{
    >= 1L << 30 => $"{b / (double)(1L << 30):F2} GB",
    >= 1L << 20 => $"{b / (double)(1L << 20):F1} MB",
    >= 1L << 10 => $"{b / 1024.0:F0} KB",
    _ => $"{b} B",
};

static async Task<int> RunCleanup(string[] args)
{
    bool apply = args.Contains("--apply"), report = args.Contains("--report");
    var engine = new CleanupEngine(new ActivityLog());
    var reports = new List<RuleReport>();
    foreach (var rule in RuleStore.Load().Where(r => r.Enabled))
        reports.Add(apply ? await engine.RunAsync(rule) : await engine.PreviewAsync(rule));
    var text = ReportWriter.Render(reports, apply);
    Console.WriteLine(text);
    if (report) Console.WriteLine($"Report saved: {ReportWriter.Save(text)}");
    return 0;
}

static int RunSchedule(string[] args)
{
    var scheduler = new ReportScheduler();
    if (args.Contains("--remove")) { scheduler.Disable(); Console.WriteLine("Schedule removed."); return 0; }
    string day = "MON", time = "09:00";
    for (int i = 1; i < args.Length - 1; i++)
    {
        if (args[i] == "--day") day = args[i + 1].ToUpperInvariant();
        if (args[i] == "--time") time = args[i + 1];
    }
    var dow = Enum.GetValues<DayOfWeek>().FirstOrDefault(d => d.ToString()[..3].Equals(day[..Math.Min(3, day.Length)], StringComparison.OrdinalIgnoreCase));
    try { scheduler.Enable(dow, time, Environment.ProcessPath!, "cleanup --report"); }
    catch (Exception ex) when (ex is ArgumentException or IOException) { Console.Error.WriteLine(ex.Message); return 1; }
    Console.WriteLine($"Scheduled a weekly report for {dow} at {time}.");
    return 0;
}
static async Task<int> RunDownloads(string[] args)
{
    string? folder = null; int days = 30; long largeMb = 500; bool remove = args.Contains("--remove");
    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] == "--days" && i + 1 < args.Length) days = int.Parse(args[++i]);
        else if (args[i] == "--large-mb" && i + 1 < args.Length) largeMb = long.Parse(args[++i]);
        else if (!args[i].StartsWith("--")) folder = args[i];
    }
    var reviewer = new DownloadsReviewer(new ActivityLog());
    var items = await reviewer.ScanAsync(folder, new DownloadsOptions { OldAfterDays = days, LargeThresholdBytes = largeMb * 1024 * 1024 });
    foreach (var it in items)
        Console.WriteLine($"{ReportWriter.Bytes(it.Size),10}  {it.Reasons,-22} {it.Path}");
    Console.WriteLine($"\n{items.Count} files flagged, {ReportWriter.Bytes(items.Sum(i => i.Size))} total.");
    if (!remove) return 0;
    var chosen = new List<DownloadItem>();
    foreach (var it in items)
    {
        Console.Write($"Recycle {Path.GetFileName(it.Path)} ({ReportWriter.Bytes(it.Size)})? [y/N] ");
        if (Console.ReadLine()?.Trim().ToLowerInvariant() == "y") chosen.Add(it);
    }
    var res = reviewer.Remove(chosen);
    Console.WriteLine($"Freed {ReportWriter.Bytes(res.Sum(r => r.Bytes))}; {res.Count(r => r.Status != DeleteStatus.Recycled)} skipped.");
    return 0;
}

static async Task<int> RunStorage(string[] args)
{
    if (args.Length < 2) { Console.Error.WriteLine("Give a folder to analyze."); return 1; }
    int top = 10, threads = new StorageOptions().MaxParallel;
    for (int i = 2; i < args.Length - 1; i++)
    {
        if (args[i] == "--top") top = int.Parse(args[i + 1]);
        if (args[i] == "--threads") threads = int.Parse(args[i + 1]);
    }
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var r = await new StorageAnalyzer().AnalyzeAsync(args[1], new StorageOptions { MaxParallel = threads });
    Console.Error.WriteLine($"Scanned in {sw.Elapsed.TotalSeconds:F1}s with {threads} thread(s).");
    Console.WriteLine($"{args[1]}: {ReportWriter.Bytes(r.TotalBytes)} in {r.FileCount} files\n\nBy type:");
    foreach (var c in r.ByCategory) Console.WriteLine($"  {Bar(c.Bytes, r.TotalBytes)} {c.Name,-20} {ReportWriter.Bytes(c.Bytes)}");
    Console.WriteLine("\nBiggest folders:");
    foreach (var f in r.Root.Children.Take(top)) Console.WriteLine($"  {Bar(f.Bytes, r.TotalBytes)} {f.Name,-30} {ReportWriter.Bytes(f.Bytes)}");
    return 0;
}

static string Bar(long part, long whole) => new string('#', (int)(20.0 * part / Math.Max(1, whole))).PadRight(20, '.');

static async Task<int> RunEmpty(string[] args)
{
    if (args.Length < 2) { Console.Error.WriteLine("Give a folder to search."); return 1; }
    var finder = new EmptyFolderFinder(new ActivityLog());
    var found = await finder.ScanAsync(args[1]);
    foreach (var f in found) Console.WriteLine($"{f.Path}  (+{f.NestedEmpty} nested)");
    Console.WriteLine($"\n{found.Count} empty folders.");
    if (!args.Contains("--remove") || found.Count == 0) return 0;
    Console.Write("Send all of these to the Recycle Bin? [y/N] ");
    if (Console.ReadLine()?.Trim().ToLowerInvariant() != "y") return 0;
    var res = finder.Remove(found);
    Console.WriteLine($"Removed {res.Count(r => r.Status == EmptyFolderStatus.Removed)}; skipped {res.Count(r => r.Status != EmptyFolderStatus.Removed)}.");
    return 0;
}


static async Task<int> RunCaches(string[] args)
{
    bool apply = args.Contains("--apply");
    var cleaner = new CacheCleaner(new ActivityLog());
    var found = await cleaner.ScanAsync();
    foreach (var r in found)
        Console.WriteLine($"{ReportWriter.Bytes(r.Bytes),10}  {r.Target.Name,-22} {r.Files} files");
    Console.WriteLine($"\nTotal: {ReportWriter.Bytes(found.Sum(f => f.Bytes))}");
    if (!apply) { Console.WriteLine("Dry run. Re-run with --apply to clear these."); return 0; }
    long freed = 0;
    foreach (var r in found) freed += (await cleaner.ClearAsync(r.Target)).FreedBytes;
    Console.WriteLine($"Cleared {ReportWriter.Bytes(freed)}.");
    return 0;
}

static async Task<int> RunPhotos(string[] args)
{
    if (args.Length < 2) { Console.Error.WriteLine("Give a folder of photos."); return 1; }
    var opts = new PhotoScanOptions { MaxDistance = args.Contains("--strict") ? 2 : 6 };
    var progress = new Progress<PhotoProgress>(p => Console.Error.Write($"\rReading photos {p.Hashed}/{p.Found}   "));
    var r = await new SimilarPhotoFinder().ScanAsync(args[1], opts, progress);
    Console.Error.WriteLine();
    foreach (var g in r.Groups)
    {
        Console.WriteLine($"\n{g.Photos.Count} similar photos (could free {ReportWriter.Bytes(g.ReclaimableBytes)})");
        foreach (var p in g.Photos.OrderByDescending(p => p.Pixels))
            Console.WriteLine($"  {(p == g.SuggestedKeep ? "KEEP" : "    ")} {p.Width}x{p.Height} {ReportWriter.Bytes(p.Size),9}  {p.Path}");
    }
    Console.WriteLine($"\n{r.Groups.Count} groups among {r.PhotosScanned} photos, {ReportWriter.Bytes(r.ReclaimableBytes)} reclaimable.");
    return 0;
}

static int RunHistory()
{
    var log = new ActivityLog();
    var all = log.ReadAll();
    foreach (var e in all.Take(50))
        Console.WriteLine($"{e.TimeUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {e.Status,-16} {ReportWriter.Bytes(e.Bytes),9}  {e.Path}");
    Console.WriteLine(all.Count == 0 ? "Nothing has been removed yet." : $"\n{all.Count} entries. Restore with: dcleaner restore \"<path>\"");
    return 0;
}

static async Task<int> RunRestore(string[] args)
{
    if (args.Length < 2) { Console.Error.WriteLine("Give the full original path."); return 1; }
    var res = await RecycleBinRestorer.RestoreAsync(args[1]);
    Console.WriteLine($"{res.Status}{(res.Detail is null ? "" : ": " + res.Detail)}");
    return res.Status == RestoreStatus.Restored ? 0 : 1;
}



static async Task<int> RunOneDrive(string[] args)
{
    long minMb = 5;
    for (int i = 1; i < args.Length - 1; i++) if (args[i] == "--min-mb") minMb = long.Parse(args[i + 1]);
    var roots = OneDriveSpace.Roots();
    Console.WriteLine(roots.Count == 0 ? "OneDrive is not set up on this PC." : "OneDrive folders: " + string.Join(", ", roots));
    var files = await OneDriveSpace.FindFreeableAsync(null, minMb * 1024 * 1024);
    foreach (var f in files.Take(25)) Console.WriteLine($"{ReportWriter.Bytes(f.Size),10}  {f.Path}");
    Console.WriteLine($"\n{files.Count} files, {ReportWriter.Bytes(files.Sum(f => f.Size))} could be set to online-only.");
    return 0;
}

static int RunApps()
{
    foreach (var a in InstalledApps.List().Take(25)) Console.WriteLine($"{ReportWriter.Bytes(a.SizeBytes),10}  {a.Name}");
    return 0;
}





static async Task<int> RunTemp(string[] args)
{
    int days = 7;
    for (int i = 1; i < args.Length - 1; i++) if (args[i] == "--days") days = int.Parse(args[i + 1]);
    var cleaner = new TempCleaner(new ActivityLog());
    var scan = await cleaner.ScanAsync(days);
    Console.WriteLine($"Temp folder: {scan.Root}");
    foreach (var e in scan.Entries.Take(15))
        Console.WriteLine(e.EmptyFolder ? $"{"empty",10}  {e.Name}  (empty folder)" : $"{ReportWriter.Bytes(e.Bytes),10}  {e.Name}{(e.IsFolder ? "  (folder)" : "")}  ({e.FileCount} files)");
    if (scan.Entries.Count > 15) Console.WriteLine($"  ... and {scan.Entries.Count - 15} more items");
    Console.WriteLine($"\n{scan.FileCount} files and {scan.EmptyFolders} empty folders older than {days} days, {ReportWriter.Bytes(scan.TotalBytes)}.");
    if (scan.KeptNewer > 0) Console.WriteLine($"{scan.KeptNewer} newer files are left alone (use --days 0 to include them).");
    if (!args.Contains("--apply")) { Console.WriteLine("Dry run. Re-run with --apply to delete them for good (files in use are skipped)."); return 0; }
    var result = await cleaner.ClearAsync(scan.Entries, days);
    Console.WriteLine($"Removed {result.Deleted} items, freed {ReportWriter.Bytes(result.FreedBytes)}. {result.LeftAlone} left alone (in use or changed).");
    return 0;
}

static async Task<int> RunVideos(string[] args)
{
    long minMb = 0; string? type = null, folder = null; bool all = args.Contains("--all-drives");
    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] == "--min-mb" && i + 1 < args.Length) minMb = long.Parse(args[++i]);
        else if (args[i] == "--type" && i + 1 < args.Length) type = args[++i].ToLowerInvariant();
        else if (!args[i].StartsWith("--")) folder = args[i];
    }
    var roots = all ? VideoLocations.AllDrives() : [folder ?? VideoLocations.UserFolder];
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var scan = await new VideoFinder().ScanAsync(roots, all ? VideoLocations.SystemFolders() : null);
    var videos = scan.Videos.Where(v => v.Size >= minMb * 1024 * 1024 && (type is null || v.Extension == (type.StartsWith('.') ? type : "." + type))).ToList();
    Console.WriteLine($"Looked in {string.Join(", ", scan.Roots)} ({sw.Elapsed.TotalSeconds:F1}s)\n");
    foreach (var v in videos.Take(25)) Console.WriteLine($"{ReportWriter.Bytes(v.Size),10}  {v.Extension,-6} {v.Path}");
    if (videos.Count > 25) Console.WriteLine($"  ... and {videos.Count - 25} more");
    Console.WriteLine($"\n{videos.Count} videos, {ReportWriter.Bytes(videos.Sum(v => v.Size))}.");
    Console.WriteLine("By type: " + string.Join(", ", scan.ByExtension.Select(e => $"{e.Extension} {e.Count} ({ReportWriter.Bytes(e.Bytes)})")));
    if (scan.CloudOnlySkipped > 0) Console.WriteLine($"{scan.CloudOnlySkipped} online-only OneDrive videos were left out (they use no space on this PC).");
    return 0;
}



