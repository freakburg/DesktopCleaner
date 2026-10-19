using DesktopCleaner.Core.Deletion;
using DesktopCleaner.Core.Storage;
using DesktopCleaner.Core.Videos;
using Xunit;

namespace DesktopCleaner.Tests;

public sealed class VideoTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-vid").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    private string Make(string rel, int size = 100)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new byte[size]);
        return p;
    }

    [Fact]
    public async Task Finds_every_video_type_case_insensitively_biggest_first_and_ignores_other_files()
    {
        Make("a.mp4", 300); Make("sub/b.MKV", 900); Make("sub/deep/c.mov", 100); Make("d.avi", 50); Make("e.mpeg", 10); Make("f.braw", 20);
        Make("notes.txt", 5000); Make("photo.jpg", 5000); Make("code.ts", 5000);   // .ts is TypeScript, not video
        var scan = await new VideoFinder().ScanAsync([_root]);
        Assert.Equal(["b.MKV", "a.mp4", "c.mov", "d.avi", "f.braw", "e.mpeg"], scan.Videos.Select(v => v.Name));
        Assert.Equal(1380, scan.TotalBytes);
        Assert.Equal(".mkv", scan.Videos[0].Extension);
        Assert.Contains(scan.ByExtension, x => x.Extension == ".mp4" && x.Count == 1 && x.Bytes == 300);
        Assert.Equal(6, scan.ByExtension.Count);
    }

    [Fact]
    public async Task Excluded_folders_are_skipped_but_siblings_are_not()
    {
        Make("keep/a.mp4"); Make("Program Files/game/intro.mp4"); Make("Program Files Backup/b.mp4");
        var scan = await new VideoFinder().ScanAsync([_root], [Path.Combine(_root, "Program Files")]);
        Assert.Equal(["a.mp4", "b.mp4"], scan.Videos.Select(v => v.Name).Order());
    }

    [Fact]
    public async Task Overlapping_roots_do_not_list_a_video_twice()
    {
        Make("sub/a.mp4");
        var scan = await new VideoFinder().ScanAsync([_root, Path.Combine(_root, "sub")]);
        Assert.Single(scan.Videos);
    }

    [Fact]
    public async Task Missing_roots_are_ignored_and_cancellation_is_honoured()
    {
        Make("a.mp4");
        Assert.Empty((await new VideoFinder().ScanAsync([Path.Combine(_root, "nope")])).Videos);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new VideoFinder().ScanAsync([_root], null, null, cts.Token));
    }

    [Fact]
    public async Task Video_extensions_match_the_Videos_category()
    {
        foreach (var e in FileCategories.VideoExtensions) Assert.Equal("Videos", FileCategories.For(e));
        Assert.Equal("Code & dev files", FileCategories.For(".ts"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Remover_recycles_unchanged_videos_logs_and_reports_progress_but_skips_changed_ones()
    {
        var a = Make("a.mp4", 100); var b = Make("b.mp4", 100);
        var scan = await new VideoFinder().ScanAsync([_root]);
        var recycled = new List<string>(); var reports = new List<DeleteProgress>();
        var logPath = Path.Combine(_root, "log.jsonl");
        File.AppendAllText(b, "more");   // b changes after the scan
        var res = new VideoRemover(new ActivityLog(logPath), recycled.Add).Remove(scan.Videos, new Sync(reports));
        Assert.Equal([a], recycled);
        Assert.Equal(DeleteStatus.SkippedChanged, res.Single(o => o.Path == b).Status);
        Assert.Equal([1, 2], reports.Select(r => r.Done));
        Assert.Equal(2, File.ReadAllLines(logPath).Length);
    }

    [Fact]
    public void Remover_refuses_system_folders()
    {
        var win = new VideoFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "x.mp4"), 1, DateTime.UtcNow);
        var res = new VideoRemover(null, _ => throw new InvalidOperationException("must not delete")).Remove([win]);
        Assert.Equal(DeleteStatus.SkippedProtected, res[0].Status);
    }

    [Fact]
    public void Drives_and_system_folder_lists_are_sensible()
    {
        Assert.NotEmpty(VideoLocations.AllDrives());
        Assert.Contains(VideoLocations.SystemFolders(), f => f.EndsWith("Windows", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class Sync(List<DeleteProgress> into) : IProgress<DeleteProgress> { public void Report(DeleteProgress v) => into.Add(v); }
}

