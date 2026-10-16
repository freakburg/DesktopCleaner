using DesktopCleaner.Core.Deletion;
using DesktopCleaner.Core.Duplicates;
using Xunit;

namespace DesktopCleaner.Tests;

public sealed class DuplicateTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-test").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    private string Write(string name, string content)
    {
        var p = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public async Task Finds_identical_files_with_different_names()
    {
        Write("a.txt", "hello world"); Write("sub/b.txt", "hello world");
        Write("c.txt", "hello w0rld"); // same size, different content
        Write("d.txt", "unique");
        var r = await new DuplicateScanner().ScanAsync(_root);
        var g = Assert.Single(r.Groups);
        Assert.Equal(2, g.Files.Count);
        Assert.Equal(11, g.WastedBytes);
    }

    [Fact]
    public async Task Cancellation_throws()
    {
        Write("a.txt", "x"); Write("b.txt", "x");
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DuplicateScanner().ScanAsync(_root, ct: cts.Token));
    }

    [Fact]
    public async Task Remover_skips_changed_file_and_never_removes_last_copy()
    {
        var a = Write("a.txt", "same"); var b = Write("b.txt", "same");
        var group = Assert.Single((await new DuplicateScanner().ScanAsync(_root)).Groups);
        var recycled = new List<string>();
        var remover = new DuplicateRemover(null, recycled.Add);

        File.WriteAllText(b, "diff");
        var changed = await remover.RemoveAsync(group, [b]);
        Assert.Equal(DeleteStatus.SkippedChanged, changed[0].Status);

        var all = await remover.RemoveAsync(group, [a, b]);
        Assert.All(all, o => Assert.Equal(DeleteStatus.SkippedLastCopy, o.Status));
        Assert.Empty(recycled);
    }

    [Fact]
    public async Task Remover_recycles_verified_copy()
    {
        var a = Write("a.txt", "same"); var b = Write("b.txt", "same");
        var group = Assert.Single((await new DuplicateScanner().ScanAsync(_root)).Groups);
        var recycled = new List<string>();
        var res = await new DuplicateRemover(null, recycled.Add).RemoveAsync(group, [b]);
        Assert.Equal(DeleteStatus.Recycled, res[0].Status);
        Assert.Equal([b], recycled);
    }
}
