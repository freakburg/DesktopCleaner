using DesktopCleaner.Core.Cleanup;
using DesktopCleaner.Core.Deletion;
using Xunit;

namespace DesktopCleaner.Tests;

public sealed class TempCleanerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-temp").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    private string Make(string rel, int ageDays, int size = 100)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new byte[size]);
        File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-ageDays));
        return p;
    }

    private TempCleaner Cleaner(ActivityLog? log = null) => new(log, null, null, _root);

    [Fact]
    public async Task Scan_groups_by_top_level_item_and_only_lists_old_files()
    {
        Make("loose.tmp", 10, 50);
        Make("setup-xyz/a.dat", 10, 300); Make("setup-xyz/deep/b.dat", 10, 200);
        Make("fresh.tmp", 1); Make("mixed/old.dat", 10, 10); Make("mixed/new.dat", 0, 999);
        var scan = await Cleaner().ScanAsync(7);
        Assert.Equal(["setup-xyz", "loose.tmp", "mixed"], scan.Entries.Select(e => e.Name));
        var setup = scan.Entries[0];
        Assert.True(setup.IsFolder); Assert.Equal(500, setup.Bytes); Assert.Equal(2, setup.FileCount);
        Assert.False(scan.Entries.Single(e => e.Name == "loose.tmp").IsFolder);
        Assert.Equal(10, scan.Entries.Single(e => e.Name == "mixed").Bytes); // the new file is not counted
        Assert.Equal(560, scan.TotalBytes);
    }

    [Fact]
    public async Task Everything_option_includes_fresh_files()
    {
        Make("fresh.tmp", 0);
        Assert.Empty((await Cleaner().ScanAsync(7)).Entries);
        Assert.Single((await Cleaner().ScanAsync(0)).Entries);
    }

    [Fact]
    public async Task Clear_deletes_chosen_entries_logs_them_and_tidies_empty_folders_but_keeps_new_files()
    {
        var loose = Make("loose.tmp", 10);
        Make("gone/a.dat", 10); Make("gone/sub/b.dat", 10);
        var keepNew = Make("mixed/new.dat", 0); Make("mixed/old.dat", 10);
        var logPath = Path.Combine(_root, "log.jsonl");
        var cleaner = Cleaner(new ActivityLog(logPath));
        var scan = await cleaner.ScanAsync(7);

        var result = await cleaner.ClearAsync(scan.Entries.Where(e => e.Name != "loose.tmp"), 7);   // leave loose.tmp alone
        Assert.True(File.Exists(loose));
        Assert.False(Directory.Exists(Path.Combine(_root, "gone")));   // emptied folder removed too
        Assert.True(File.Exists(keepNew));                              // new file survives, so its folder stays
        Assert.False(File.Exists(Path.Combine(_root, "mixed", "old.dat")));
        Assert.Equal(3, result.Deleted); Assert.Equal(300, result.FreedBytes); Assert.Equal(0, result.LeftAlone);
        Assert.Equal(3, File.ReadAllLines(logPath).Count(l => l.Contains("Deleted")));
    }

    [Fact]
    public async Task Locked_files_are_left_alone_and_counted()
    {
        var a = Make("x/a.dat", 10); Make("x/b.dat", 10);
        var scan = await Cleaner().ScanAsync(7);
        using var held = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.None);
        var result = await Cleaner().ClearAsync(scan.Entries, 7);
        Assert.True(File.Exists(a)); Assert.False(File.Exists(Path.Combine(_root, "x", "b.dat")));
        Assert.Equal(1, result.LeftAlone); Assert.Equal(1, result.Deleted);
        Assert.True(Directory.Exists(Path.Combine(_root, "x")));  // still holds the locked file
    }

    [Fact]
    public async Task A_file_that_changed_after_the_scan_is_not_deleted()
    {
        var a = Make("x/a.dat", 10);
        var scan = await Cleaner().ScanAsync(7);
        File.WriteAllBytes(a, new byte[5]); // now new and a different size
        var result = await Cleaner().ClearAsync(scan.Entries, 7);
        Assert.True(File.Exists(a));
        Assert.Equal(0, result.Deleted);
    }

    private string MakeDir(string rel, int ageDays)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(p);
        Directory.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-ageDays));
        return p;
    }

    [Fact]
    public async Task Old_empty_folders_are_listed_and_new_ones_are_kept_until_Everything_is_chosen()
    {
        MakeDir("old-empty", 10); MakeDir("new-empty", 0); MakeDir("nested-empty/a/b", 10);
        Directory.SetLastWriteTimeUtc(Path.Combine(_root, "nested-empty"), DateTime.UtcNow.AddDays(-10));
        Make("has-files/x.dat", 10);
        var week = await Cleaner().ScanAsync(7);
        Assert.Equal(["nested-empty", "old-empty"], week.Entries.Where(e => e.EmptyFolder).Select(e => e.Name).Order());
        Assert.Equal(2, week.EmptyFolders);
        Assert.Equal(3, week.ItemCount);   // one file + two empty folders
        var all = await Cleaner().ScanAsync(0);
        Assert.Contains(all.Entries, e => e.Name == "new-empty" && e.EmptyFolder);
    }

    [Fact]
    public async Task Folder_with_a_file_anywhere_inside_is_not_called_empty()
    {
        MakeDir("tree", 10); Make("tree/sub/deep/new.dat", 0);   // the only file is new, so it is not a delete candidate either
        var scan = await Cleaner().ScanAsync(7);
        Assert.DoesNotContain(scan.Entries, e => e.EmptyFolder);
    }

    [Fact]
    public async Task Clearing_removes_empty_folders_reports_progress_and_skips_one_that_got_a_file()
    {
        var gone = MakeDir("gone-empty", 10); var refilled = MakeDir("refilled", 10);
        Make("old.dat", 10);
        var cleaner = Cleaner();
        var scan = await cleaner.ScanAsync(7);
        File.WriteAllBytes(Path.Combine(refilled, "late.dat"), new byte[3]);   // a program started using it after the scan
        var rec = new Recorder();
        var res = await cleaner.ClearAsync(scan.Entries, 7, default, rec);
        Assert.False(Directory.Exists(gone));
        Assert.True(File.Exists(Path.Combine(refilled, "late.dat")));
        Assert.Equal(1, res.LeftAlone);
        Assert.Equal(3, rec.Reports[^1].Total);                      // 1 file + 2 folders on one bar
        Assert.Equal(3, rec.Reports[^1].Done);
    }

    [Fact]
    public async Task Files_newer_than_the_limit_are_counted_so_the_UI_can_say_why_they_were_skipped()
    {
        Make("a.dat", 10); Make("b.dat", 0); Make("c.dat", 1);
        var scan = await Cleaner().ScanAsync(7);
        Assert.Equal(2, scan.KeptNewer);
    }
}
