using DesktopCleaner.Core.Deletion;
using DesktopCleaner.Core.Downloads;
using Xunit;

namespace DesktopCleaner.Tests;

public sealed class DownloadsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-dl").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    private string Make(string name, int ageDays, int size = 10)
    {
        var p = Path.Combine(_root, name);
        File.WriteAllBytes(p, new byte[size]);
        File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-ageDays));
        return p;
    }

    [Fact]
    public async Task Flags_old_installers_archives_and_large_files_only()
    {
        Make("setup.exe", 90); Make("new-setup.exe", 2); Make("bundle.zip", 90);
        Make("photo.jpg", 90); Make("huge.bin", 1, 5000);
        var items = await new DownloadsReviewer().ScanAsync(_root, new DownloadsOptions { LargeThresholdBytes = 1000 });
        var names = items.Select(i => Path.GetFileName(i.Path)).ToHashSet();
        Assert.Equal(new[] { "setup.exe", "bundle.zip", "huge.bin" }.ToHashSet(), names);
        Assert.Equal(ReviewReason.Large, items.Single(i => i.Path.EndsWith("huge.bin")).Reasons);
    }

    [Fact]
    public async Task Remove_skips_changed_files_and_recycles_unchanged()
    {
        var a = Make("a.exe", 90); var b = Make("b.exe", 90);
        var recycled = new List<string>();
        var rev = new DownloadsReviewer(null, recycled.Add);
        var items = await rev.ScanAsync(_root);
        File.WriteAllBytes(b, new byte[99]);
        var res = rev.Remove(items);
        Assert.Equal([a], recycled);
        Assert.Contains(res, r => r.Status == DeleteStatus.SkippedChanged);
    }
}
