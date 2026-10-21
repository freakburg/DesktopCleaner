using DesktopCleaner.Core.Deletion;
using DesktopCleaner.Core.Projects;
using Xunit;

namespace DesktopCleaner.Tests;

public sealed class ProjectClutterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-proj").FullName;
    private static readonly DateTime Now = DateTime.UtcNow;
    public void Dispose() => Directory.Delete(_root, true);

    private string File_(string rel, int size = 100, int ageDays = 0)
    {
        var p = Path.Combine(_root, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new byte[size]); File.SetLastWriteTimeUtc(p, Now.AddDays(-ageDays)); return p;
    }

    /// <summary>Makes everything in a project look as if it was last touched <paramref name="ageDays"/> ago.</summary>
    private void Age(string projectRel, int ageDays)
    {
        var dir = Path.Combine(_root, projectRel);
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(f, Now.AddDays(-ageDays));
        foreach (var d in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories).Reverse()) Directory.SetLastWriteTimeUtc(d, Now.AddDays(-ageDays));
        Directory.SetLastWriteTimeUtc(dir, Now.AddDays(-ageDays));
    }

    [Fact]
    public async Task Finds_each_kind_only_when_the_owning_project_file_is_there_and_the_project_is_old()
    {
        File_("web/package.json", ageDays: 200); File_("web/node_modules/lib/index.js", 5000, 200);
        File_("app/App.csproj", ageDays: 200); File_("app/bin/Debug/app.dll", 3000, 200); File_("app/obj/x.cache", 1000, 200);
        File_("rust/Cargo.toml", ageDays: 200); File_("rust/target/debug/x", 4000, 200);
        File_("game/Assets/a.png", ageDays: 200); File_("game/ProjectSettings/p.asset", ageDays: 200); File_("game/Library/big.bin", 9000, 200);
        File_("py/requirements.txt", ageDays: 200); File_("py/.venv/pyvenv.cfg", 10, 200); File_("py/.venv/lib/x.py", 2000, 200);
        foreach (var p in new[] { "web", "app", "rust", "game", "py" }) Age(p, 200);
        var items = await new ProjectClutterFinder().ScanAsync([_root], 90);
        Assert.Equal(new[] { "DotNetBuild", "DotNetBuild", "NodeModules", "PythonVenv", "RustTarget", "UnityLibrary" }, items.Select(i => i.Kind.ToString()).Order());
        Assert.Equal(9000, items.Single(i => i.Kind == ClutterKind.UnityLibrary).Bytes);
        Assert.All(items, i => Assert.True(i.ProjectLastActivityUtc < Now.AddDays(-150)));
        Assert.Equal("web", items.Single(i => i.Kind == ClutterKind.NodeModules).ProjectName);
    }

    [Fact]
    public async Task Folders_without_an_owning_project_file_are_left_alone()
    {
        File_("loose/node_modules/x.js", 100, 400);          // no package.json
        File_("notproject/bin/tool.exe", 100, 400);           // no csproj: just a folder called bin
        File_("data/build/out.txt", 100, 400);                // no build.gradle
        File_("lib/venv/pyvenv.cfg", 10, 400);                // no requirements.txt
        foreach (var p in new[] { "loose", "notproject", "data", "lib" }) Age(p, 400);
        Assert.Empty(await new ProjectClutterFinder().ScanAsync([_root], 90));
    }

    [Fact]
    public async Task A_recently_used_project_is_skipped_including_git_activity_and_new_source_files()
    {
        File_("fresh/package.json", ageDays: 400); File_("fresh/node_modules/a.js", 100, 400); File_("fresh/src/new.js", 10, 2);   // edited 2 days ago
        File_("gitproj/package.json", ageDays: 400); File_("gitproj/node_modules/a.js", 100, 400); File_("gitproj/.git/index", 10, 1);  // committed yesterday
        File_("stale/package.json", ageDays: 400); File_("stale/node_modules/a.js", 100, 400);
        Age("stale", 400);
        var items = await new ProjectClutterFinder().ScanAsync([_root], 90);
        Assert.Equal(["stale"], items.Select(i => i.ProjectName));
    }

    [Fact]
    public async Task Never_looks_inside_dot_folders_node_modules_or_tool_folders()
    {
        File_(".vscode/extensions/ext/package.json", ageDays: 500); File_(".vscode/extensions/ext/node_modules/x.js", 100, 500);
        File_("AppData/Local/tool/package.json", ageDays: 500); File_("AppData/Local/tool/node_modules/x.js", 100, 500);
        File_("proj/package.json", ageDays: 500); File_("proj/node_modules/dep/package.json", ageDays: 500); File_("proj/node_modules/dep/node_modules/y.js", 100, 500);
        foreach (var p in new[] { ".vscode", "AppData", "proj" }) Age(p, 500);
        var items = await new ProjectClutterFinder().ScanAsync([_root], 90);
        Assert.Equal(["proj"], items.Select(i => i.ProjectName));
        Assert.Single(items);   // the nested node_modules is not reported separately
    }

    [Fact]
    public async Task Remover_deletes_old_build_folders_reports_progress_and_skips_ones_that_changed()
    {
        File_("a/package.json", ageDays: 300); File_("a/node_modules/x.js", 500, 300); File_("b/package.json", ageDays: 300); File_("b/node_modules/y.js", 700, 300);
        Age("a", 300); Age("b", 300);
        var items = await new ProjectClutterFinder().ScanAsync([_root], 90);
        File_("b/src/just-edited.js", 5, 0);                           // project b gets used after the scan
        var reports = new List<DeleteProgress>(); var logPath = Path.Combine(_root, "log.jsonl");
        var res = new ProjectClutterRemover(new ActivityLog(logPath)).Remove(items, 90, new Sync(reports));
        Assert.False(Directory.Exists(Path.Combine(_root, "a", "node_modules")));
        Assert.True(Directory.Exists(Path.Combine(_root, "b", "node_modules")));
        Assert.Equal(1, res.Count(o => o.Succeeded)); Assert.Equal(500, res.Sum(o => o.Bytes));
        Assert.Equal([1, 2], reports.Select(r => r.Done));
        Assert.Contains("Deleted", File.ReadAllText(logPath));
    }

    [Fact]
    public void Creator_and_developer_caches_are_in_the_catalog()
    {
        var all = DesktopCleaner.Core.Caches.CacheCatalog.All;
        Assert.Contains(all, c => c.Id == "capcut"); Assert.Contains(all, c => c.Id == "premiere"); Assert.Contains(all, c => c.Id == "gradle");
        Assert.Contains(all, c => c.Group == DesktopCleaner.Core.Caches.CacheGroup.Creator);
    }

    private sealed class Sync(List<DeleteProgress> into) : IProgress<DeleteProgress> { public void Report(DeleteProgress v) => into.Add(v); }
}
