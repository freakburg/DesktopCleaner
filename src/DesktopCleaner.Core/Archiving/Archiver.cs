using System.Security.Cryptography;
using System.Text.Json;
using DesktopCleaner.Core.Deletion;

namespace DesktopCleaner.Core.Archiving;

public sealed record ArchivedFile(string Id, string OriginalPath, string ArchivedPath, string ShortcutPath, long Bytes, DateTime ArchivedUtc);

public sealed record ArchiveProgress(int FileNumber, int FileCount, long BytesDone, long BytesTotal, string Current);

/// <summary>
/// Moves big, rarely used files to another drive and leaves a shortcut behind, so they still open but stop using space on the
/// original drive. Each file is copied, then checked byte for byte (SHA-256) against the original, and only then is the original removed;
/// if anything fails the original stays exactly where it was. Everything archived is remembered, so "Bring back" restores it.
/// </summary>
public sealed class Archiver
{
    private readonly object _gate = new();
    private readonly string _indexPath;
    private readonly ActivityLog? _log;
    private readonly Func<string, string, bool> _createShortcut;
    private readonly Dictionary<string, ArchivedFile> _items = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public static string DefaultIndexPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopCleaner", "archive.jsonl");

    private readonly bool _requireDifferentDrive;

    /// <param name="requireDifferentDrive">Archiving to the same drive frees nothing, so it is refused. Tests turn this off.</param>
    public Archiver(string? indexPath = null, ActivityLog? log = null, Func<string, string, bool>? createShortcut = null, bool requireDifferentDrive = true)
    {
        _indexPath = indexPath ?? DefaultIndexPath; _log = log; _createShortcut = createShortcut ?? CreateShortcutFile; _requireDifferentDrive = requireDifferentDrive;
    }

    private sealed record Entry(string Op, ArchivedFile? Item, string? Id);

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        if (!File.Exists(_indexPath)) return;
        foreach (var line in File.ReadAllLines(_indexPath))
        {
            try
            {
                var e = JsonSerializer.Deserialize<Entry>(line);
                if (e?.Op == "add" && e.Item is not null) _items[e.Item.Id] = e.Item;
                else if (e?.Op == "remove" && e.Id is not null) _items.Remove(e.Id);
            }
            catch (JsonException) { }
        }
    }

    private void Append(Entry e)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_indexPath)!);
        File.AppendAllText(_indexPath, JsonSerializer.Serialize(e) + Environment.NewLine);
    }

    public IReadOnlyList<ArchivedFile> List() { lock (_gate) { EnsureLoaded(); return _items.Values.OrderByDescending(i => i.ArchivedUtc).ToList(); } }

    public ArchivedFile? FindByOriginalPath(string originalPath)
    {
        var full = Path.GetFullPath(originalPath);
        lock (_gate) { EnsureLoaded(); return _items.Values.Where(i => string.Equals(i.OriginalPath, full, StringComparison.OrdinalIgnoreCase)).OrderByDescending(i => i.ArchivedUtc).FirstOrDefault(); }
    }

    /// <summary>Where a file ends up under the destination: Archive\&lt;drive letter&gt;\&lt;original folders&gt;\&lt;name&gt;.</summary>
    public static string DestinationFor(string originalPath, string destinationFolder)
    {
        var full = Path.GetFullPath(originalPath);
        var root = Path.GetPathRoot(full)!;
        var drive = root.TrimEnd('\\', ':').Replace(":", "");
        return Path.Combine(Path.GetFullPath(destinationFolder), "Archived files", drive.Length > 0 ? drive : "share", full[root.Length..]);
    }

    public async Task<IReadOnlyList<DeleteOutcome>> ArchiveAsync(IEnumerable<(string Path, long Size, DateTime LastWriteUtc)> files, string destinationFolder,
        IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default)
    {
        var list = files.ToList();
        var outcomes = new List<DeleteOutcome>();
        long total = list.Sum(f => f.Size), done = 0;
        try { Directory.CreateDirectory(destinationFolder); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return list.Select(f => new DeleteOutcome(f.Path, DeleteStatus.Failed, 0, "Cannot use that destination: " + ex.Message)).ToList();
        }
        long free;
        try { free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(destinationFolder))!).AvailableFreeSpace; } catch (Exception) { free = long.MaxValue; }
        if (free < total) return list.Select(f => new DeleteOutcome(f.Path, DeleteStatus.Failed, 0, "Not enough free space on the destination drive.")).ToList();

        for (int i = 0; i < list.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var f = list[i];
            var o = await ArchiveOneAsync(f, destinationFolder, d => progress?.Report(new ArchiveProgress(i + 1, list.Count, done + d, total, f.Path)), ct).ConfigureAwait(false);
            if (o.Succeeded) done += f.Size;
            outcomes.Add(o); _log?.Write(o);
            progress?.Report(new ArchiveProgress(i + 1, list.Count, done, total, f.Path));
        }
        return outcomes;
    }

    private async Task<DeleteOutcome> ArchiveOneAsync((string Path, long Size, DateTime LastWriteUtc) f, string destFolder, Action<long> onBytes, CancellationToken ct)
    {
        var src = Path.GetFullPath(f.Path);
        if (ProtectedPaths.IsProtected(src)) return new(src, DeleteStatus.SkippedProtected, 0, "Inside a system or program folder.");
        if (_requireDifferentDrive && string.Equals(Path.GetPathRoot(src), Path.GetPathRoot(Path.GetFullPath(destFolder)), StringComparison.OrdinalIgnoreCase))
            return new(src, DeleteStatus.Failed, 0, "Choose a folder on a different drive, or nothing would be freed.");
        var dest = DestinationFor(src, destFolder); var temp = dest + ".dc-partial"; var shortcut = src + ".lnk";
        try
        {
            var fi = new FileInfo(src);
            if (!fi.Exists) return new(src, DeleteStatus.SkippedMissing, 0);
            if (fi.Length != f.Size || fi.LastWriteTimeUtc != f.LastWriteUtc) return new(src, DeleteStatus.SkippedChanged, 0, "File changed since scan.");
            if (File.Exists(dest) || File.Exists(shortcut)) return new(src, DeleteStatus.Failed, 0, "Something with this name already exists at the destination or next to the file.");

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            var srcHash = await CopyWithHashAsync(src, temp, onBytes, ct).ConfigureAwait(false);
            var destHash = await DuplicateScanner_Hash(temp, ct).ConfigureAwait(false);
            if (!string.Equals(srcHash, destHash, StringComparison.Ordinal))
            {
                File.Delete(temp);
                return new(src, DeleteStatus.Failed, 0, "The copy did not match the original, so nothing was moved.");
            }
            File.SetLastWriteTimeUtc(temp, f.LastWriteUtc);
            File.Move(temp, dest);

            // the original must still be the same file we copied
            var again = new FileInfo(src);
            if (!again.Exists || again.Length != f.Size || again.LastWriteTimeUtc != f.LastWriteUtc)
            {
                File.Delete(dest);
                return new(src, DeleteStatus.SkippedChanged, 0, "The file changed while it was being copied.");
            }
            if (!_createShortcut(shortcut, dest)) { File.Delete(dest); return new(src, DeleteStatus.Failed, 0, "Could not create the shortcut, so nothing was moved."); }
            File.Delete(src);                          // only now, with a verified copy and a working shortcut in place

            var rec = new ArchivedFile(Guid.NewGuid().ToString("N"), src, dest, shortcut, f.Size, DateTime.UtcNow);
            lock (_gate) { EnsureLoaded(); _items[rec.Id] = rec; Append(new Entry("add", rec, null)); }
            return new(src, DeleteStatus.Archived, f.Size, $"Moved to {dest}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
            return new(src, DeleteStatus.Failed, 0, ex.Message);
        }
    }

    /// <summary>Puts an archived file back where it was (verified), and removes the shortcut.</summary>
    public async Task<RestoreResult> RestoreAsync(string id, CancellationToken ct = default)
    {
        ArchivedFile? a;
        lock (_gate) { EnsureLoaded(); _items.TryGetValue(id, out a); }
        if (a is null) return new(id, RestoreStatus.NotInRecycleBin, "That file is not in the archive list.");
        try
        {
            if (!File.Exists(a.ArchivedPath)) { Forget(id); return new(a.OriginalPath, RestoreStatus.NotInRecycleBin, "The archived copy is no longer there."); }
            if (File.Exists(a.OriginalPath)) return new(a.OriginalPath, RestoreStatus.AlreadyExists, "A file with this name is already in that place.");
            Directory.CreateDirectory(Path.GetDirectoryName(a.OriginalPath)!);
            var temp = a.OriginalPath + ".dc-partial";
            var h1 = await CopyWithHashAsync(a.ArchivedPath, temp, _ => { }, ct).ConfigureAwait(false);
            if (h1 != await DuplicateScanner_Hash(temp, ct).ConfigureAwait(false)) { File.Delete(temp); return new(a.OriginalPath, RestoreStatus.Failed, "The restored copy did not match."); }
            File.SetLastWriteTimeUtc(temp, File.GetLastWriteTimeUtc(a.ArchivedPath));
            if (File.Exists(a.ShortcutPath)) File.Delete(a.ShortcutPath);
            File.Move(temp, a.OriginalPath);
            File.Delete(a.ArchivedPath);
            Forget(id);
            return new(a.OriginalPath, RestoreStatus.Restored);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(a.OriginalPath, RestoreStatus.Failed, ex.Message); }
    }

    private void Forget(string id) { lock (_gate) { EnsureLoaded(); if (_items.Remove(id)) Append(new Entry("remove", null, id)); } }

    private static async Task<string> CopyWithHashAsync(string from, string to, Action<long> onBytes, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        await using var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous | FileOptions.WriteThrough);
        var buf = new byte[1 << 20]; long copied = 0; int n;
        while ((n = await input.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            sha.TransformBlock(buf, 0, n, null, 0);
            await output.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            copied += n; onBytes(copied);
        }
        sha.TransformFinalBlock([], 0, 0);
        await output.FlushAsync(ct).ConfigureAwait(false);
        return Convert.ToHexString(sha.Hash!);
    }

    private static Task<string> DuplicateScanner_Hash(string path, CancellationToken ct) => Duplicates.DuplicateScanner.HashFileAsync(path, null, ct);

    /// <summary>A real Windows shortcut (.lnk) via the Windows scripting host.</summary>
    private static bool CreateShortcutFile(string lnkPath, string target)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return false;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic link = shell.CreateShortcut(lnkPath);
            link.TargetPath = target;
            link.Description = "Archived by Desktop Cleaner";
            link.Save();
            return File.Exists(lnkPath);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or IOException) { return false; }
    }
}


