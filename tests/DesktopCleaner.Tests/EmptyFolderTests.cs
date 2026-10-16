using DesktopCleaner.Core.EmptyFolders;
using Xunit;

namespace DesktopCleaner.Tests;

public sealed class EmptyFolderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-empty").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    private string Dir(string rel) => Directory.CreateDirectory(Path.Combine(_root, rel)).FullName;
    private void File_(string rel) { Dir(Path.GetDirectoryName(rel) ?? ""); File.WriteAllText(Path.Combine(_root, rel), "x"); }

    [Fact]
    public async Task Reports_only_topmost_empty_folders_with_nested_count()
    {
        Dir("a/b/c"); Dir("a/b2");           // a is entirely empty
        File_("keep/file.txt"); Dir("keep/empty");
        var r = await new EmptyFolderFinder().ScanAsync(_root);
        Assert.Equal(2, r.Count);
        var a = r.Single(f => f.Path.EndsWith(Path.DirectorySeparatorChar + "a"));
        Assert.Equal(3, a.NestedEmpty);
        Assert.Contains(r, f => f.Path.EndsWith("empty"));
        Assert.DoesNotContain(r, f => f.Path.EndsWith("keep"));
    }

    [Fact]
    public async Task Folder_with_a_file_deep_inside_is_not_empty()
    {
        Dir("x/y"); File_("x/y/z/deep.txt");
        Assert.Empty(await new EmptyFolderFinder().ScanAsync(_root));
    }

    [Fact]
    public async Task Picked_root_is_never_reported_even_if_empty()
    {
        Dir("only/child");
        var r = await new EmptyFolderFinder().ScanAsync(_root);
        Assert.Equal([Path.Combine(_root, "only")], r.Select(f => f.Path));
    }

    [Fact]
    public async Task Remove_rechecks_and_skips_folders_that_filled_up()
    {
        Dir("a"); Dir("b");
        var finder = new EmptyFolderFinder(null, p => Directory.Delete(p, true));
        var found = await finder.ScanAsync(_root);
        File_("b/new.txt");
        var res = finder.Remove(found);
        Assert.Equal(EmptyFolderStatus.Removed, res.Single(o => o.Path.EndsWith("a")).Status);
        Assert.Equal(EmptyFolderStatus.SkippedNotEmpty, res.Single(o => o.Path.EndsWith("b")).Status);
        Assert.False(Directory.Exists(Path.Combine(_root, "a")));
        Assert.True(File.Exists(Path.Combine(_root, "b", "new.txt")));
    }

    [Fact]
    public void Remove_refuses_standard_user_folders()
    {
        var finder = new EmptyFolderFinder(null, _ => throw new InvalidOperationException("must not delete"));
        var res = finder.Remove([new EmptyFolder(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 0, DateTime.UtcNow)]);
        Assert.Equal(EmptyFolderStatus.SkippedProtected, res[0].Status);
    }
}
