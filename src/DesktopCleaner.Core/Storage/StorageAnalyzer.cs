namespace DesktopCleaner.Core.Storage;

public sealed class FolderNode
{
    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : Path;
    /// <summary>Total size including everything below, even levels not kept in <see cref="Children"/>.</summary>
    public long Bytes { get; internal set; }
    public long FileCount { get; internal set; }
    /// <summary>Bytes of files directly in this folder.</summary>
    public long OwnBytes { get; internal set; }
    public List<FolderNode> Children { get; } = [];
    /// <summary>Bytes in subfolders that were folded away (too small to keep, or deeper than MaxDepth).</summary>
    public long OtherBytes => Math.Max(0, Bytes - OwnBytes - Children.Sum(c => c.Bytes));
}

public sealed record FileStat(string Path, long Size, DateTime LastWriteUtc)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
}

public sealed record ExtensionStat(string Extension, long Bytes, long Count);

public sealed record StorageOptions
{
    /// <summary>Folder levels kept in the tree; deeper folders still count toward their ancestors' sizes.</summary>
    public int MaxDepth { get; init; } = 6;
    /// <summary>Folders smaller than this share of the whole scan are folded into their parent to save memory.</summary>
    public double MinFolderFraction { get; init; } = 0.001;
    public int TopFileCount { get; init; } = 25;
    /// <summary>Folders scanned at the same time. 1 scans one folder at a time.</summary>
    public int MaxParallel { get; init; } = Math.Clamp(Environment.ProcessorCount, 1, 8);
}

public sealed record StorageProgress(long FilesSeen, long Bytes, string CurrentFolder);

public sealed class StorageReport
{
    public required FolderNode Root { get; init; }
    public required IReadOnlyList<ExtensionStat> ByExtension { get; init; }
    public required IReadOnlyList<string> Issues { get; init; }
    public IReadOnlyList<FileStat> LargestFiles { get; init; } = [];
    public IReadOnlyList<CategoryStat> ByCategory => FileCategories.Group(ByExtension);
    /// <summary>Size of files that live only in the cloud (OneDrive). They take no space on this PC, so they are not part of <see cref="TotalBytes"/>.</summary>
    public long CloudOnlyBytes { get; init; }
    public long CloudOnlyFiles { get; init; }
    public long TotalBytes => Root.Bytes;
    public long FileCount => Root.FileCount;
}

/// <summary>
/// Walks a folder once, summing sizes per folder and per file type. The top folder levels are scanned in parallel.
/// Inaccessible folders are recorded, not fatal. Cloud-only OneDrive files are counted separately because they use no local space.
/// </summary>
public sealed class StorageAnalyzer
{
    /// <summary>Folders at or above this depth fan out across threads; deeper ones run on the thread that reached them.</summary>
    private const int ParallelDepth = 2;

    public Task<StorageReport> AnalyzeAsync(string root, StorageOptions? options = null,
        IProgress<StorageProgress>? progress = null, CancellationToken ct = default)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        options ??= new();
        return Task.Run(() =>
        {
            var gate = new object();
            var exts = new Dictionary<string, (long Bytes, long Count)>(StringComparer.OrdinalIgnoreCase);
            var issues = new List<string>();
            var top = new PriorityQueue<FileStat, long>(); // min-heap on size keeps the N largest
            long seen = 0, total = 0, cloudBytes = 0, cloudFiles = 0;

            var node = Walk(new DirectoryInfo(root), 0);
            Prune(node, (long)(node.Bytes * options.MinFolderFraction));

            var stats = exts.Select(kv => new ExtensionStat(kv.Key, kv.Value.Bytes, kv.Value.Count)).OrderByDescending(e => e.Bytes).ToList();
            var largest = new List<FileStat>();
            while (top.Count > 0) largest.Add(top.Dequeue());
            largest.Reverse();
            return new StorageReport
            {
                Root = node, ByExtension = stats, Issues = issues, LargestFiles = largest,
                CloudOnlyBytes = cloudBytes, CloudOnlyFiles = cloudFiles,
            };

            static void Prune(FolderNode n, long minBytes)
            {
                n.Children.RemoveAll(c => c.Bytes < minBytes);
                foreach (var c in n.Children) Prune(c, minBytes);
            }

            FolderNode Walk(DirectoryInfo dir, int depth)
            {
                ct.ThrowIfCancellationRequested();
                var n = new FolderNode { Path = dir.FullName };
                var localExts = new Dictionary<string, (long Bytes, long Count)>(StringComparer.OrdinalIgnoreCase);
                var localTop = new List<FileStat>();
                long localCloudBytes = 0, localCloudFiles = 0;
                var subs = new List<DirectoryInfo>();
                string? issue = null;
                try
                {
                    var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
                    foreach (var f in dir.EnumerateFiles("*", opts))
                    {
                        try
                        {
                            if (FsWalker.IsLink(f)) continue;
                            var len = f.Length;
                            if (CloudFiles.IsCloudOnly(f.Attributes)) { localCloudBytes += len; localCloudFiles++; continue; }
                            n.OwnBytes += len; n.FileCount++;
                            var key = f.Extension.Length == 0 ? "(none)" : f.Extension.ToLowerInvariant();
                            localExts[key] = localExts.TryGetValue(key, out var cur) ? (cur.Bytes + len, cur.Count + 1) : (len, 1);
                            if (options.TopFileCount > 0) localTop.Add(new FileStat(f.FullName, len, f.LastWriteTimeUtc));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            lock (gate) issues.Add($"{f.FullName}: {ex.Message}");
                        }
                    }
                    foreach (var sub in dir.EnumerateDirectories("*", opts))
                        if (!FsWalker.IsLink(sub)) subs.Add(sub);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { issue = $"{dir.FullName}: {ex.Message}"; }

                // fold this folder's own files into the shared totals under one short lock
                long seenNow;
                lock (gate)
                {
                    if (issue is not null) issues.Add(issue);
                    foreach (var (k, v) in localExts) exts[k] = exts.TryGetValue(k, out var cur) ? (cur.Bytes + v.Bytes, cur.Count + v.Count) : v;
                    foreach (var fs in localTop.OrderByDescending(x => x.Size).Take(options.TopFileCount))
                    {
                        if (top.Count < options.TopFileCount || fs.Size > top.Peek().Size)
                        {
                            top.Enqueue(fs, fs.Size);
                            if (top.Count > options.TopFileCount) top.Dequeue();
                        }
                    }
                    total += n.OwnBytes; seen += n.FileCount; cloudBytes += localCloudBytes; cloudFiles += localCloudFiles;
                    seenNow = seen;
                }
                progress?.Report(new StorageProgress(seenNow, Interlocked.Read(ref total), dir.FullName));

                var children = new FolderNode[subs.Count];
                if (depth < ParallelDepth && options.MaxParallel > 1 && subs.Count > 1)
                    Parallel.For(0, subs.Count, new ParallelOptions { MaxDegreeOfParallelism = options.MaxParallel, CancellationToken = ct },
                        i => children[i] = Walk(subs[i], depth + 1));
                else
                    for (int i = 0; i < subs.Count; i++) children[i] = Walk(subs[i], depth + 1);

                foreach (var child in children)
                {
                    n.Bytes += child.Bytes; n.FileCount += child.FileCount;
                    if (depth < options.MaxDepth) n.Children.Add(child);
                }
                n.Bytes += n.OwnBytes;
                n.Children.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
                return n;
            }
        }, ct);
    }
}
