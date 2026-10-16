namespace DesktopCleaner.Core.Storage;

public sealed record CategoryStat(string Name, long Bytes, long Count);

/// <summary>Maps file extensions to plain-language categories an average user recognises.</summary>
public static class FileCategories
{
    public const string Other = "Other";

    private static readonly Dictionary<string, string[]> Map = new()
    {
        ["Videos"] = [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".m4v"],
        ["Pictures"] = [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".tif", ".tiff", ".raw", ".psd", ".svg"],
        ["Music & audio"] = [".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".wma"],
        ["Documents"] = [".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".rtf", ".odt", ".csv", ".md"],
        ["Zip & archives"] = [".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".iso"],
        ["Apps & installers"] = [".exe", ".msi", ".msix", ".appx", ".dll", ".sys", ".jar", ".apk"],
        ["Code & dev files"] = [".cs", ".js", ".ts", ".py", ".java", ".cpp", ".h", ".json", ".xml", ".html", ".css", ".obj", ".pdb", ".map"],
        ["Temporary & logs"] = [".tmp", ".temp", ".log", ".bak", ".old", ".dmp", ".cache"],
    };

    private static readonly Dictionary<string, string> Lookup = Map
        .SelectMany(kv => kv.Value.Select(e => (e, kv.Key))).ToDictionary(x => x.e, x => x.Key, StringComparer.OrdinalIgnoreCase);

    public static string For(string extension) => Lookup.TryGetValue(extension, out var c) ? c : Other;

    public static IReadOnlyList<CategoryStat> Group(IEnumerable<ExtensionStat> stats) =>
        stats.GroupBy(s => For(s.Extension))
             .Select(g => new CategoryStat(g.Key, g.Sum(s => s.Bytes), g.Sum(s => s.Count)))
             .OrderByDescending(c => c.Bytes).ToList();
}
