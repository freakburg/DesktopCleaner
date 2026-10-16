using DesktopCleaner.Core.Storage;
using Xunit;

namespace DesktopCleaner.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-st").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    private void Make(string rel, int size)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new byte[size]);
    }

    [Fact]
    public async Task Sums_folders_recursively_and_sorts_largest_first()
    {
        Make("a/big.mp4", 1000); Make("a/deep/x.mp4", 500); Make("b/small.txt", 10); Make("root.txt", 5);
        var r = await new StorageAnalyzer().AnalyzeAsync(_root);
        Assert.Equal(1515, r.TotalBytes);
        Assert.Equal(4, r.FileCount);
        Assert.Equal("a", r.Root.Children[0].Name);
        Assert.Equal(1500, r.Root.Children[0].Bytes);
    }

    [Fact]
    public async Task Deep_folders_still_count_toward_ancestors_when_not_kept()
    {
        Make("a/b/c/d.bin", 100);
        var r = await new StorageAnalyzer().AnalyzeAsync(_root, new StorageOptions { MaxDepth = 1 });
        Assert.Equal(100, r.TotalBytes);
        Assert.Empty(r.Root.Children[0].Children);
    }

    [Fact]
    public async Task Groups_extensions_into_friendly_categories()
    {
        Make("v.mp4", 300); Make("w.MKV", 200); Make("n.txt", 50); Make("q.weird", 7);
        var r = await new StorageAnalyzer().AnalyzeAsync(_root);
        Assert.Equal("Videos", r.ByCategory[0].Name);
        Assert.Equal(500, r.ByCategory[0].Bytes);
        Assert.Contains(r.ByCategory, c => c.Name == FileCategories.Other && c.Bytes == 7);
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        Make("a.txt", 1);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new StorageAnalyzer().AnalyzeAsync(_root, ct: cts.Token));
    }
}
