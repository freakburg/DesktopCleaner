using DesktopCleaner.Core.Deletion;

namespace DesktopCleaner.Core.Cleanup;

/// <summary>
/// One item sitting directly in the temp folder: a loose file, a folder with everything old inside it, or a folder that is already
/// empty (programs often leave those behind). <see cref="EmptyFolder"/> entries hold no files at all.
/// </summary>
public sealed record TempEntry(string Name, string Path, bool IsFolder, long Bytes, int FileCount, DateTime NewestUtc, IReadOnlyList<CleanupCandidate> Files, bool EmptyFolder = false);

public sealed record TempScan(string Root, int OlderThanDays, IReadOnlyList<TempEntry> Entries, int KeptNewer = 0)
{
    public long TotalBytes => Entries.Sum(e => e.Bytes);
    public int FileCount => Entries.Sum(e => e.FileCount);
    public int EmptyFolders => Entries.Count(e => e.EmptyFolder);
    /// <summary>Everything the clean-up would remove: files plus empty folders.</summary>
    public int ItemCount => FileCount + EmptyFolders;
}

public sealed record TempClearResult(IReadOnlyList<DeleteOutcome> Outcomes)
{
    public long FreedBytes => Outcomes.Where(o => o.Succeeded).Sum(o => o.Bytes);
    public int Deleted => Outcomes.Count(o => o.Succeeded);
    /// <summary>Files left alone, usually because a program is using them right now.</summary>
    public int LeftAlone => Outcomes.Count(o => !o.Succeeded);
}

/// <summary>
/// Clears the user's temporary-files folder. Temp files are disposable by definition, and recycling thousands of them would
/// only move the space into the Recycle Bin, so (like Windows' own Disk Cleanup) they are deleted outright, one file at a time.
/// A file a program is using cannot be deleted and is simply left alone. Only files older than the chosen age are touched.
/// </summary>
public sealed class TempCleaner(ActivityLog? log = null, Action<string>? delete = null, Func<DateTime>? clock = null, string? tempRoot = null)
{
    public static string DefaultRoot => Path.TrimEndingDirectorySeparator(Path.GetTempPath());
    private string Root => Path.TrimEndingDirectorySeparator(tempRoot ?? DefaultRoot);

    public async Task<TempScan> ScanAsync(int olderThanDays, CancellationToken ct = default)
    {
        var rule = new CleanupRule { Name = "Temporary files", Folders = [Root], OlderThanDays = olderThanDays };
        var report = await new CleanupEngine(null, delete, clock, permanent: true).PreviewAsync(rule, ct).ConfigureAwait(false);

        var entries = report.Candidates
            .GroupBy(c => Path.GetRelativePath(Root, c.Path).Split(Path.DirectorySeparatorChar)[0], StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var path = Path.Combine(Root, g.Key);
                var isFolder = g.Any(c => !string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));
                return new TempEntry(g.Key, path, isFolder, g.Sum(c => c.Size), g.Count(), g.Max(c => c.LastWriteUtc), g.ToList());
            })
            .ToList();
        entries.AddRange(FindEmptyFolders(olderThanDays));
        return new TempScan(Root, olderThanDays, entries.OrderByDescending(e => e.Bytes).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(), report.KeptNewer);
    }

    private DateTime Now => clock?.Invoke() ?? DateTime.UtcNow;

    /// <summary>Folders directly inside temp that hold no files at any depth and have not been touched for the chosen time.</summary>
    private List<TempEntry> FindEmptyFolders(int olderThanDays)
    {
        var cutoff = Now.AddDays(-olderThanDays);
        var found = new List<TempEntry>();
        try
        {
            foreach (var dir in new DirectoryInfo(Root).EnumerateDirectories())
            {
                if (FsWalker.IsLink(dir) || dir.LastWriteTimeUtc >= cutoff || !HoldsNoFiles(dir.FullName)) continue;
                found.Add(new TempEntry(dir.Name, dir.FullName, true, 0, 0, dir.LastWriteTimeUtc, [], EmptyFolder: true));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* unreadable temp: report what we have */ }
        return found;
    }

    /// <summary>True only when we can see the whole tree and it contains no files. Anything unreadable counts as "has files".</summary>
    private static bool HoldsNoFiles(string dir)
    {
        try
        {
            if (Directory.EnumerateFiles(dir).Any()) return false;
            foreach (var sub in new DirectoryInfo(dir).EnumerateDirectories())
                if (FsWalker.IsLink(sub) || !HoldsNoFiles(sub.FullName)) return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public async Task<TempClearResult> ClearAsync(IEnumerable<TempEntry> entries, int olderThanDays, CancellationToken ct = default, IProgress<DeleteProgress>? progress = null)
    {
        var chosen = entries.ToList();
        var emptyFolders = chosen.Where(e => e.EmptyFolder).ToList();
        var files = chosen.SelectMany(e => e.Files).ToList();
        int total = files.Count + emptyFolders.Count;

        // one progress bar for files and folders together
        var relay = progress is null ? null : new Relay(p => progress.Report(new DeleteProgress(p.Done, total, p.Current)));
        var outcomes = (await new CleanupEngine(log, delete, clock, permanent: true).DeleteAsync(files, olderThanDays, ct, relay).ConfigureAwait(false)).ToList();

        foreach (var folder in chosen.Where(e => e.IsFolder && !e.EmptyFolder)) RemoveEmptyFolders(folder.Path);
        var cutoff = Now.AddDays(-olderThanDays);
        foreach (var f in emptyFolders)
        {
            ct.ThrowIfCancellationRequested();
            var o = RemoveEmptyEntry(f, cutoff);
            outcomes.Add(o);
            log?.Write(o);
            progress?.Report(new DeleteProgress(outcomes.Count, total, f.Path));
        }
        return new TempClearResult(outcomes);
    }

    /// <summary>Re-checks that the folder is still empty and still old before removing it (a program may have started using it).</summary>
    private DeleteOutcome RemoveEmptyEntry(TempEntry f, DateTime cutoff)
    {
        try
        {
            var dir = new DirectoryInfo(f.Path);
            if (!dir.Exists) return new(f.Path, DeleteStatus.SkippedMissing, 0);
            if (dir.LastWriteTimeUtc >= cutoff || !HoldsNoFiles(f.Path)) return new(f.Path, DeleteStatus.SkippedChanged, 0, "Folder is no longer empty.");
            Directory.Delete(f.Path, recursive: true);   // safe: we just confirmed the whole tree holds no files
            return new(f.Path, DeleteStatus.Deleted, 0, "empty folder");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(f.Path, DeleteStatus.Failed, 0, ex.Message);
        }
    }

    private sealed class Relay(Action<DeleteProgress> onReport) : IProgress<DeleteProgress>
    {
        public void Report(DeleteProgress value) => onReport(value);
    }

    /// <summary>Tidies up folders that were emptied by the cleanup. Never removes a folder that still holds anything.</summary>
    private static void RemoveEmptyFolders(string dir)
    {
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir)) if (!FsWalker.IsLink(new DirectoryInfo(sub))) RemoveEmptyFolders(sub);
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); // non-recursive: fails if anything is left
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* in use or not empty: leave it */ }
    }
}



