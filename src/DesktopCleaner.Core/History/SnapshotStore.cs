using System.Text.Json;
using DesktopCleaner.Core.Storage;

namespace DesktopCleaner.Core.History;

public sealed record SpaceSnapshot(DateTime TimeUtc, string Root, long TotalBytes, long FileCount,
    Dictionary<string, long> Categories, Dictionary<string, long> Folders);

public sealed record FolderChange(string Name, long Delta);

public sealed record SpaceComparison(SpaceSnapshot Previous, long TotalDelta, IReadOnlyList<FolderChange> FolderChanges, IReadOnlyList<CategoryChange> CategoryChanges)
{
    public double DaysAgo(DateTime nowUtc) => (nowUtc - Previous.TimeUtc).TotalDays;
}

public sealed record CategoryChange(string Name, long Delta);

/// <summary>Remembers each storage check so the app can say what grew since last time.</summary>
public sealed class SnapshotStore(string? path = null, int maxEntries = 300)
{
    private readonly object _gate = new();
    public string FilePath { get; } = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopCleaner", "snapshots.jsonl");

    public static SpaceSnapshot From(StorageReport r, DateTime? nowUtc = null) => new(
        nowUtc ?? DateTime.UtcNow, r.Root.Path, r.TotalBytes, r.FileCount,
        r.ByCategory.ToDictionary(c => c.Name, c => c.Bytes),
        r.Root.Children.Take(30).ToDictionary(c => c.Name, c => c.Bytes, StringComparer.OrdinalIgnoreCase));

    public void Save(SpaceSnapshot s)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var lines = File.Exists(FilePath) ? File.ReadAllLines(FilePath).ToList() : [];
            lines.Add(JsonSerializer.Serialize(s));
            if (lines.Count > maxEntries) lines = lines.Skip(lines.Count - maxEntries).ToList();
            File.WriteAllLines(FilePath, lines);
        }
    }

    /// <summary>Snapshots of one folder, oldest first.</summary>
    public IReadOnlyList<SpaceSnapshot> For(string root)
    {
        if (!File.Exists(FilePath)) return [];
        string[] lines;
        lock (_gate) lines = File.ReadAllLines(FilePath);
        var list = new List<SpaceSnapshot>();
        foreach (var l in lines)
        {
            try
            {
                var s = JsonSerializer.Deserialize<SpaceSnapshot>(l);
                if (s is not null && string.Equals(s.Root, root, StringComparison.OrdinalIgnoreCase)) list.Add(s);
            }
            catch (JsonException) { }
        }
        return list.OrderBy(s => s.TimeUtc).ToList();
    }

    /// <summary>How <paramref name="current"/> differs from the snapshot before it. Null when there is nothing to compare with.</summary>
    public static SpaceComparison? Compare(SpaceSnapshot? previous, SpaceSnapshot current)
    {
        if (previous is null) return null;
        var folders = Diff(previous.Folders, current.Folders).Select(d => new FolderChange(d.Key, d.Value)).ToList();
        var cats = Diff(previous.Categories, current.Categories).Select(d => new CategoryChange(d.Key, d.Value)).ToList();
        return new SpaceComparison(previous, current.TotalBytes - previous.TotalBytes, folders, cats);
    }

    private static List<KeyValuePair<string, long>> Diff(Dictionary<string, long> before, Dictionary<string, long> after) =>
        before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(k => new KeyValuePair<string, long>(k, after.GetValueOrDefault(k) - before.GetValueOrDefault(k)))
            .Where(d => d.Value != 0).OrderByDescending(d => Math.Abs(d.Value)).ToList();
}
