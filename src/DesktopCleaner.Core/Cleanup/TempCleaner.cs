using DesktopCleaner.Core.Deletion;

namespace DesktopCleaner.Core.Cleanup;

/// <summary>One item sitting directly in the temp folder: a loose file, or a folder with everything old inside it.</summary>
public sealed record TempEntry(string Name, string Path, bool IsFolder, long Bytes, int FileCount, DateTime NewestUtc, IReadOnlyList<CleanupCandidate> Files);

public sealed record TempScan(string Root, int OlderThanDays, IReadOnlyList<TempEntry> Entries)
{
    public long TotalBytes => Entries.Sum(e => e.Bytes);
    public int FileCount => Entries.Sum(e => e.FileCount);
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
            .OrderByDescending(e => e.Bytes).ToList();
        return new TempScan(Root, olderThanDays, entries);
    }

    public async Task<TempClearResult> ClearAsync(IEnumerable<TempEntry> entries, int olderThanDays, CancellationToken ct = default)
    {
        var chosen = entries.ToList();
        var outcomes = await new CleanupEngine(log, delete, clock, permanent: true)
            .DeleteAsync(chosen.SelectMany(e => e.Files), olderThanDays, ct).ConfigureAwait(false);
        foreach (var folder in chosen.Where(e => e.IsFolder)) RemoveEmptyFolders(folder.Path);
        return new TempClearResult(outcomes);
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
