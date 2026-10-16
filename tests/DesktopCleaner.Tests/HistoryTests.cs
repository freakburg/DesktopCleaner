using System.Runtime.Versioning;
using DesktopCleaner.Core.Deletion;
using Xunit;

namespace DesktopCleaner.Tests;

[SupportedOSPlatform("windows")]
public sealed class HistoryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dc-hist").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void ReadAll_returns_newest_first_and_skips_garbage_lines()
    {
        var path = Path.Combine(_dir, "log.jsonl");
        var log = new ActivityLog(path);
        log.Write(new DeleteOutcome(@"C:\a.txt", DeleteStatus.Recycled, 10));
        File.AppendAllText(path, "this is not json\n");
        log.Write(new DeleteOutcome(@"C:\b.txt", DeleteStatus.Deleted, 20, "cache"));
        var all = log.ReadAll();
        Assert.Equal([@"C:\b.txt", @"C:\a.txt"], all.Select(e => e.Path));
        Assert.True(all[1].CanRestore);
        Assert.False(all[0].CanRestore);
        Assert.Equal("cache", all[0].Detail);
    }

    [Fact]
    public void ReadAll_on_missing_file_is_empty() => Assert.Empty(new ActivityLog(Path.Combine(_dir, "nope.jsonl")).ReadAll());

    /// <summary>Touches the real Recycle Bin, so it only runs when DC_INTEGRATION=1.</summary>
    [Fact]
    public async Task Real_roundtrip_recycle_then_restore()
    {
        if (Environment.GetEnvironmentVariable("DC_INTEGRATION") != "1") return;
        var path = Path.Combine(_dir, $"restore-me-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "hello");
        RecycleBin.Send(path);
        Assert.False(File.Exists(path));
        var res = await RecycleBinRestorer.RestoreAsync(path);
        Assert.Equal(RestoreStatus.Restored, res.Status);
        Assert.Equal("hello", File.ReadAllText(path));
        Assert.Equal(RestoreStatus.NotInRecycleBin, (await RecycleBinRestorer.RestoreAsync(Path.Combine(_dir, "never-existed.txt"))).Status);
    }
}
