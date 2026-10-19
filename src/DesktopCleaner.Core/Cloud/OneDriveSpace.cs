using System.Diagnostics;
using DesktopCleaner.Core.Deletion;

namespace DesktopCleaner.Core.Cloud;

public sealed record LocalCloudFile(string Path, long Size, DateTime LastWriteUtc)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
}

/// <summary>
/// OneDrive "Free up space": files that are downloaded and also in the cloud can be set back to online-only,
/// like Storage Sense does. Nothing is lost: the file stays in OneDrive and re-downloads when opened.
/// Only files inside OneDrive folders are ever touched, and files the user pinned ("Always keep on this device") are left alone.
/// </summary>
public static class OneDriveSpace
{
    public static IReadOnlyList<string> Roots() =>
        new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }
            .Select(Environment.GetEnvironmentVariable).Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
            .Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p!))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static bool IsInsideOneDrive(string path, IEnumerable<string>? roots = null)
    {
        var full = Path.GetFullPath(path);
        return (roots ?? Roots()).Any(r => full.StartsWith(Path.TrimEndingDirectorySeparator(r) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Downloaded, unpinned OneDrive files at least <paramref name="minBytes"/> big, largest first.
    /// .NET programs are not shown the reparse-point flag on OneDrive placeholders, so a downloaded placeholder looks like an
    /// ordinary file. We therefore only offer files when the folder clearly uses Files On-Demand (it contains online-only files).
    /// </summary>
    public static Task<IReadOnlyList<LocalCloudFile>> FindFreeableAsync(IEnumerable<string>? roots = null, long minBytes = 5 * 1024 * 1024,
        int maxResults = 500, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<LocalCloudFile>>(() =>
        {
            var found = new List<LocalCloudFile>();
            foreach (var root in roots ?? Roots())
            {
                var candidates = new List<LocalCloudFile>();
                bool onDemand = false;
                foreach (var f in FsWalker.Files(root, true, false, null, ct))
                {
                    try
                    {
                        var a = f.Attributes;
                        if (CloudFiles.IsCloudOnly(a)) { onDemand = true; continue; }
                        if (CloudFiles.IsPinned(a) || CloudFiles.IsUnpinned(a) || f.Length < minBytes) continue;
                        candidates.Add(new LocalCloudFile(f.FullName, f.Length, f.LastWriteTimeUtc));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                if (onDemand) found.AddRange(candidates);
            }
            return found.OrderByDescending(x => x.Size).Take(maxResults).ToList();
        }, ct);
    /// <summary>Marks the files online-only (attrib +U -P). OneDrive then removes the local copy. Runs <paramref name="runAttrib"/> per file.</summary>
    public static IReadOnlyList<DeleteOutcome> FreeUp(IEnumerable<LocalCloudFile> files, ActivityLog? log = null,
        Func<string, int>? runAttrib = null, IEnumerable<string>? roots = null, IProgress<DeleteProgress>? progress = null)
    {
        runAttrib ??= RunAttrib;
        roots = (roots ?? Roots()).ToList();
        var outcomes = new List<DeleteOutcome>();
        var list = files.ToList();
        foreach (var f in list)
        {
            DeleteOutcome o;
            try
            {
                if (!IsInsideOneDrive(f.Path, roots)) o = new(f.Path, DeleteStatus.SkippedProtected, 0, "Not inside a OneDrive folder.");
                else if (!File.Exists(f.Path)) o = new(f.Path, DeleteStatus.SkippedMissing, 0);
                else
                {
                    var fi = new FileInfo(f.Path);
                    if (fi.Length != f.Size || fi.LastWriteTimeUtc != f.LastWriteUtc) o = new(f.Path, DeleteStatus.SkippedChanged, 0, "File changed since scan.");
                    else if (CloudFiles.IsPinned(fi.Attributes)) o = new(f.Path, DeleteStatus.SkippedProtected, 0, "Pinned to this device.");
                    else if (runAttrib(f.Path) != 0) o = new(f.Path, DeleteStatus.Failed, 0, "Windows could not change the file.");
                    else o = new(f.Path, DeleteStatus.FreedFromPc, f.Size, "Still in OneDrive");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { o = new(f.Path, DeleteStatus.Failed, 0, ex.Message); }
            outcomes.Add(o);
            log?.Write(o);
            progress?.Report(new DeleteProgress(outcomes.Count, list.Count, f.Path));
        }
        return outcomes;
    }

    private static int RunAttrib(string path)
    {
        var psi = new ProcessStartInfo("attrib.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("+U"); psi.ArgumentList.Add("-P"); psi.ArgumentList.Add(path);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }
}




