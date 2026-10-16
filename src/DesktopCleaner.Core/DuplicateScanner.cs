using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace DesktopCleaner.Core.Duplicates;

/// <summary>
/// Finds duplicate files: group by size (cheap), then by a partial hash of the head,
/// then by a full SHA-256 only for survivors. Locked/unreadable files are reported, not fatal.
/// </summary>
public sealed class DuplicateScanner
{
    private const int PartialBytes = 64 * 1024;

    public async Task<ScanResult> ScanAsync(
        string root, ScanOptions? options = null,
        IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        options ??= new ScanOptions();
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);

        var issues = new ConcurrentBag<ScanIssue>();
        var bySize = new Dictionary<long, List<FileEntry>>();
        long seen = 0;

        // Phase 1: enumerate and bucket by size.
        long cloudSkipped = 0;
        await Task.Run(() =>
        {
            foreach (var info in FsWalker.Files(root, options.IncludeSubfolders, options.SkipHiddenAndSystem,
                         (p, m) => issues.Add(new ScanIssue(p, m)), ct))
            {
                try
                {
                    // Reading a cloud-only file would download it, so leave those out.
                    if (CloudFiles.IsCloudOnly(info.Attributes)) { cloudSkipped++; continue; }
                    var len = info.Length;
                    seen++;
                    if (len >= options.MinimumSize)
                    {
                        if (!bySize.TryGetValue(len, out var list)) bySize[len] = list = new();
                        list.Add(new FileEntry(info.FullName, len, info.LastWriteTimeUtc));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    issues.Add(new ScanIssue(info.FullName, ex.Message));
                }
                if (seen % 500 == 0)
                    progress?.Report(new ScanProgress(ScanPhase.Enumerating, seen, 0, 0, info.FullName));
            }
        }, ct).ConfigureAwait(false);
        var candidates = bySize.Values.Where(l => l.Count > 1).ToList();
        long toHash = candidates.Sum(l => (long)l.Count);
        long hashed = 0;
        var groups = new ConcurrentBag<DuplicateGroup>();
        progress?.Report(new ScanProgress(ScanPhase.Hashing, seen, toHash, 0, null));

        // Phase 2: partial hash, then full hash within each same-size bucket.
        await Parallel.ForEachAsync(candidates,
            new ParallelOptions { MaxDegreeOfParallelism = options.MaxParallelHashing, CancellationToken = ct },
            async (bucket, token) =>
            {
                var partial = await GroupByHashAsync(bucket, PartialBytes, issues, token);
                foreach (var sub in partial.Where(g => g.Count > 1))
                {
                    var full = await GroupByHashAsync(sub.Files, null, issues, token);
                    foreach (var g in full.Where(g => g.Count > 1))
                        groups.Add(new DuplicateGroup(g.Hash, g[0].Size, g.Files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList()));
                }
                var n = Interlocked.Add(ref hashed, bucket.Count);
                progress?.Report(new ScanProgress(ScanPhase.Hashing, seen, toHash, n, bucket[0].Path));
            }).ConfigureAwait(false);

        progress?.Report(new ScanProgress(ScanPhase.Done, seen, toHash, hashed, null));
        return new ScanResult
        {
            Groups = groups.OrderByDescending(g => g.WastedBytes).ToList(),
            Issues = issues.ToList(),
            FilesScanned = seen,
            CloudOnlySkipped = cloudSkipped,
        };
    }

    private sealed class HashBucket(string hash, List<FileEntry> files)
    {
        public string Hash { get; } = hash;
        public List<FileEntry> Files { get; } = files;
        public int Count => Files.Count;
        public FileEntry this[int i] => Files[i];
        public long Size => Files[0].Size;
    }

    private static async Task<List<HashBucket>> GroupByHashAsync(
        IReadOnlyList<FileEntry> files, int? limit, ConcurrentBag<ScanIssue> issues, CancellationToken ct)
    {
        var map = new Dictionary<string, List<FileEntry>>();
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var h = await HashFileAsync(f.Path, limit, ct).ConfigureAwait(false);
                if (!map.TryGetValue(h, out var l)) map[h] = l = new();
                l.Add(f);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                issues.Add(new ScanIssue(f.Path, ex.Message));
            }
        }
        return map.Select(kv => new HashBucket(kv.Key, kv.Value)).ToList();
    }

    /// <summary>SHA-256 of a file (or of its first <paramref name="limit"/> bytes). Opens with shared read access.</summary>
    public static async Task<string> HashFileAsync(string path, int? limit = null, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        var buf = new byte[81920];
        long remaining = limit ?? long.MaxValue;
        int read;
        while (remaining > 0 && (read = await fs.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, remaining)), ct).ConfigureAwait(false)) > 0)
        {
            sha.TransformBlock(buf, 0, read, null, 0);
            remaining -= read;
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }
}



