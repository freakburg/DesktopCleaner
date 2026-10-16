using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopCleaner.Core.Cleanup;

/// <summary>A narrowly scoped rule: files in the given folders older than N days.</summary>
public sealed record CleanupRule
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Folders { get; init; }
    public int OlderThanDays { get; init; } = 14;
    /// <summary>Optional extensions (".tmp"); empty means any file.</summary>
    public IReadOnlyList<string> Extensions { get; init; } = [];
    public bool IncludeSubfolders { get; init; } = true;
    public bool Enabled { get; init; } = true;

    public static CleanupRule DefaultTemp() => new()
    {
        Name = "User temp files",
        Folders = [Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)],
        OlderThanDays = 14,
    };
}

public static class RuleStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopCleaner", "rules.json");

    public static IReadOnlyList<CleanupRule> Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return [CleanupRule.DefaultTemp()];
        return JsonSerializer.Deserialize<List<CleanupRule>>(File.ReadAllText(path), Json) ?? [];
    }

    public static void Save(IEnumerable<CleanupRule> rules, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(rules, Json));
    }
}
