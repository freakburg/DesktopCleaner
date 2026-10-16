using System.Text.Json;

namespace DesktopCleaner.Core.Settings;

public sealed record AppSettings
{
    /// <summary>Show a notification during the weekly report if the system drive is running low.</summary>
    public bool LowDiskAlert { get; init; } = true;
    /// <summary>Alert when free space falls below this percentage of the drive.</summary>
    public int LowDiskPercent { get; init; } = 15;
    /// <summary>...or below this many gigabytes, whichever comes first.</summary>
    public int LowDiskGigabytes { get; init; } = 15;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopCleaner", "settings.json");

    public static AppSettings Load(string? path = null)
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path ?? DefaultPath)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed record DiskStatus(string Drive, long FreeBytes, long TotalBytes)
{
    public double FreePercent => TotalBytes == 0 ? 100 : 100.0 * FreeBytes / TotalBytes;
}

public static class DiskSpaceCheck
{
    public static DiskStatus? SystemDrive()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (root is null) return null;
            var d = new DriveInfo(root);
            return new DiskStatus(d.Name, d.AvailableFreeSpace, d.TotalSize);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    public static bool IsLow(DiskStatus s, AppSettings settings) =>
        settings.LowDiskAlert && (s.FreePercent < settings.LowDiskPercent || s.FreeBytes < settings.LowDiskGigabytes * 1024L * 1024 * 1024);
}
