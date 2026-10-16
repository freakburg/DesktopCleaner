using DesktopCleaner.Core.Caches;
using DesktopCleaner.Core.Deletion;
using Xunit;

namespace DesktopCleaner.Tests;

public sealed class CacheTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-cache").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    private CacheTarget Target(params string[] folders) =>
        new("t", "Test app", CacheGroup.Apps, "d", () => folders.Select(f => Path.Combine(_root, f)));

    private string Make(string rel, int size = 100)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new byte[size]);
        return p;
    }

    [Fact]
    public async Task Scan_reports_size_of_existing_folders_only_and_deletes_nothing()
    {
        var a = Make("cache/a.bin", 300); Make("cache/sub/b.bin", 200);
        var r = await new CacheCleaner().ScanAsync([Target("cache", "missing")]);
        var res = Assert.Single(r);
        Assert.Equal(500, res.Bytes);
        Assert.Equal(2, res.Files);
        Assert.True(File.Exists(a));
    }

    [Fact]
    public async Task Clear_deletes_outright_and_logs_Deleted()
    {
        var a = Make("cache/a.bin", 300);
        var logPath = Path.Combine(_root, "log.jsonl");
        var report = await new CacheCleaner(new ActivityLog(logPath)).ClearAsync(Target("cache"));
        Assert.False(File.Exists(a));
        Assert.Equal(300, report.FreedBytes);
        Assert.Equal(DeleteStatus.Deleted, report.Outcomes.Single().Status);
        Assert.Contains("Deleted", File.ReadAllText(logPath));
    }

    [Fact]
    public async Task Locked_cache_files_are_left_alone_and_reported_when_clearing()
    {
        var a = Make("cache/a.bin"); var b = Make("cache/b.bin");
        using var held = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.None);
        var report = await new CacheCleaner().ClearAsync(Target("cache"));
        Assert.True(File.Exists(a));
        Assert.False(File.Exists(b));
        Assert.Equal(1, report.Outcomes.Count(o => !o.Succeeded));
        Assert.Equal(100, report.FreedBytes);
    }
    [Fact]
    public void Catalog_never_points_at_personal_or_credential_folders()
    {
        foreach (var t in CacheCatalog.All)
            foreach (var f in t.ResolveFolders())
            {
                var leaf = Path.GetFileName(f);
                Assert.DoesNotContain(leaf, new[] { "Login Data", "Cookies", "History", "Local Storage", "Documents" });
                Assert.Null(DesktopCleaner.Core.Cleanup.RuleSafety.ValidateFolder(f));
            }
    }
}

