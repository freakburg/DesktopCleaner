using System.Text.Json;

namespace DesktopCleaner.Core.Deletion;

/// <summary>Append-only JSON-lines activity log of every deletion attempt.</summary>
public sealed class ActivityLog(string? filePath = null)
{
    private readonly object _gate = new();
    public string FilePath { get; } = filePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopCleaner", "activity.jsonl");

    public void Write(DeleteOutcome o)
    {
        var line = JsonSerializer.Serialize(new
        {
            timeUtc = DateTime.UtcNow, path = o.Path, status = o.Status.ToString(), bytes = o.Bytes, detail = o.Detail,
        });
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.AppendAllText(FilePath, line + Environment.NewLine);
        }
    }

    /// <summary>Every logged action, newest first. Lines that cannot be parsed are skipped.</summary>
    public IReadOnlyList<ActivityEntry> ReadAll()
    {
        if (!File.Exists(FilePath)) return [];
        string[] lines;
        lock (_gate) lines = File.ReadAllLines(FilePath);
        var entries = new List<ActivityEntry>();
        foreach (var line in lines)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                if (!Enum.TryParse<DeleteStatus>(r.GetProperty("status").GetString(), out var status)) continue;
                entries.Add(new ActivityEntry(
                    r.GetProperty("timeUtc").GetDateTime().ToUniversalTime(), r.GetProperty("path").GetString() ?? "", status,
                    r.TryGetProperty("bytes", out var b) ? b.GetInt64() : 0,
                    r.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null));
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { }
        }
        entries.Reverse();
        return entries;
    }
}

public sealed record ActivityEntry(DateTime TimeUtc, string Path, DeleteStatus Status, long Bytes, string? Detail)
{
    /// <summary>Only items that went to the Recycle Bin can be put back.</summary>
    public bool CanRestore => Status is DeleteStatus.Recycled or DeleteStatus.Archived;
    public bool Succeeded => Status is DeleteStatus.Recycled or DeleteStatus.Deleted or DeleteStatus.FreedFromPc or DeleteStatus.Linked or DeleteStatus.Archived;
}




