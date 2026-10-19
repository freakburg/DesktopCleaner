using DesktopCleaner.Core.Cleanup;
using DesktopCleaner.Core.Deletion;
using DesktopCleaner.Core.Downloads;
using DesktopCleaner.Core.EmptyFolders;
using DesktopCleaner.Core.Duplicates;
using Xunit;

namespace DesktopCleaner.Tests;

/// <summary>A progress sink that records reports synchronously (Progress&lt;T&gt; would post them to another thread).</summary>
internal sealed class Recorder : IProgress<DeleteProgress>
{
    public List<DeleteProgress> Reports { get; } = [];
    public void Report(DeleteProgress value) => Reports.Add(value);
}

public sealed class DeleteProgressTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-prog").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    private string Make(string rel, int ageDays = 10, int size = 10)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new byte[size]);
        File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-ageDays));
        return p;
    }

    private static void AssertCountsUpToTotal(Recorder r, int total)
    {
        Assert.NotEmpty(r.Reports);
        Assert.All(r.Reports, p => Assert.Equal(total, p.Total));
        Assert.Equal(Enumerable.Range(1, total), r.Reports.Select(p => p.Done));   // 1, 2, 3 ... total, in order
    }

    [Fact]
    public async Task Duplicate_removal_reports_each_file()
    {
        Make("a/keep.txt", size: 20); Make("b/x.txt", size: 20); Make("c/y.txt", size: 20);
        var group = Assert.Single((await new DuplicateScanner().ScanAsync(_root)).Groups);
        var rec = new Recorder();
        await new DuplicateRemover(null, p => { }).RemoveAsync(group, group.Files.Skip(1).Select(f => f.Path), default, rec);
        AssertCountsUpToTotal(rec, 2);
    }

    [Fact]
    public async Task Downloads_removal_reports_each_file()
    {
        Make("a.exe", 90); Make("b.exe", 90); Make("c.exe", 90);
        var rev = new DownloadsReviewer(null, p => { });
        var items = await rev.ScanAsync(_root);
        var rec = new Recorder();
        rev.Remove(items, rec);
        AssertCountsUpToTotal(rec, 3);
        Assert.All(rec.Reports, p => Assert.EndsWith(".exe", p.Current));                // names the file just handled
    }

    [Fact]
    public async Task Empty_folder_removal_reports_each_folder()
    {
        Directory.CreateDirectory(Path.Combine(_root, "e1")); Directory.CreateDirectory(Path.Combine(_root, "e2"));
        var finder = new EmptyFolderFinder(null, p => Directory.Delete(p, true));
        var rec = new Recorder();
        finder.Remove(await finder.ScanAsync(_root), rec);
        AssertCountsUpToTotal(rec, 2);
    }

    [Fact]
    public async Task Temp_clearing_reports_progress_and_the_last_report_is_complete()
    {
        for (int i = 0; i < 12; i++) Make($"t/f{i}.dat");
        var cleaner = new TempCleaner(null, null, null, _root);
        var scan = await cleaner.ScanAsync(7);
        var rec = new Recorder();
        await cleaner.ClearAsync(scan.Entries, 7, default, rec);
        Assert.Equal(new DeleteProgress(12, 12, rec.Reports[^1].Current), rec.Reports[^1]);
        Assert.All(rec.Reports, p => Assert.InRange(p.Done, 1, 12));
        Assert.True(rec.Reports.Zip(rec.Reports.Skip(1)).All(pair => pair.Second.Done >= pair.First.Done));   // never goes backwards
    }

    [Fact]
    public async Task Huge_temp_clears_report_in_steps_not_once_per_file()
    {
        for (int i = 0; i < 1000; i++) Make($"big/f{i}.dat", size: 1);
        var cleaner = new TempCleaner(null, null, null, _root);
        var scan = await cleaner.ScanAsync(7);
        var rec = new Recorder();
        await cleaner.ClearAsync(scan.Entries, 7, default, rec);
        Assert.InRange(rec.Reports.Count, 100, 400);              // throttled
        Assert.Equal(1000, rec.Reports[^1].Done);                  // but always finishes at 100%
    }
}

