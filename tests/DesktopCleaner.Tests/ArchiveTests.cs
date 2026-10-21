using DesktopCleaner.Core.Archiving;
using DesktopCleaner.Core.Deletion;
using Xunit;

namespace DesktopCleaner.Tests;

public sealed class ArchiveTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-arc").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    private (string Path, long Size, DateTime When) Make(string rel, string content = "big video bytes", int ageDays = 5)
    {
        var p = Path.GetFullPath(Path.Combine(_root, "source", rel));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content); File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-ageDays));
        var fi = new FileInfo(p);
        return (p, fi.Length, fi.LastWriteTimeUtc);
    }

    private Archiver NewArchiver(ActivityLog? log = null, Func<string, string, bool>? shortcut = null) =>
        new(Path.Combine(_root, "idx.jsonl"), log, shortcut ?? ((lnk, target) => { File.WriteAllText(lnk, "SHORTCUT -> " + target); return true; }), requireDifferentDrive: false);

    private string Dest => Path.Combine(_root, "archive-drive");

    [Fact]
    public async Task Copies_verifies_leaves_a_shortcut_removes_the_original_and_logs()
    {
        var f = Make("clips/holiday.mp4", "important footage", 40);
        var logPath = Path.Combine(_root, "log.jsonl"); var reports = new List<ArchiveProgress>();
        var res = await NewArchiver(new ActivityLog(logPath)).ArchiveAsync([f], Dest, new Sync(reports));
        var o = res.Single();
        Assert.Equal(DeleteStatus.Archived, o.Status); Assert.Equal(f.Size, o.Bytes);
        Assert.False(File.Exists(f.Path));                                       // original gone...
        Assert.True(File.Exists(f.Path + ".lnk"));                                // ...shortcut in its place
        var archived = Archiver.DestinationFor(f.Path, Dest);
        Assert.Equal("important footage", File.ReadAllText(archived));
        Assert.Equal(f.When, File.GetLastWriteTimeUtc(archived));                 // modified time preserved
        Assert.False(File.Exists(archived + ".dc-partial"));
        Assert.Equal(f.Size, reports[^1].BytesDone);
        Assert.Contains("Archived", File.ReadAllText(logPath));
    }

    [Fact]
    public async Task Bring_back_restores_the_original_removes_shortcut_and_forgets_the_record()
    {
        var f = Make("a/b/movie.mkv", "movie data");
        var arc = NewArchiver();
        await arc.ArchiveAsync([f], Dest);
        var rec = Assert.Single(arc.List());
        Assert.Equal(f.Path, rec.OriginalPath);
        var res = await arc.RestoreAsync(rec.Id);
        Assert.Equal(RestoreStatus.Restored, res.Status);
        Assert.Equal("movie data", File.ReadAllText(f.Path));
        Assert.False(File.Exists(f.Path + ".lnk")); Assert.False(File.Exists(rec.ArchivedPath));
        Assert.Empty(NewArchiver().List());                                        // a new instance agrees: the index recorded the removal
    }

    [Fact]
    public async Task A_failed_shortcut_leaves_the_original_untouched_and_removes_the_copy()
    {
        var f = Make("x.mp4");
        var res = await NewArchiver(null, (_, _) => false).ArchiveAsync([f], Dest);
        Assert.Equal(DeleteStatus.Failed, res.Single().Status);
        Assert.True(File.Exists(f.Path));
        Assert.False(File.Exists(Archiver.DestinationFor(f.Path, Dest)));
    }

    [Fact]
    public async Task Changed_missing_and_conflicting_files_are_skipped_and_nothing_is_lost()
    {
        var changed = Make("c.mp4"); File.AppendAllText(changed.Path, "edited");
        var missing = Make("m.mp4"); File.Delete(missing.Path);
        var clash = Make("k.mp4"); File.WriteAllText(clash.Path + ".lnk", "already a shortcut here");
        var res = await NewArchiver().ArchiveAsync([changed, missing, clash], Dest);
        Assert.Equal([DeleteStatus.SkippedChanged, DeleteStatus.SkippedMissing, DeleteStatus.Failed], res.Select(r => r.Status));
        Assert.True(File.Exists(changed.Path)); Assert.True(File.Exists(clash.Path));
    }

    [Fact]
    public async Task Same_drive_is_refused_by_default_because_it_would_free_nothing()
    {
        var f = Make("same.mp4");
        var res = await new Archiver(Path.Combine(_root, "i2.jsonl"), null, (_, _) => true).ArchiveAsync([f], Dest);
        Assert.Equal(DeleteStatus.Failed, res.Single().Status);
        Assert.Contains("different drive", res.Single().Detail);
        Assert.True(File.Exists(f.Path));
    }

    [Fact]
    public void Destination_path_keeps_the_drive_letter_and_folders()
    {
        Assert.Equal(@"G:\Backup\Archived files\C\Users\me\Videos\a.mp4", Archiver.DestinationFor(@"C:\Users\me\Videos\a.mp4", @"G:\Backup"));
    }

    private sealed class Sync(List<ArchiveProgress> into) : IProgress<ArchiveProgress> { public void Report(ArchiveProgress v) => into.Add(v); }
}
