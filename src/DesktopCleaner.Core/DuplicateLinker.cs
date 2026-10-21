using System.Runtime.InteropServices;
using DesktopCleaner.Core.Deletion;

namespace DesktopCleaner.Core.Duplicates;

public enum KeepRule { Oldest, Newest, ShortestPath, PreferFolder }

public static class KeepRules
{
    /// <summary>Which copy of a duplicate group to keep. Ties fall back to the path so the answer is always the same.</summary>
    public static FileEntry Choose(DuplicateGroup group, KeepRule rule, string? preferredFolder = null)
    {
        IEnumerable<FileEntry> files = group.Files;
        if (rule == KeepRule.PreferFolder && !string.IsNullOrWhiteSpace(preferredFolder))
        {
            var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(preferredFolder)) + Path.DirectorySeparatorChar;
            var inside = group.Files.Where(f => f.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            if (inside.Count > 0) files = inside;          // otherwise nothing is in that folder: fall back to the oldest copy below
            else rule = KeepRule.Oldest;
        }
        return rule switch
        {
            KeepRule.Newest => files.OrderByDescending(f => f.LastWriteUtc).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).First(),
            KeepRule.ShortestPath => files.OrderBy(f => f.Path.Length).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).First(),
            _ => files.OrderBy(f => f.LastWriteUtc).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).First(),
        };
    }
}

/// <summary>
/// Frees the space of a duplicate WITHOUT deleting it: the extra copy becomes an NTFS hard link, a second name for the kept file's
/// data. Both paths keep working and nothing is lost, but they are now literally the same file (editing one changes the other),
/// and both must be on the same drive. Every step is re-verified and rolled back if anything fails.
/// </summary>
public sealed class DuplicateLinker(ActivityLog? log = null, Func<string, string, bool>? createLink = null)
{
    private readonly Func<string, string, bool> _createLink = createLink ?? CreateHardLink;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr securityAttributes);

    private static bool CreateHardLink(string newPath, string existingPath) => CreateHardLinkW(newPath, existingPath, IntPtr.Zero);

    public async Task<IReadOnlyList<DeleteOutcome>> LinkAsync(DuplicateGroup group, string keeperPath, IEnumerable<string> duplicatePaths,
        CancellationToken ct = default, IProgress<DeleteProgress>? progress = null)
    {
        var dups = duplicatePaths.Where(p => !string.Equals(Path.GetFullPath(p), Path.GetFullPath(keeperPath), StringComparison.OrdinalIgnoreCase)).ToList();
        var outcomes = new List<DeleteOutcome>();
        var keeper = Find(group, keeperPath);
        foreach (var path in dups)
        {
            ct.ThrowIfCancellationRequested();
            var o = await LinkOneAsync(group, keeper, path, ct).ConfigureAwait(false);
            outcomes.Add(o); log?.Write(o);
            progress?.Report(new DeleteProgress(outcomes.Count, dups.Count, path));
        }
        return outcomes;
    }

    private async Task<DeleteOutcome> LinkOneAsync(DuplicateGroup group, FileEntry? keeper, string path, CancellationToken ct)
    {
        var entry = Find(group, path);
        if (keeper is null || entry is null) return new(path, DeleteStatus.Failed, 0, "Not part of this group.");
        if (ProtectedPaths.IsProtected(path)) return new(path, DeleteStatus.SkippedProtected, 0, "Inside a protected folder.");
        if (!string.Equals(Path.GetPathRoot(Path.GetFullPath(path)), Path.GetPathRoot(Path.GetFullPath(keeper.Path)), StringComparison.OrdinalIgnoreCase))
            return new(path, DeleteStatus.Failed, 0, "A link needs both files on the same drive.");
        var backup = path + ".dc-old";
        bool movedAway = false;
        try
        {
            var fi = new FileInfo(path); var ki = new FileInfo(keeper.Path);
            if (!fi.Exists || !ki.Exists) return new(path, DeleteStatus.SkippedMissing, 0);
            if (fi.Length != entry.Size || fi.LastWriteTimeUtc != entry.LastWriteUtc || ki.Length != keeper.Size || ki.LastWriteTimeUtc != keeper.LastWriteUtc)
                return new(path, DeleteStatus.SkippedChanged, 0, "A file changed since the scan.");
            if (IsSameFile(path, keeper.Path)) return new(path, DeleteStatus.SkippedChanged, 0, "Already the same file (already linked).");
            if (await DuplicateScanner.HashFileAsync(path, null, ct).ConfigureAwait(false) != group.Hash ||
                await DuplicateScanner.HashFileAsync(keeper.Path, null, ct).ConfigureAwait(false) != group.Hash)
                return new(path, DeleteStatus.SkippedChanged, 0, "Content changed since the scan.");

            File.Move(path, backup); movedAway = true;
            if (!_createLink(path, keeper.Path))
            {
                File.Move(backup, path); movedAway = false;
                return new(path, DeleteStatus.Failed, 0, "This drive does not support links (it needs NTFS).");
            }
            File.Delete(backup); movedAway = false;
            return new(path, DeleteStatus.Linked, entry.Size, $"Now a link to {keeper.Path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (movedAway) { try { if (File.Exists(path)) File.Delete(path); File.Move(backup, path); } catch (Exception) { /* leave the backup next to it */ } }
            return new(path, DeleteStatus.Failed, 0, ex.Message);
        }
    }

    private static FileEntry? Find(DuplicateGroup group, string path) =>
        group.Files.FirstOrDefault(f => string.Equals(Path.GetFullPath(f.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));

    /// <summary>True when both paths already point at the same data on disk (the same volume serial and file index).</summary>
    public static bool IsSameFile(string a, string b)
    {
        var ia = Identity(a); var ib = Identity(b);
        return ia is not null && ia == ib;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes; public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, out ByHandleFileInformation info);

    private static (uint Volume, uint High, uint Low)? Identity(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return GetFileInformationByHandle(fs.SafeFileHandle, out var i) ? (i.VolumeSerialNumber, i.FileIndexHigh, i.FileIndexLow) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}




