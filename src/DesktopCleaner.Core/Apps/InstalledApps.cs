using Microsoft.Win32;

namespace DesktopCleaner.Core.Apps;

public sealed record InstalledApp(string Name, string? Publisher, string? Version, long SizeBytes, DateTime? InstalledOn, string? InstallLocation)
{
    public int? YearsSinceInstall(DateTime now) => InstalledOn is null ? null : (int)((now - InstalledOn.Value).TotalDays / 365.25);
}

/// <summary>
/// Lists installed desktop programs from the Windows uninstall registry keys, biggest first. Windows does not
/// record when a program was last used, so we show install date and size instead. We never uninstall anything:
/// the UI sends the user to Windows' own Installed apps page.
/// </summary>
public static class InstalledApps
{
    // The 32-bit and 64-bit registry views both use this path; opening each view reaches the WOW6432Node copy too.
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static IReadOnlyList<InstalledApp> List(Func<IEnumerable<IReadOnlyDictionary<string, object?>>>? source = null)
    {
        var apps = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in (source ?? ReadRegistry)())
        {
            var name = Str(v, "DisplayName");
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (Int(v, "SystemComponent") == 1 || !string.IsNullOrEmpty(Str(v, "ParentKeyName"))) continue; // hidden components, updates
            if (name.StartsWith("KB", StringComparison.Ordinal) && name.Length < 12) continue;
            if (name.Contains("Update for ", StringComparison.OrdinalIgnoreCase) || name.Contains("Security Update", StringComparison.OrdinalIgnoreCase)) continue;

            var version = Str(v, "DisplayVersion");
            var app = new InstalledApp(name.Trim(), Str(v, "Publisher"), version, Int(v, "EstimatedSize") * 1024L,
                ParseDate(Str(v, "InstallDate")), Str(v, "InstallLocation") is { Length: > 0 } loc ? loc.Trim('"') : null);
            var key = name.Trim() + "|" + version;
            if (!apps.TryGetValue(key, out var existing) || app.SizeBytes > existing.SizeBytes) apps[key] = app;
        }
        return apps.Values.OrderByDescending(a => a.SizeBytes).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Many installers leave EstimatedSize empty; measure the install folder for those (slow, so it is a separate step).</summary>
    public static Task<IReadOnlyList<InstalledApp>> FillMissingSizesAsync(IReadOnlyList<InstalledApp> apps, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<InstalledApp>>(() => apps.Select(a =>
        {
            ct.ThrowIfCancellationRequested();
            if (a.SizeBytes > 0 || a.InstallLocation is null || !Directory.Exists(a.InstallLocation)) return a;
            long total = 0;
            foreach (var f in FsWalker.Files(a.InstallLocation, true, false, null, ct))
                try { if (!CloudFiles.IsCloudOnly(f.Attributes)) total += f.Length; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return a with { SizeBytes = total };
        }).OrderByDescending(a => a.SizeBytes).ToList(), ct);

    private static IEnumerable<IReadOnlyDictionary<string, object?>> ReadRegistry()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        var hives = new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser };
        foreach (var hive in hives)
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var root = baseKey.OpenSubKey(UninstallPath);
                if (root is null) continue;
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(sub);
                    if (k is null) continue;
                    yield return k.GetValueNames().ToDictionary(n => n, n => k.GetValue(n), StringComparer.OrdinalIgnoreCase)!;
                }
            }
    }
    private static string? Str(IReadOnlyDictionary<string, object?> v, string k) => v.TryGetValue(k, out var o) ? o?.ToString() : null;
    private static long Int(IReadOnlyDictionary<string, object?> v, string k) => v.TryGetValue(k, out var o) && long.TryParse(o?.ToString(), out var n) ? n : 0;
    private static DateTime? ParseDate(string? s) =>
        DateTime.TryParseExact(s, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var d) ? d : null;
}

