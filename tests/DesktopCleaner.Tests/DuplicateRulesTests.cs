using DesktopCleaner.Core.Deletion;
using DesktopCleaner.Core.Duplicates;
using Xunit;

namespace DesktopCleaner.Tests;

public sealed class DuplicateRulesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-dr").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    private string Make(string rel, string content = "same content", int ageDays = 0)
    {
        var p = Path.GetFullPath(Path.Combine(_root, rel));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-ageDays));
        return p;
    }

    private async Task<DuplicateGroup> GroupOf(params string[] _) => Assert.Single((await new DuplicateScanner().ScanAsync(_root)).Groups);

    [Fact]
    public async Task Keep_rules_pick_the_expected_copy()
    {
        var old = Make("archive/original.txt", ageDays: 30); var mid = Make("b/copy.txt", ageDays: 10); var nw = Make("some/deeper/folder/new.txt", ageDays: 1);
        var g = await GroupOf();
        Assert.Equal(old, KeepRules.Choose(g, KeepRule.Oldest).Path);
        Assert.Equal(nw, KeepRules.Choose(g, KeepRule.Newest).Path);
        Assert.Equal(mid.Length < old.Length ? mid : old, KeepRules.Choose(g, KeepRule.ShortestPath).Path);
        Assert.Equal(mid, KeepRules.Choose(g, KeepRule.PreferFolder, Path.Combine(_root, "b")).Path);
        Assert.Equal(old, KeepRules.Choose(g, KeepRule.PreferFolder, Path.Combine(_root, "nothing-here")).Path);   // falls back to the oldest
        Assert.Equal(mid, KeepRules.Choose(g, KeepRule.PreferFolder, Path.Combine(_root, "b") + Path.DirectorySeparatorChar).Path);
    }

    [Fact]
    public async Task PreferFolder_does_not_match_a_folder_that_only_shares_a_name_prefix()
    {
        var a = Make("photos/x.txt", ageDays: 5); var b = Make("photos-backup/x.txt", ageDays: 9);
        var g = await GroupOf();
        Assert.Equal(a, KeepRules.Choose(g, KeepRule.PreferFolder, Path.Combine(_root, "photos")).Path);
    }

    [Fact]
    public async Task Linking_makes_the_copy_the_same_file_keeps_its_name_and_logs_it()
    {
        var keep = Make("keep/a.txt", "payload", 20); var dup = Make("dup/a-copy.txt", "payload", 2);
        var g = await GroupOf(); var logPath = Path.Combine(_root, "log.jsonl"); var reports = new List<DeleteProgress>();
        var res = await new DuplicateLinker(new ActivityLog(logPath)).LinkAsync(g, keep, [dup], default, new Sync(reports));
        Assert.Equal(DeleteStatus.Linked, res.Single().Status);
        Assert.Equal(7, res.Single().Bytes);
        Assert.True(DuplicateLinker.IsSameFile(keep, dup));
        Assert.Equal("payload", File.ReadAllText(dup));
        Assert.False(File.Exists(dup + ".dc-old"));
        Assert.Equal([1], reports.Select(r => r.Done));
        Assert.Contains("Linked", File.ReadAllText(logPath));
        Assert.True(res.Single().Succeeded);
    }

    [Fact]
    public async Task A_failed_link_restores_the_original_file_untouched()
    {
        var keep = Make("keep/a.txt", "payload", 20); var dup = Make("dup/b.txt", "payload", 2);
        var g = await GroupOf();
        var res = await new DuplicateLinker(null, (_, _) => false).LinkAsync(g, keep, [dup]);
        Assert.Equal(DeleteStatus.Failed, res.Single().Status);
        Assert.Equal("payload", File.ReadAllText(dup));
        Assert.False(File.Exists(dup + ".dc-old"));
        Assert.False(DuplicateLinker.IsSameFile(keep, dup));
    }

    [Fact]
    public async Task Already_linked_changed_and_cross_drive_files_are_skipped_safely()
    {
        var keep = Make("keep/a.txt", "payload", 20); var dup = Make("dup/b.txt", "payload", 2);
        var g = await GroupOf();
        var linker = new DuplicateLinker();
        Assert.Equal(DeleteStatus.Linked, (await linker.LinkAsync(g, keep, [dup])).Single().Status);
        Assert.Equal(DeleteStatus.SkippedChanged, (await linker.LinkAsync(g, keep, [dup])).Single().Status);          // second time: already the same file

        var other = Make("c/c.txt", "payload", 3);
        var g2 = await GroupOf();
        File.WriteAllText(other, "edited!");
        Assert.Equal(DeleteStatus.SkippedChanged, (await linker.LinkAsync(g2, keep, [other])).Single().Status);
        Assert.Equal("edited!", File.ReadAllText(other));

        var farAway = new DuplicateGroup(g.Hash, g.FileSize, [.. g.Files, new FileEntry(@"Z:\nowhere\x.txt", g.FileSize, DateTime.UtcNow)]);
        Assert.Contains("same drive", (await linker.LinkAsync(farAway, keep, [@"Z:\nowhere\x.txt"])).Single().Detail);
    }

    private sealed class Sync(List<DeleteProgress> into) : IProgress<DeleteProgress> { public void Report(DeleteProgress v) => into.Add(v); }
}

