using DesktopCleaner.Core.Deletion;

namespace DesktopCleaner.Core.EmptyFolders;

/// <summary>An empty folder (possibly holding only more empty folders). <see cref="NestedEmpty"/> counts the empty folders inside it.</summary>
public sealed record EmptyFolder(string Path, int NestedEmpty, DateTime LastWriteUtc);

public enum EmptyFolderStatus { Removed, SkippedNotEmpty, SkippedMissing, SkippedProtected, Failed }

public sealed record EmptyFolderOutcome(string Path, EmptyFolderStatus Status, string? Detail = null);

/// <summary>
/// Finds folders that hold no files at any depth. Only the top-most empty folders are reported, so
/// removing one covers everything inside it. Anything unreadable or linked counts as "not empty",
/// since we cannot be sure. Removal sends folders to the Recycle Bin after re-checking they are still empty.
/// </summary>
public sealed class EmptyFolderFinder(ActivityLog? log = null, Action<string>? recycle = null)
{
    private readonly Action<string> _recycle = recycle ?? RecycleBin.Send;

    private sealed record Node(DirectoryInfo Dir, bool IsEmpty, int EmptyCount, List<Node> Children);

    public Task<IReadOnlyList<EmptyFolder>> ScanAsync(string root, CancellationToken ct = default)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        return Task.Run<IReadOnlyList<EmptyFolder>>(() =>
        {
            var found = new List<EmptyFolder>();
            var top = Analyze(new DirectoryInfo(root), ct);
            // The folder the user picked is never itself a result; report what is inside it.
            if (top.IsEmpty) foreach (var c in top.Children) Collect(c, found);
            else CollectBelow(top, found);
            return found.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        }, ct);
    }

    private static Node Analyze(DirectoryInfo dir, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var children = new List<Node>();
        bool empty = true;
        try
        {
            if (dir.EnumerateFiles().Any()) empty = false;
            foreach (var sub in dir.EnumerateDirectories())
            {
                if (FsWalker.IsLink(sub)) { empty = false; continue; }
                var child = Analyze(sub, ct);
                children.Add(child);
                if (!child.IsEmpty) empty = false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { empty = false; }
        var count = children.Sum(c => c.EmptyCount) + (empty ? 1 : 0);
        return new Node(dir, empty, count, children);
    }

    private static void CollectBelow(Node node, List<EmptyFolder> found)
    {
        foreach (var c in node.Children) Collect(c, found);
    }

    private static void Collect(Node node, List<EmptyFolder> found)
    {
        if (node.IsEmpty) found.Add(new EmptyFolder(node.Dir.FullName, node.EmptyCount - 1, node.Dir.LastWriteTimeUtc));
        else CollectBelow(node, found);
    }

    public IReadOnlyList<EmptyFolderOutcome> Remove(IEnumerable<EmptyFolder> chosen, IProgress<DeleteProgress>? progress = null)
    {
        var results = new List<EmptyFolderOutcome>();
        var folders = chosen.ToList();
        foreach (var f in folders)
        {
            var o = RemoveOne(f.Path);
            results.Add(o);
            log?.Write(new DeleteOutcome(o.Path, o.Status switch
            {
                EmptyFolderStatus.Removed => DeleteStatus.Recycled,
                EmptyFolderStatus.SkippedNotEmpty => DeleteStatus.SkippedChanged,
                EmptyFolderStatus.SkippedMissing => DeleteStatus.SkippedMissing,
                EmptyFolderStatus.SkippedProtected => DeleteStatus.SkippedProtected,
                _ => DeleteStatus.Failed,
            }, 0, o.Detail ?? "empty folder"));
            progress?.Report(new DeleteProgress(results.Count, folders.Count, f.Path));
        }
        return results;
    }

    private EmptyFolderOutcome RemoveOne(string path)
    {
        try
        {
            var dir = new DirectoryInfo(path);
            if (!dir.Exists) return new(path, EmptyFolderStatus.SkippedMissing);
            if (IsProtected(path)) return new(path, EmptyFolderStatus.SkippedProtected, "System, drive or standard user folder.");
            if (!Analyze(dir, CancellationToken.None).IsEmpty) return new(path, EmptyFolderStatus.SkippedNotEmpty, "Folder is no longer empty.");
            _recycle(path);
            return new(path, EmptyFolderStatus.Removed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(path, EmptyFolderStatus.Failed, ex.Message);
        }
    }

    private static bool IsProtected(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (ProtectedPaths.IsProtected(full + Path.DirectorySeparatorChar)) return true;
        if (Path.GetPathRoot(full) is { } r && string.Equals(Path.TrimEndingDirectorySeparator(r), full, StringComparison.OrdinalIgnoreCase)) return true;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var standard = new[]
        {
            profile, Path.Combine(profile, "Downloads"), Path.Combine(profile, "AppData"),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        };
        return standard.Any(s => !string.IsNullOrEmpty(s) &&
            string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(s)), full, StringComparison.OrdinalIgnoreCase));
    }
}



