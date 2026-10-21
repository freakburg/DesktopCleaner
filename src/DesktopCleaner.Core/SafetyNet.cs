using System.Text.Json;

namespace DesktopCleaner.Core.Deletion;

public sealed record HeldItem(string Id, string OriginalPath, string HeldPath, bool IsFolder, long Bytes, DateTime HeldUtc, DateTime ExpiresUtc)
{
    public int DaysLeft(DateTime nowUtc) => Math.Max(0, (int)Math.Ceiling((ExpiresUtc - nowUtc).TotalDays));
}

public sealed record HoldPurgeResult(int Removed, long FreedBytes);

/// <summary>
/// A "safety net" for deleted files: instead of the Recycle Bin, items are moved into a hidden holding folder on the same drive
/// (an instant rename, even for huge files) and kept for a set number of days, then deleted for good. Restoring moves them back.
/// An index file remembers where everything came from. Unlike the Recycle Bin, emptying it by accident is not possible from Windows.
/// </summary>
public sealed class SafetyNet
{
    public const string HoldingFolderName = ".DesktopCleaner-Holding";
    private readonly object _gate = new();
    private readonly string _indexPath;
    private readonly Func<DateTime> _now;
    private readonly string? _fallbackRoot;
    private readonly Dictionary<string, HeldItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public int Days { get; }
    public static string DefaultIndexPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopCleaner", "holding.jsonl");

    /// <param name="fallbackRoot">Used when a drive's root folder cannot be written to; only valid for files on that same drive.</param>
    public SafetyNet(string? indexPath = null, int days = 30, Func<DateTime>? clock = null, string? fallbackRoot = null)
    {
        _indexPath = indexPath ?? DefaultIndexPath; Days = Math.Max(1, days); _now = clock ?? (() => DateTime.UtcNow); _fallbackRoot = fallbackRoot;
    }

    private sealed record Entry(string Op, HeldItem? Item, string? Id);

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
                if (e?.Op == "hold" && e.Item is not null) _items[e.Item.Id] = e.Item;
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

    /// <summary>Moves a file or folder into the holding area. Throws IOException if it cannot (the caller reports it as not removed).</summary>
    public HeldItem Hold(string path)
    {
        var full = Path.GetFullPath(path);
        bool isFolder = Directory.Exists(full);
        if (!isFolder && !File.Exists(full)) throw new FileNotFoundException("Nothing to hold.", full);
        long bytes = isFolder ? DirSize(full) : new FileInfo(full).Length;
        var id = Guid.NewGuid().ToString("N");
        var holdDir = Path.Combine(HoldingRootFor(full), id);
        Directory.CreateDirectory(holdDir);
        var dest = Path.Combine(holdDir, Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar)));
        if (isFolder) Directory.Move(full, dest); else File.Move(full, dest);
        var item = new HeldItem(id, full, dest, isFolder, bytes, _now(), _now().AddDays(Days));
        lock (_gate) { EnsureLoaded(); _items[id] = item; Append(new Entry("hold", item, null)); }
        return item;
    }

    private string HoldingRootFor(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath)!;
        var candidate = Path.Combine(root, HoldingFolderName);
        try
        {
            if (!Directory.Exists(candidate)) Directory.CreateDirectory(candidate).Attributes |= FileAttributes.Hidden;
            return candidate;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_fallbackRoot is not null && string.Equals(Path.GetPathRoot(Path.GetFullPath(_fallbackRoot)), root, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(_fallbackRoot);
                return _fallbackRoot;
            }
            throw new IOException($"Could not create the safety-net folder on drive {root}.", ex);
        }
    }

    /// <summary>Everything currently held, soonest-to-expire first.</summary>
    public IReadOnlyList<HeldItem> List() { lock (_gate) { EnsureLoaded(); return _items.Values.OrderBy(i => i.ExpiresUtc).ToList(); } }

    public HeldItem? FindByOriginalPath(string originalPath)
    {
        var full = Path.GetFullPath(originalPath);
        lock (_gate) { EnsureLoaded(); return _items.Values.Where(i => string.Equals(i.OriginalPath, full, StringComparison.OrdinalIgnoreCase)).OrderByDescending(i => i.HeldUtc).FirstOrDefault(); }
    }

    public RestoreResult Restore(string id)
    {
        HeldItem? item;
        lock (_gate) { EnsureLoaded(); _items.TryGetValue(id, out item); }
        if (item is null) return new(id, RestoreStatus.NotInRecycleBin, "That item is no longer in the safety net.");
        try
        {
            if (!(item.IsFolder ? Directory.Exists(item.HeldPath) : File.Exists(item.HeldPath)))
            {
                Forget(id);
                return new(item.OriginalPath, RestoreStatus.NotInRecycleBin, "The held copy has gone missing.");
            }
            if (File.Exists(item.OriginalPath) || Directory.Exists(item.OriginalPath))
                return new(item.OriginalPath, RestoreStatus.AlreadyExists, "Something with this name is already in that place.");
            Directory.CreateDirectory(Path.GetDirectoryName(item.OriginalPath)!);
            if (item.IsFolder) Directory.Move(item.HeldPath, item.OriginalPath); else File.Move(item.HeldPath, item.OriginalPath);
            TryDeleteDir(Path.GetDirectoryName(item.HeldPath)!);
            Forget(id);
            return new(item.OriginalPath, RestoreStatus.Restored);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(item.OriginalPath, RestoreStatus.Failed, ex.Message);
        }
    }

    /// <summary>Deletes held items for good: only the expired ones, or all of them with <paramref name="all"/>.</summary>
    public HoldPurgeResult Purge(bool all = false)
    {
        List<HeldItem> due;
        lock (_gate) { EnsureLoaded(); due = _items.Values.Where(i => all || i.ExpiresUtc <= _now()).ToList(); }
        int removed = 0; long freed = 0;
        foreach (var i in due)
        {
            try
            {
                if (i.IsFolder && Directory.Exists(i.HeldPath)) Directory.Delete(i.HeldPath, true);
                else if (File.Exists(i.HeldPath)) File.Delete(i.HeldPath);
                TryDeleteDir(Path.GetDirectoryName(i.HeldPath)!);
                Forget(i.Id); removed++; freed += i.Bytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* in use: try again next time */ }
        }
        return new(removed, freed);
    }

    private void Forget(string id) { lock (_gate) { EnsureLoaded(); if (_items.Remove(id)) Append(new Entry("remove", null, id)); } }

    private static void TryDeleteDir(string dir) { try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

    private static long DirSize(string dir)
    {
        long total = 0;
        foreach (var f in FsWalker.Files(dir, true, false)) try { total += f.Length; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return total;
    }
}
