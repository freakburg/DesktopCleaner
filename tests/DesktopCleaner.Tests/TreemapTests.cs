using DesktopCleaner.Core.Storage;
using Xunit;

namespace DesktopCleaner.Tests;

public sealed class TreemapAndTopFilesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-tm").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Tiles_have_area_proportional_to_size_and_fill_the_space()
    {
        var items = new[] { 600L, 300, 150, 100, 50, 30, 20 }.Select((s, i) => new TreemapItem("k" + i, s)).ToList();
        var rects = Treemap.Layout(items, 400, 250);
        Assert.Equal(items.Count, rects.Count);
        Assert.Equal(400 * 250, rects.Sum(r => r.Width * r.Height), 3);
        double total = items.Sum(i => (double)i.Size);
        foreach (var r in rects) Assert.Equal(r.Size / total * 400 * 250, r.Width * r.Height, 3);
    }

    [Fact]
    public void Tiles_stay_inside_bounds_and_do_not_overlap()
    {
        var items = Enumerable.Range(1, 30).Select(i => new TreemapItem("k" + i, i * i * 7)).ToList();
        var rects = Treemap.Layout(items, 500, 300);
        foreach (var r in rects)
        {
            Assert.InRange(r.X, -1e-6, 500 + 1e-6); Assert.InRange(r.Y, -1e-6, 300 + 1e-6);
            Assert.True(r.X + r.Width <= 500 + 1e-6 && r.Y + r.Height <= 300 + 1e-6);
        }
        for (int i = 0; i < rects.Count; i++)
            for (int j = i + 1; j < rects.Count; j++)
            {
                var a = rects[i]; var b = rects[j];
                double ox = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
                double oy = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
                Assert.False(ox > 1e-6 && oy > 1e-6, $"{a.Key} overlaps {b.Key}");
            }
    }

    [Fact]
    public void Zero_sizes_and_empty_input_are_handled()
    {
        Assert.Empty(Treemap.Layout([], 100, 100));
        Assert.Empty(Treemap.Layout([new TreemapItem("a", 0)], 100, 100));
        Assert.Single(Treemap.Layout([new TreemapItem("a", 5), new TreemapItem("b", 0)], 100, 100));
    }

    [Fact]
    public async Task Largest_files_are_returned_biggest_first_and_capped()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        for (int i = 1; i <= 8; i++) File.WriteAllBytes(Path.Combine(_root, i % 2 == 0 ? "sub" : "", $"f{i}.bin"), new byte[i * 100]);
        var r = await new StorageAnalyzer().AnalyzeAsync(_root, new StorageOptions { TopFileCount = 3 });
        Assert.Equal([800L, 700, 600], r.LargestFiles.Select(f => f.Size));
        Assert.Equal("f8.bin", r.LargestFiles[0].Name);
    }

    [Fact]
    public async Task Small_folders_are_folded_into_OtherBytes()
    {
        File.WriteAllBytes(Path.Combine(_root, "big.bin"), new byte[10_000]);
        Directory.CreateDirectory(Path.Combine(_root, "tiny"));
        File.WriteAllBytes(Path.Combine(_root, "tiny", "t.bin"), new byte[5]);
        var r = await new StorageAnalyzer().AnalyzeAsync(_root);
        Assert.Empty(r.Root.Children);
        Assert.Equal(5, r.Root.OtherBytes);
        Assert.Equal(10_005, r.TotalBytes);
    }
}
