using DesktopCleaner.Core.Duplicates;

namespace DesktopCleaner.Core.Deletion;

public enum DeleteStatus { Recycled, Deleted, FreedFromPc, Linked, Archived, SkippedChanged, SkippedMissing, SkippedProtected, SkippedLastCopy, Failed }

/// <summary>How far a removal has got, so the UI can show a progress bar. <paramref name="Current"/> is the file just handled.</summary>
public sealed record DeleteProgress(int Done, int Total, string? Current);

public sealed record DeleteOutcome(string Path, DeleteStatus Status, long Bytes, string? Detail = null)
{
    /// <summary>True when the file is gone, either to the Recycle Bin or (for regenerable caches) deleted outright.</summary>
    public bool Succeeded => Status is DeleteStatus.Recycled or DeleteStatus.Deleted or DeleteStatus.FreedFromPc or DeleteStatus.Linked or DeleteStatus.Archived;
}

/// <summary>
/// Removes chosen duplicate copies safely: refuses protected folders, never deletes the last
/// surviving copy of a group, and re-verifies size/timestamp/hash right before recycling.
/// </summary>
public sealed class DuplicateRemover(ActivityLog? log = null, Action<string>? recycle = null)
{
    private readonly Action<string> _recycle = recycle ?? FileDisposal.Send;

    public async Task<IReadOnlyList<DeleteOutcome>> RemoveAsync(
        DuplicateGroup group, IEnumerable<string> pathsToRemove, CancellationToken ct = default, IProgress<DeleteProgress>? progress = null)
    {
        var remove = new HashSet<string>(pathsToRemove, StringComparer.OrdinalIgnoreCase);
        var outcomes = new List<DeleteOutcome>();
        var survivors = group.Files.Count(f => !remove.Contains(f.Path));

        var targets = group.Files.Where(f => remove.Contains(f.Path)).ToList();
        int done = 0;
        foreach (var entry in targets)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await RemoveOneAsync(group, entry, survivors, ct);
            if (outcome.Status == DeleteStatus.Recycled) { /* survivors count already excludes removals */ }
            outcomes.Add(outcome);
            log?.Write(outcome);
            progress?.Report(new DeleteProgress(++done, targets.Count, entry.Path));
        }
        return outcomes;
    }

    private async Task<DeleteOutcome> RemoveOneAsync(DuplicateGroup group, FileEntry entry, int survivors, CancellationToken ct)
    {
        if (survivors < 1)
            return new(entry.Path, DeleteStatus.SkippedLastCopy, 0, "At least one copy must be kept.");
        if (ProtectedPaths.IsProtected(entry.Path))
            return new(entry.Path, DeleteStatus.SkippedProtected, 0, "Inside a protected folder.");

        try
        {
            var info = new FileInfo(entry.Path);
            if (!info.Exists) return new(entry.Path, DeleteStatus.SkippedMissing, 0);
            if (info.Length != entry.Size || info.LastWriteTimeUtc != entry.LastWriteUtc)
                return new(entry.Path, DeleteStatus.SkippedChanged, 0, "File changed since scan.");

            var hash = await DuplicateScanner.HashFileAsync(entry.Path, null, ct);
            if (!string.Equals(hash, group.Hash, StringComparison.OrdinalIgnoreCase))
                return new(entry.Path, DeleteStatus.SkippedChanged, 0, "Content changed since scan.");

            _recycle(entry.Path);
            return new(entry.Path, DeleteStatus.Recycled, entry.Size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(entry.Path, DeleteStatus.Failed, 0, ex.Message);
        }
    }
}











