using DesktopCleaner.Core.Duplicates;

namespace DesktopCleaner.Core.Deletion;

public enum DeleteStatus { Recycled, Deleted, FreedFromPc, SkippedChanged, SkippedMissing, SkippedProtected, SkippedLastCopy, Failed }

public sealed record DeleteOutcome(string Path, DeleteStatus Status, long Bytes, string? Detail = null)
{
    /// <summary>True when the file is gone, either to the Recycle Bin or (for regenerable caches) deleted outright.</summary>
    public bool Succeeded => Status is DeleteStatus.Recycled or DeleteStatus.Deleted or DeleteStatus.FreedFromPc;
}

/// <summary>
/// Removes chosen duplicate copies safely: refuses protected folders, never deletes the last
/// surviving copy of a group, and re-verifies size/timestamp/hash right before recycling.
/// </summary>
public sealed class DuplicateRemover(ActivityLog? log = null, Action<string>? recycle = null)
{
    private readonly Action<string> _recycle = recycle ?? RecycleBin.Send;

    public async Task<IReadOnlyList<DeleteOutcome>> RemoveAsync(
        DuplicateGroup group, IEnumerable<string> pathsToRemove, CancellationToken ct = default)
    {
        var remove = new HashSet<string>(pathsToRemove, StringComparer.OrdinalIgnoreCase);
        var outcomes = new List<DeleteOutcome>();
        var survivors = group.Files.Count(f => !remove.Contains(f.Path));

        foreach (var entry in group.Files.Where(f => remove.Contains(f.Path)))
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await RemoveOneAsync(group, entry, survivors, ct);
            if (outcome.Status == DeleteStatus.Recycled) { /* survivors count already excludes removals */ }
            outcomes.Add(outcome);
            log?.Write(outcome);
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


