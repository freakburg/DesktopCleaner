using System.Collections.Concurrent;
using DesktopCleaner.Core.Deletion;
using DesktopCleaner.Core.Storage;

namespace DesktopCleaner.Core.Videos;

public sealed record VideoFile(string Path, long Size, DateTime LastWriteUtc)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    /// <summary>Lower-case extension including the dot, e.g. ".mp4".</summary>
    public string Extension => System.IO.Path.GetExtension(Path).ToLowerInvariant();
}

public sealed record VideoProgress(long FilesSeen, int VideosFound, long VideoBytes, string CurrentFolder);

public sealed record VideoScan(IReadOnlyList<string> Roots, IReadOnlyList<VideoFile> Videos, long CloudOnlySkipped, IReadOnlyList<string> Issues)
{
    public long TotalBytes => Videos.Sum(v => v.Size);
    /// <summary>How many videos of each type, biggest total first. Drives the type filter.</summary>
    public IReadOnlyList<(string Extension, int Count, long Bytes)> ByExtension =>
        Videos.GroupBy(v => v.Extension).Select(g => (g.Key, g.Count(), g.Sum(v => v.Size))).OrderByDescending(x => x.Item3).ToList();
}

/// <summary>Where to look for videos.</summary>
public static class VideoLocations
{
    public static string UserFolder => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The root of every fixed (non-removable, non-network) drive that is ready.</summary>
    public static IReadOnlyList<string> AllDrives()
    {
        var roots = new List<string>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try { if (d.DriveType == DriveType.Fixed && d.IsReady) roots.Add(d.RootDirectory.FullName); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return roots;
    }

    /// <summary>
    /// Folders skipped when searching a whole drive. Videos in Windows, Program Files and ProgramData belong to the system
    /// and to installed programs (game intros, tutorials), they cannot be deleted from here anyway, and they would drown out your own files.
    /// </summary>
    public static IReadOnlyList<string> SystemFolders()
    {
        var list = new List<string>();
        foreach (var f in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData })
            if (Environment.GetFolderPath(f) is { Length: > 0 } p) list.Add(Path.TrimEndingDirectorySeparator(p));
        return list;
    }
}

/// <summary>
/// Finds every video file under the given folders, using all the machine's cores. Read-only: it never opens a file's contents,
/// skips shortcuts/junctions, leaves OneDrive online-only files alone (they take no space here), and reports unreadable folders instead of failing.
/// </summary>
public sealed class VideoFinder
{
    public async Task<VideoScan> ScanAsync(IEnumerable<string> roots, IEnumerable<string>? excludedFolders = null,
        IProgress<VideoProgress>? progress = null, CancellationToken ct = default)
    {
        var rootList = roots.Where(Directory.Exists).Select(r => Path.GetFullPath(r)).Select(r => r.EndsWith(Path.DirectorySeparatorChar) ? r : r + Path.DirectorySeparatorChar)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var excluded = (excludedFolders ?? []).Select(e => Path.TrimEndingDirectorySeparator(Path.GetFullPath(e))).ToList();
        var exts = FileCategories.VideoExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new ConcurrentBag<VideoFile>();
        var issues = new ConcurrentBag<string>();
        long seen = 0, cloudSkipped = 0, bytes = 0, count = 0;

        bool IsExcluded(string dir) => excluded.Any(e => string.Equals(dir, e, StringComparison.OrdinalIgnoreCase) ||
                                                         dir.StartsWith(e + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

        void ScanFolder(string dir, bool recurse)
        {
            foreach (var f in FsWalker.Files(dir, recurse, true, (p, m) => issues.Add($"{p}: {m}"), ct))
            {
                var n = Interlocked.Increment(ref seen);
                if (n % 2000 == 0) progress?.Report(new VideoProgress(n, (int)Interlocked.Read(ref count), Interlocked.Read(ref bytes), f.DirectoryName ?? ""));
                if (!exts.Contains(f.Extension)) continue;
                try
                {
                    if (IsExcluded(f.DirectoryName ?? "")) continue;
                    if (CloudFiles.IsCloudOnly(f.Attributes)) { Interlocked.Increment(ref cloudSkipped); continue; }
                    var v = new VideoFile(f.FullName, f.Length, f.LastWriteTimeUtc);
                    found.Add(v);
                    Interlocked.Increment(ref count); Interlocked.Add(ref bytes, v.Size);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { issues.Add($"{f.FullName}: {ex.Message}"); }
            }
        }

        await Task.Run(() =>
        {
            // Fan out over each root's top-level folders so a big drive uses several cores.
            var work = new List<(string Dir, bool Recurse)>();
            foreach (var root in rootList)
            {
                work.Add((root, false)); // loose files directly in the root
                try
                {
                    foreach (var sub in new DirectoryInfo(root).EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System }))
                        if (!FsWalker.IsLink(sub) && !IsExcluded(sub.FullName)) work.Add((sub.FullName, true));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { issues.Add($"{root}: {ex.Message}"); }
            }
            Parallel.ForEach(work, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8), CancellationToken = ct },
                w => ScanFolder(w.Dir, w.Recurse));
        }, ct).ConfigureAwait(false);

        progress?.Report(new VideoProgress(seen, (int)count, bytes, ""));
        // A video can be reached from two roots (for example C:\ and a folder inside it); keep each once.
        var videos = found.GroupBy(v => v.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderByDescending(v => v.Size).ToList();
        return new VideoScan(rootList, videos, cloudSkipped, issues.ToList());
    }
}

/// <summary>
/// Sends videos the user picked to the Recycle Bin. Each one is re-checked first (still there, same size and time) so a file that changed
/// after the scan is left alone. Only Windows and Program Files are off limits: the user chose these files one by one, so (unlike the
/// automatic cleanup rules) their own Videos, Desktop and OneDrive folders are allowed.
/// </summary>
public sealed class VideoRemover(ActivityLog? log = null, Action<string>? recycle = null)
{
    private readonly Action<string> _recycle = recycle ?? FileDisposal.Send;

    public IReadOnlyList<DeleteOutcome> Remove(IEnumerable<VideoFile> chosen, IProgress<DeleteProgress>? progress = null, CancellationToken ct = default)
    {
        var items = chosen.ToList();
        var outcomes = new List<DeleteOutcome>();
        foreach (var v in items)
        {
            ct.ThrowIfCancellationRequested();
            var o = RemoveOne(v);
            outcomes.Add(o);
            log?.Write(o);
            progress?.Report(new DeleteProgress(outcomes.Count, items.Count, v.Path));
        }
        return outcomes;
    }

    private DeleteOutcome RemoveOne(VideoFile v)
    {
        if (ProtectedPaths.IsProtected(v.Path)) return new(v.Path, DeleteStatus.SkippedProtected, 0, "Inside a system or program folder.");
        try
        {
            var fi = new FileInfo(v.Path);
            if (!fi.Exists) return new(v.Path, DeleteStatus.SkippedMissing, 0);
            if (fi.Length != v.Size || fi.LastWriteTimeUtc != v.LastWriteUtc) return new(v.Path, DeleteStatus.SkippedChanged, 0, "File changed since scan.");
            _recycle(v.Path);
            return new(v.Path, DeleteStatus.Recycled, v.Size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(v.Path, DeleteStatus.Failed, 0, ex.Message);
        }
    }
}


