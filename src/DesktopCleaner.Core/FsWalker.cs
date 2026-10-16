namespace DesktopCleaner.Core;

/// <summary>
/// Safe file-tree walking that understands OneDrive. Cloud placeholders (files AND folders) carry the
/// reparse-point flag, so skipping every reparse point silently hides all of OneDrive. We skip only real
/// links (symlinks, junctions), which are the ones that can loop or double-count.
/// </summary>
public static class FsWalker
{
    public static bool IsLink(FileSystemInfo f) => (f.Attributes & FileAttributes.ReparsePoint) != 0 && f.LinkTarget is not null;

    /// <summary>All files under <paramref name="root"/>. Unreadable folders are reported through <paramref name="onIssue"/> and skipped.</summary>
    public static IEnumerable<FileInfo> Files(string root, bool recurse = true, bool skipHiddenAndSystem = true,
        Action<string, string>? onIssue = null, CancellationToken ct = default)
    {
        var skip = skipHiddenAndSystem ? FileAttributes.Hidden | FileAttributes.System : FileAttributes.System;
        var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = skip };
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            IEnumerator<FileInfo>? files = null;
            try { files = dir.EnumerateFiles("*", opts).GetEnumerator(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { onIssue?.Invoke(dir.FullName, ex.Message); }
            while (files is not null)
            {
                FileInfo f;
                try { if (!files.MoveNext()) break; f = files.Current; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { onIssue?.Invoke(dir.FullName, ex.Message); break; }
                if (!IsLinkSafe(f)) yield return f;
            }
            files?.Dispose();
            if (!recurse) continue;
            try { foreach (var sub in dir.EnumerateDirectories("*", opts)) if (!IsLinkSafe(sub)) pending.Push(sub); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { onIssue?.Invoke(dir.FullName, ex.Message); }
        }
    }

    private static bool IsLinkSafe(FileSystemInfo f)
    {
        try { return IsLink(f); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; } // cannot tell: leave it alone
    }
}

/// <summary>Cloud-sync (OneDrive) awareness: placeholders whose content is not on this PC, and OneDrive's "free up space" pin state.</summary>
public static class CloudFiles
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes Pinned = (FileAttributes)0x00080000;
    private const FileAttributes Unpinned = (FileAttributes)0x00100000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>True when the file's content lives only in the cloud: it takes no disk space, and reading it would download it.</summary>
    public static bool IsCloudOnly(FileAttributes a) => (a & (RecallOnDataAccess | RecallOnOpen | FileAttributes.Offline)) != 0;
    public static bool IsPinned(FileAttributes a) => (a & Pinned) != 0;
    public static bool IsUnpinned(FileAttributes a) => (a & Unpinned) != 0;
}


