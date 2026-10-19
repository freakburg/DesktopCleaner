using DesktopCleaner.Core.Deletion;

namespace DesktopCleaner.Core.Cleanup;

public sealed record CleanupCandidate(string Path, long Size, DateTime LastWriteUtc);

public sealed record RuleReport(
    CleanupRule Rule, IReadOnlyList<CleanupCandidate> Candidates,
    IReadOnlyList<string> Skipped, IReadOnlyList<DeleteOutcome> Outcomes)
{
    public long RecoverableBytes => Candidates.Sum(c => c.Size);
    /// <summary>Files left out only because they are newer than the age limit (they may still be in use).</summary>
    public int KeptNewer { get; init; }
    public long FreedBytes => Outcomes.Where(o => o.Succeeded).Sum(o => o.Bytes);
}

/// <summary>Finds (dry run) and optionally removes files matching cleanup rules.</summary>
/// <param name="permanent">Delete outright instead of using the Recycle Bin. Only meant for regenerable caches, where recycling would just move the space somewhere else.</param>
public sealed class CleanupEngine(ActivityLog? log = null, Action<string>? recycle = null, Func<DateTime>? clock = null, bool permanent = false)
{
    private readonly Action<string> _recycle = recycle ?? (permanent ? File.Delete : RecycleBin.Send);
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);

    /// <summary>Dry run: list what the rule would remove. Never modifies anything.</summary>
    public Task<RuleReport> PreviewAsync(CleanupRule rule, CancellationToken ct = default) =>
        Task.Run(() => Preview(rule, ct), ct);

    private RuleReport Preview(CleanupRule rule, CancellationToken ct)
    {
        var skipped = new List<string>();
        var found = new List<CleanupCandidate>();
        int keptNewer = 0;
        var cutoff = _now().AddDays(-rule.OlderThanDays);
        var exts = rule.Extensions.Select(e => e.StartsWith('.') ? e : "." + e).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in rule.Folders)
        {
            if (RuleSafety.ValidateFolder(folder) is { } problem) { skipped.Add($"{folder}: {problem}"); continue; }
            if (!Directory.Exists(folder)) continue;
            foreach (var fi in FsWalker.Files(folder, rule.IncludeSubfolders, skipHiddenAndSystem: false, (p, m) => skipped.Add($"{p}: {m}"), ct))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (CloudFiles.IsCloudOnly(fi.Attributes)) continue;
                    if (fi.LastWriteTimeUtc >= cutoff) { keptNewer++; continue; }
                    if (exts.Count > 0 && !exts.Contains(fi.Extension)) continue;
                    // Opening every file just to test for locks is slow on huge cache trees (and antivirus scans each open),
                    // so permanent-delete mode finds locked files when it actually tries to delete them.
                    if (!permanent && IsInUse(fi.FullName)) { skipped.Add($"{fi.FullName}: in use"); continue; }
                    found.Add(new CleanupCandidate(fi.FullName, fi.Length, fi.LastWriteTimeUtc));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped.Add($"{fi.FullName}: {ex.Message}");
                }
            }
        }
        return new RuleReport(rule, found, skipped, []) { KeptNewer = keptNewer };
    }

    /// <summary>Applies the rule: re-checks each file right before sending it to the Recycle Bin.</summary>
    public async Task<RuleReport> RunAsync(CleanupRule rule, CancellationToken ct = default, IProgress<DeleteProgress>? progress = null)
    {
        var preview = await PreviewAsync(rule, ct).ConfigureAwait(false);
        var cutoff = _now().AddDays(-rule.OlderThanDays);
        var outcomes = new List<DeleteOutcome>();
        foreach (var c in preview.Candidates)
        {
            ct.ThrowIfCancellationRequested();
            var o = Remove(c, cutoff);
            outcomes.Add(o);
            log?.Write(o);
            Report(progress, outcomes.Count, preview.Candidates.Count, c.Path);
        }
        return preview with { Outcomes = outcomes };
    }

    /// <summary>Removes just the given files, each re-checked first (still there, unchanged, still older than <paramref name="olderThanDays"/>).</summary>
    public Task<IReadOnlyList<DeleteOutcome>> DeleteAsync(IEnumerable<CleanupCandidate> items, int olderThanDays, CancellationToken ct = default, IProgress<DeleteProgress>? progress = null) =>
        Task.Run<IReadOnlyList<DeleteOutcome>>(() =>
        {
            var cutoff = _now().AddDays(-olderThanDays);
            var outcomes = new List<DeleteOutcome>();
            var list = items.ToList();
            foreach (var c in list)
            {
                ct.ThrowIfCancellationRequested();
                var o = Remove(c, cutoff);
                outcomes.Add(o);
                log?.Write(o);
                Report(progress, outcomes.Count, list.Count, c.Path);
            }
            return outcomes;
        }, ct);

    /// <summary>Temp folders can hold tens of thousands of tiny files; reporting every one would flood the UI thread, so report in steps.</summary>
    private static void Report(IProgress<DeleteProgress>? progress, int done, int total, string path)
    {
        if (progress is null) return;
        if (done == total || done % Math.Max(1, total / 200) == 0) progress.Report(new DeleteProgress(done, total, path));
    }

    private DeleteOutcome Remove(CleanupCandidate c, DateTime cutoff)
    {
        try
        {
            var fi = new FileInfo(c.Path);
            if (!fi.Exists) return new(c.Path, DeleteStatus.SkippedMissing, 0);
            if (fi.Length != c.Size || fi.LastWriteTimeUtc != c.LastWriteUtc || fi.LastWriteTimeUtc >= cutoff)
                return new(c.Path, DeleteStatus.SkippedChanged, 0, "File changed since scan.");
            if (!permanent && IsInUse(c.Path)) return new(c.Path, DeleteStatus.Failed, 0, "In use.");
            _recycle(c.Path);
            return new(c.Path, permanent ? DeleteStatus.Deleted : DeleteStatus.Recycled, c.Size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(c.Path, DeleteStatus.Failed, 0, ex.Message);
        }
    }

    /// <summary>A file is "in use" if we can't open it for exclusive access.</summary>
    private static bool IsInUse(string path)
    {
        try { using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }
}











