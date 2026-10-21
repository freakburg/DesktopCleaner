using DesktopCleaner.Core.Deletion;
using DesktopCleaner.Core.Settings;
using Xunit;

namespace DesktopCleaner.Tests;

public sealed class SafetyNetTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-net").FullName;
    private DateTime _now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    public void Dispose() => Directory.Delete(_root, true);

    private SafetyNet Net(int days = 30) => new(Path.Combine(_root, "idx.jsonl"), days, () => _now, Path.Combine(_root, "holding"));

    private string Make(string rel, string content = "data")
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void Hold_moves_the_file_away_and_Restore_puts_it_back_with_its_content()
    {
        var f = Make("docs/a.txt", "hello");
        var net = Net();
        var item = net.Hold(f);
        Assert.False(File.Exists(f));
        Assert.True(File.Exists(item.HeldPath));
        Assert.Equal(30, item.DaysLeft(_now));
        var res = net.Restore(item.Id);
        Assert.Equal(RestoreStatus.Restored, res.Status);
        Assert.Equal("hello", File.ReadAllText(f));
        Assert.Empty(net.List());
    }

    [Fact]
    public void Folders_are_held_and_restored_whole()
    {
        Make("proj/sub/x.dat", "1"); Make("proj/y.dat", "22");
        var net = Net();
        var item = net.Hold(Path.Combine(_root, "proj"));
        Assert.True(item.IsFolder); Assert.Equal(3, item.Bytes);
        Assert.False(Directory.Exists(Path.Combine(_root, "proj")));
        net.Restore(item.Id);
        Assert.True(File.Exists(Path.Combine(_root, "proj", "sub", "x.dat")));
    }

    [Fact]
    public void Restore_refuses_to_overwrite_something_that_took_its_place()
    {
        var f = Make("a.txt", "old");
        var net = Net(); var item = net.Hold(f);
        File.WriteAllText(f, "new");
        Assert.Equal(RestoreStatus.AlreadyExists, net.Restore(item.Id).Status);
        Assert.Equal("new", File.ReadAllText(f));
        Assert.Single(net.List());
    }

    [Fact]
    public void Purge_deletes_only_expired_items_unless_asked_for_all()
    {
        var a = Make("a.bin", "aaaa"); var b = Make("b.bin", "bb");
        var net = Net(days: 10);
        var ia = net.Hold(a);
        _now = _now.AddDays(6);
        var ib = net.Hold(b);          // expires 6 days after a
        _now = _now.AddDays(5);        // a is expired (11 days), b is not (5 days)
        var r = net.Purge();
        Assert.Equal((1, 4L), (r.Removed, r.FreedBytes));
        Assert.False(File.Exists(ia.HeldPath)); Assert.True(File.Exists(ib.HeldPath));
        Assert.Equal(1, net.Purge(all: true).Removed);
        Assert.Empty(net.List());
    }

    [Fact]
    public void The_index_survives_restarts_and_lookup_by_original_path_works()
    {
        var f = Make("keep/me.txt");
        var item = Net().Hold(f);
        var again = Net();                                   // a new instance reads the index file
        Assert.Equal(item.Id, again.FindByOriginalPath(f)!.Id);
        again.Restore(item.Id);
        Assert.Null(Net().FindByOriginalPath(f));            // the removal was recorded too
    }

    [Fact]
    public void Missing_file_cannot_be_held_and_a_lost_held_copy_is_reported_not_crashed()
    {
        Assert.Throws<FileNotFoundException>(() => Net().Hold(Path.Combine(_root, "nope.txt")));
        var f = Make("x.txt"); var net = Net(); var item = net.Hold(f);
        File.Delete(item.HeldPath);
        Assert.Equal(RestoreStatus.NotInRecycleBin, net.Restore(item.Id).Status);
        Assert.Empty(net.List());
    }

    [Fact]
    public void FileDisposal_follows_the_settings()
    {
        try
        {
            FileDisposal.Configure(new AppSettings { SafetyNet = true, SafetyNetDays = 7 });
            Assert.Equal(DisposalMode.SafetyNet, FileDisposal.Mode);
            Assert.Equal(7, FileDisposal.Net.Days);
            FileDisposal.Configure(new AppSettings());
            Assert.Equal(DisposalMode.RecycleBin, FileDisposal.Mode);
        }
        finally { FileDisposal.Configure(new AppSettings()); }
    }
}
