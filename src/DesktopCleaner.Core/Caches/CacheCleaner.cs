using DesktopCleaner.Core.Cleanup;
using DesktopCleaner.Core.Deletion;

namespace DesktopCleaner.Core.Caches;

public enum CacheGroup { Browsers, Apps, Creator, Developer, Windows }

/// <summary>A cache folder set that apps rebuild on their own. Folders are resolved on demand so missing apps are simply skipped.</summary>
public sealed record CacheTarget(string Id, string Name, CacheGroup Group, string Description, Func<IEnumerable<string>> ResolveFolders)
{
    public IReadOnlyList<string> ExistingFolders() => ResolveFolders().Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

public static class CacheCatalog
{
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    private static readonly string[] ChromiumCacheDirs = ["Cache", "Code Cache", "GPUCache"];

    /// <summary>Chromium browsers keep one cache set per profile ("Default", "Profile 1", ...). Passwords, history and logins live elsewhere and are never touched.</summary>
    private static IEnumerable<string> ChromiumFolders(string userDataDir)
    {
        if (!Directory.Exists(userDataDir)) yield break;
        foreach (var profile in Directory.EnumerateDirectories(userDataDir)
                     .Where(d => Path.GetFileName(d) is "Default" || Path.GetFileName(d).StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)))
            foreach (var c in ChromiumCacheDirs) yield return Path.Combine(profile, c);
    }

    public static IReadOnlyList<CacheTarget> All { get; } =
    [
        new("chrome", "Google Chrome", CacheGroup.Browsers, "Saved copies of web pages and images. Pages may load a little slower once.",
            () => ChromiumFolders(Path.Combine(Local, "Google", "Chrome", "User Data"))),
        new("edge", "Microsoft Edge", CacheGroup.Browsers, "Saved copies of web pages and images. Pages may load a little slower once.",
            () => ChromiumFolders(Path.Combine(Local, "Microsoft", "Edge", "User Data"))),
        new("brave", "Brave", CacheGroup.Browsers, "Saved copies of web pages and images. Pages may load a little slower once.",
            () => ChromiumFolders(Path.Combine(Local, "BraveSoftware", "Brave-Browser", "User Data"))),
        new("firefox", "Firefox", CacheGroup.Browsers, "Saved copies of web pages and images. Pages may load a little slower once.",
            () => Directory.Exists(Path.Combine(Local, "Mozilla", "Firefox", "Profiles"))
                ? Directory.EnumerateDirectories(Path.Combine(Local, "Mozilla", "Firefox", "Profiles")).Select(p => Path.Combine(p, "cache2"))
                : []),
        new("discord", "Discord", CacheGroup.Apps, "Images and files Discord saved. They re-download when you scroll chats.",
            () => ChromiumCacheDirs.Select(c => Path.Combine(Roaming, "discord", c))),
        new("steam-html", "Steam store cache", CacheGroup.Apps, "Pictures and pages from the Steam store and community.",
            () => [Path.Combine(Local, "Steam", "htmlcache")]),
        new("capcut", "CapCut", CacheGroup.Creator, "Preview and effect caches. CapCut rebuilds them when you open a project, which can take a moment.",
            () => [Path.Combine(Local, "CapCut", "User Data", "Cache")]),
        new("premiere", "Adobe Premiere / After Effects media cache", CacheGroup.Creator, "Conformed audio, peak files and preview media. Adobe regenerates them the next time a project is opened.",
            () => new[] { "Media Cache Files", "Media Cache", "Peak Files" }.Select(n => Path.Combine(Roaming, "Adobe", "Common", n))),
        new("unity-cache", "Unity package cache", CacheGroup.Creator, "Downloaded editor packages. Unity downloads them again when a project needs them.",
            () => [Path.Combine(Local, "Unity", "cache")]),
        new("gradle", "Gradle build cache", CacheGroup.Developer, "Downloaded libraries for Android and Java builds. Gradle downloads them again on the next build, so you need an internet connection.",
            () => [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gradle", "caches")]),
        new("crashdumps", "App crash reports", CacheGroup.Windows, "Memory dumps apps left behind after crashing. Only useful for debugging.",
            () => [Path.Combine(Local, "CrashDumps")]),
        new("nuget", "NuGet packages cache", CacheGroup.Developer, "Downloaded .NET packages. They re-download the next time a project needs them.",
            () => [Path.Combine(Local, "NuGet", "v3-cache")]),
        new("pip", "Python pip cache", CacheGroup.Developer, "Downloaded Python packages. They re-download when needed.",
            () => [Path.Combine(Local, "pip", "Cache")]),
        new("npm", "npm cache", CacheGroup.Developer, "Downloaded JavaScript packages. They re-download when needed.",
            () => [Path.Combine(Local, "npm-cache"), Path.Combine(Roaming, "npm-cache")]),
    ];

    /// <summary>A cleanup rule that matches every file in the target's folders. Caches are cleared regardless of age.</summary>
    public static CleanupRule RuleFor(CacheTarget target) => new()
    {
        Name = target.Name, Folders = target.ExistingFolders(), OlderThanDays = 0,
    };
}

public sealed record CacheScanResult(CacheTarget Target, RuleReport Report)
{
    public long Bytes => Report.RecoverableBytes;
    public int Files => Report.Candidates.Count;
}

/// <summary>Finds and clears app caches. Caches are deleted outright (not recycled): they rebuild themselves, and recycling would only move the space into the Recycle Bin.</summary>
public sealed class CacheCleaner(ActivityLog? log = null, Action<string>? delete = null)
{
    public async Task<IReadOnlyList<CacheScanResult>> ScanAsync(IEnumerable<CacheTarget>? targets = null, CancellationToken ct = default)
    {
        var engine = new CleanupEngine(log, delete, permanent: true);
        var results = new List<CacheScanResult>();
        foreach (var t in targets ?? CacheCatalog.All)
        {
            ct.ThrowIfCancellationRequested();
            var rule = CacheCatalog.RuleFor(t);
            if (rule.Folders.Count == 0) continue;
            var report = await engine.PreviewAsync(rule, ct).ConfigureAwait(false);
            if (report.Candidates.Count > 0 || report.Skipped.Count > 0) results.Add(new CacheScanResult(t, report));
        }
        return results.OrderByDescending(r => r.Bytes).ToList();
    }

    public async Task<RuleReport> ClearAsync(CacheTarget target, CancellationToken ct = default, IProgress<DeleteProgress>? progress = null) =>
        await new CleanupEngine(log, delete, permanent: true).RunAsync(CacheCatalog.RuleFor(target), ct, progress).ConfigureAwait(false);
}




