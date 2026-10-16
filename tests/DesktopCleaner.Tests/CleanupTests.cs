using DesktopCleaner.Core.Cleanup;
using DesktopCleaner.Core.Deletion;
using Xunit;

namespace DesktopCleaner.Tests;

public sealed class CleanupTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-clean").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    private string Make(string name, int ageDays, string content = "data")
    {
        var p = Path.Combine(_root, name);
        File.WriteAllText(p, content);
        File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-ageDays));
        return p;
    }

    private CleanupRule Rule(int days = 14, params string[] exts) =>
        new() { Name = "t", Folders = [_root], OlderThanDays = days, Extensions = exts };

    [Fact]
    public async Task Preview_only_lists_old_files_and_deletes_nothing()
    {
        var old = Make("old.tmp", 30); var fresh = Make("new.tmp", 1);
        var r = await new CleanupEngine().PreviewAsync(Rule());
        Assert.Equal([old], r.Candidates.Select(c => c.Path));
        Assert.True(File.Exists(old) && File.Exists(fresh));
        Assert.Equal(4, r.RecoverableBytes);
    }

    [Fact]
    public async Task Extension_filter_applies()
    {
        Make("a.tmp", 30); Make("b.log", 30);
        var r = await new CleanupEngine().PreviewAsync(Rule(14, ".tmp"));
        Assert.Single(r.Candidates);
    }

    [Fact]
    public async Task Locked_files_are_skipped()
    {
        var p = Make("busy.tmp", 30);
        using var held = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.None);
        var r = await new CleanupEngine().PreviewAsync(Rule());
        Assert.Empty(r.Candidates);
        Assert.Contains(r.Skipped, s => s.Contains("in use"));
    }

    [Fact]
    public async Task Run_recycles_matches()
    {
        var old = Make("old.tmp", 30);
        var recycled = new List<string>();
        var r = await new CleanupEngine(null, recycled.Add).RunAsync(Rule());
        Assert.Equal([old], recycled);
        Assert.Equal(4, r.FreedBytes);
    }

    [Fact]
    public void Rules_cannot_target_personal_or_root_folders()
    {
        Assert.NotNull(RuleSafety.ValidateFolder(@"C:\"));
        Assert.NotNull(RuleSafety.ValidateFolder(@"C:\Windows\Temp"));
        Assert.NotNull(RuleSafety.ValidateFolder(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)));
        Assert.NotNull(RuleSafety.ValidateFolder(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Assert.Null(RuleSafety.ValidateFolder(_root));
    }

    [Fact]
    public void Report_mentions_dry_run_and_total()
    {
        var text = ReportWriter.Render([new RuleReport(Rule(), [new("x", 2048, DateTime.UtcNow)], [], [])], applied: false);
        Assert.Contains("dry run", text);
        Assert.Contains("2 KB", text);
    }
}
