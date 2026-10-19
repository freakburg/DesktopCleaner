using DesktopCleaner.Core.Cleanup;
using DesktopCleaner.Core.Deletion;

namespace DesktopCleaner.Core.Downloads;

[Flags]
public enum ReviewReason { None = 0, OldInstaller = 1, OldArchive = 2, Large = 4 }

public sealed record DownloadItem(string Path, long Size, DateTime LastWriteUtc, ReviewReason Reasons)
{
    public int AgeDays(DateTime nowUtc) => (int)(nowUtc - LastWriteUtc).TotalDays;
}

public sealed record DownloadsOptions
{
    public int OldAfterDays { get; init; } = 30;
    public long LargeThresholdBytes { get; init; } = 500L * 1024 * 1024;
    public IReadOnlyList<string> InstallerExtensions { get; init; } = [".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".iso"];
    public IReadOnlyList<string> ArchiveExtensions { get; init; } = [".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz"];

    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
}

/// <summary>
/// Flags downloads worth a second look. This is review-only: nothing is removed unless the
/// caller passes explicitly chosen items to <see cref="RemoveAsync"/>.
/// </summary>
public sealed class DownloadsReviewer(ActivityLog? log = null, Action<string>? recycle = null, Func<DateTime>? clock = null)
{
    private readonly Action<string> _recycle = recycle ?? RecycleBin.Send;
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);

    public Task<IReadOnlyList<DownloadItem>> ScanAsync(string? folder = null, DownloadsOptions? options = null, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<DownloadItem>>(() => Scan(folder ?? DownloadsOptions.DefaultFolder, options ?? new(), ct), ct);

    private List<DownloadItem> Scan(string folder, DownloadsOptions o, CancellationToken ct)
    {
        var items = new List<DownloadItem>();
        if (!Directory.Exists(folder)) return items;
        var cutoff = _now().AddDays(-o.OldAfterDays);
        var installers = Set(o.InstallerExtensions); var archives = Set(o.ArchiveExtensions);

        foreach (var fi in FsWalker.Files(folder, true, skipHiddenAndSystem: false, null, ct))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (CloudFiles.IsCloudOnly(fi.Attributes)) continue; // not on this PC, so removing it frees nothing
                var reasons = ReviewReason.None;
                var old = fi.LastWriteTimeUtc < cutoff;
                if (old && installers.Contains(fi.Extension)) reasons |= ReviewReason.OldInstaller;
                if (old && archives.Contains(fi.Extension)) reasons |= ReviewReason.OldArchive;
                if (fi.Length >= o.LargeThresholdBytes) reasons |= ReviewReason.Large;
                if (reasons != ReviewReason.None) items.Add(new DownloadItem(fi.FullName, fi.Length, fi.LastWriteTimeUtc, reasons));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* unreadable: leave it alone */ }
        }
        return items.OrderByDescending(i => i.Size).ToList();
    }

    /// <summary>Recycles user-approved items after re-checking they are unchanged.</summary>
    public IReadOnlyList<DeleteOutcome> Remove(IEnumerable<DownloadItem> chosen, IProgress<DeleteProgress>? progress = null)
    {
        var outcomes = new List<DeleteOutcome>();
        var items = chosen.ToList();
        foreach (var item in items)
        {
            DeleteOutcome o;
            try
            {
                var fi = new FileInfo(item.Path);
                if (ProtectedPaths.IsProtected(item.Path) || RuleSafety.ValidateFolder(Path.GetDirectoryName(item.Path)!) is not null)
                    o = new(item.Path, DeleteStatus.SkippedProtected, 0, "Protected or personal folder.");
                else if (!fi.Exists) o = new(item.Path, DeleteStatus.SkippedMissing, 0);
                else if (fi.Length != item.Size || fi.LastWriteTimeUtc != item.LastWriteUtc)
                    o = new(item.Path, DeleteStatus.SkippedChanged, 0, "File changed since scan.");
                else { _recycle(item.Path); o = new(item.Path, DeleteStatus.Recycled, item.Size); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                o = new(item.Path, DeleteStatus.Failed, 0, ex.Message);
            }
            outcomes.Add(o);
            log?.Write(o);
            progress?.Report(new DeleteProgress(outcomes.Count, items.Count, item.Path));
        }
        return outcomes;
    }

    private static HashSet<string> Set(IEnumerable<string> exts) =>
        exts.Select(e => e.StartsWith('.') ? e : "." + e).ToHashSet(StringComparer.OrdinalIgnoreCase);
}



