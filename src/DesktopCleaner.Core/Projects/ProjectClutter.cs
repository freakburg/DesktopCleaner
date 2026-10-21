using System.Collections.Concurrent;
using DesktopCleaner.Core.Deletion;

namespace DesktopCleaner.Core.Projects;

public enum ClutterKind { NodeModules, DotNetBuild, RustTarget, MavenTarget, GradleBuild, PythonVenv, UnityLibrary }

/// <summary>A build or dependency folder that can be recreated from a project's source, inside a project you have not touched for a while.</summary>
public sealed record ClutterItem(ClutterKind Kind, string ProjectPath, string FolderPath, long Bytes, DateTime ProjectLastActivityUtc)
{
    public string ProjectName => Path.GetFileName(ProjectPath.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : ProjectPath;
    public string FolderName => Path.GetFileName(FolderPath.TrimEnd(Path.DirectorySeparatorChar));
    public string KindText => Kind switch
    {
        ClutterKind.NodeModules => "JavaScript packages",
        ClutterKind.DotNetBuild => ".NET build output",
        ClutterKind.RustTarget => "Rust build output",
        ClutterKind.MavenTarget => "Maven build output",
        ClutterKind.GradleBuild => "Gradle build output",
        ClutterKind.PythonVenv => "Python environment",
        _ => "Unity import cache",
    };
    /// <summary>How the folder comes back, shown so nobody is surprised.</summary>
    public string Rebuild => Kind switch
    {
        ClutterKind.NodeModules => "Run npm install (or yarn/pnpm install)",
        ClutterKind.DotNetBuild => "Rebuilds on the next build",
        ClutterKind.RustTarget => "Rebuilds on the next cargo build",
        ClutterKind.MavenTarget => "Rebuilds on the next mvn package",
        ClutterKind.GradleBuild => "Rebuilds on the next Gradle build",
        ClutterKind.PythonVenv => "Recreate it and pip install -r requirements.txt",
        _ => "Unity re-imports assets the next time you open the project (can take a while)",
    };
}

public sealed record ClutterProgress(int FoldersChecked, int ItemsFound, string CurrentFolder);

/// <summary>
/// Finds regenerable build/dependency folders in software projects (node_modules, bin/obj, target, build, venv, Unity Library) whose
/// project has had no activity for a while. A folder only counts when the project clearly owns it (for example, node_modules next to a
/// package.json), and tool-owned places are never searched: hidden "dot" folders such as .vscode, AppData, Program Files and Windows.
/// </summary>
public sealed class ProjectClutterFinder
{
    private static readonly HashSet<string> SkipFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppData", "Program Files", "Program Files (x86)", "ProgramData", "Windows", "$Recycle.Bin", "System Volume Information", "node_modules", ".git",
    };

    public async Task<IReadOnlyList<ClutterItem>> ScanAsync(IEnumerable<string> roots, int olderThanDays, Func<DateTime>? clock = null,
        IProgress<ClutterProgress>? progress = null, CancellationToken ct = default)
    {
        var now = (clock ?? (() => DateTime.UtcNow))();
        var cutoff = now.AddDays(-olderThanDays);
        var found = new ConcurrentBag<(ClutterKind Kind, string Project, string Folder)>();
        int checkedCount = 0;

        void Walk(DirectoryInfo dir)
        {
            ct.ThrowIfCancellationRequested();
            var n = Interlocked.Increment(ref checkedCount);
            if (n % 500 == 0) progress?.Report(new ClutterProgress(n, found.Count, dir.FullName));
            DirectoryInfo[] subs;
            try { subs = dir.GetDirectories(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
            foreach (var sub in subs)
            {
                if (FsWalker.IsLink(sub)) continue;
                if (Classify(sub) is { } kind) { found.Add((kind, dir.FullName, sub.FullName)); continue; }   // do not look inside a build folder
                if (sub.Name.StartsWith('.') || SkipFolders.Contains(sub.Name)) continue;
                Walk(sub);
            }
        }

        await Task.Run(() =>
        {
            var tops = new List<DirectoryInfo>();
            foreach (var r in roots.Where(Directory.Exists))
            {
                var root = new DirectoryInfo(r);
                // the root itself may be a project: check its own build folders, then fan out over its sub-folders
                try
                {
                    foreach (var sub in root.GetDirectories())
                    {
                        if (FsWalker.IsLink(sub)) continue;
                        if (Classify(sub) is { } k) found.Add((k, root.FullName, sub.FullName));
                        else if (!sub.Name.StartsWith('.') && !SkipFolders.Contains(sub.Name)) tops.Add(sub);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            Parallel.ForEach(tops, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8), CancellationToken = ct }, Walk);
        }, ct).ConfigureAwait(false);

        var items = new ConcurrentBag<ClutterItem>();
        await Task.Run(() => Parallel.ForEach(found, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8), CancellationToken = ct }, f =>
        {
            var activity = LastActivity(f.Project, f.Folder);
            if (activity >= cutoff) return;                     // worked on recently: leave it alone
            items.Add(new ClutterItem(f.Kind, f.Project, f.Folder, FolderSize(f.Folder, ct), activity));
        }), ct).ConfigureAwait(false);
        progress?.Report(new ClutterProgress(checkedCount, items.Count, ""));
        return items.OrderByDescending(i => i.Bytes).ToList();
    }

    /// <summary>What kind of build folder this is, but only if the project that owns it is clearly there.</summary>
    public static ClutterKind? Classify(DirectoryInfo folder)
    {
        var parent = folder.Parent;
        if (parent is null) return null;
        bool Has(string pattern) { try { return parent.EnumerateFileSystemInfos(pattern).Any(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; } }
        switch (folder.Name.ToLowerInvariant())
        {
            case "node_modules": return Has("package.json") && !IsInsideNodeModules(folder) ? ClutterKind.NodeModules : null;
            case "bin" or "obj": return Has("*.csproj") || Has("*.fsproj") || Has("*.vbproj") ? ClutterKind.DotNetBuild : null;
            case "target": return Has("Cargo.toml") ? ClutterKind.RustTarget : Has("pom.xml") ? ClutterKind.MavenTarget : null;
            case "build": return Has("build.gradle") || Has("build.gradle.kts") ? ClutterKind.GradleBuild : null;
            case ".venv" or "venv": return File.Exists(Path.Combine(folder.FullName, "pyvenv.cfg")) && (Has("requirements.txt") || Has("pyproject.toml")) ? ClutterKind.PythonVenv : null;
            case "library": return Directory.Exists(Path.Combine(parent.FullName, "Assets")) && Directory.Exists(Path.Combine(parent.FullName, "ProjectSettings")) ? ClutterKind.UnityLibrary : null;
            default: return null;
        }
    }

    private static bool IsInsideNodeModules(DirectoryInfo d) { for (var p = d.Parent; p is not null; p = p.Parent) if (p.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) return true; return false; }

    /// <summary>The newest sign of life in the project: its own top-level files, and Git activity. Build folders are ignored (building changes them).</summary>
    public static DateTime LastActivity(string projectPath, string excludeFolder)
    {
        var newest = Directory.GetLastWriteTimeUtc(projectPath);
        try
        {
            foreach (var f in new DirectoryInfo(projectPath).EnumerateFiles())
                if (f.LastWriteTimeUtc > newest) newest = f.LastWriteTimeUtc;
            foreach (var g in new[] { Path.Combine(projectPath, ".git", "index"), Path.Combine(projectPath, ".git", "HEAD"), Path.Combine(projectPath, ".git", "logs", "HEAD") })
                if (File.Exists(g) && File.GetLastWriteTimeUtc(g) > newest) newest = File.GetLastWriteTimeUtc(g);
            // source lives one level down in most projects (src, Assets, app...): look at the folders' own modified times, minus build output
            foreach (var d in new DirectoryInfo(projectPath).EnumerateDirectories())
                if (!string.Equals(d.FullName, excludeFolder, StringComparison.OrdinalIgnoreCase) && Classify(d) is null && !d.Name.StartsWith('.') && d.LastWriteTimeUtc > newest)
                    newest = d.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return newest;
    }

    private static long FolderSize(string dir, CancellationToken ct)
    {
        long total = 0;
        foreach (var f in FsWalker.Files(dir, true, false, null, ct))
            try { total += f.Length; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return total;
    }
}

/// <summary>
/// Deletes the chosen build folders for good. They are regenerable, and a node_modules folder can hold a hundred thousand files, so
/// recycling would be slow and would not free any space. Each folder is re-checked just before: still the same kind, and the project
/// has not become active since the scan.
/// </summary>
public sealed class ProjectClutterRemover(ActivityLog? log = null, Action<string>? deleteFolder = null, Func<DateTime>? clock = null)
{
    private readonly Action<string> _delete = deleteFolder ?? (p => Directory.Delete(p, recursive: true));
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);

    public IReadOnlyList<DeleteOutcome> Remove(IEnumerable<ClutterItem> chosen, int olderThanDays, IProgress<DeleteProgress>? progress = null, CancellationToken ct = default)
    {
        var items = chosen.ToList(); var outcomes = new List<DeleteOutcome>(); var cutoff = _now().AddDays(-olderThanDays);
        foreach (var i in items)
        {
            ct.ThrowIfCancellationRequested();
            DeleteOutcome o;
            try
            {
                if (ProtectedPaths.IsProtected(i.FolderPath)) o = new(i.FolderPath, DeleteStatus.SkippedProtected, 0, "Inside a system or program folder.");
                else if (!Directory.Exists(i.FolderPath)) o = new(i.FolderPath, DeleteStatus.SkippedMissing, 0);
                else if (ProjectClutterFinder.Classify(new DirectoryInfo(i.FolderPath)) != i.Kind) o = new(i.FolderPath, DeleteStatus.SkippedChanged, 0, "No longer looks like a build folder.");
                else if (ProjectClutterFinder.LastActivity(i.ProjectPath, i.FolderPath) >= cutoff) o = new(i.FolderPath, DeleteStatus.SkippedChanged, 0, "The project was used since the scan.");
                else { _delete(i.FolderPath); o = new(i.FolderPath, DeleteStatus.Deleted, i.Bytes, i.KindText); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { o = new(i.FolderPath, DeleteStatus.Failed, 0, ex.Message); }
            outcomes.Add(o); log?.Write(o);
            progress?.Report(new DeleteProgress(outcomes.Count, items.Count, i.FolderPath));
        }
        return outcomes;
    }
}
