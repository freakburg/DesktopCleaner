using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DesktopCleaner.Core.Deletion;

namespace DesktopCleaner.Core.Photos;

public sealed record PhotoInfo(string Path, long Size, DateTime LastWriteUtc, int Width, int Height, ulong Hash)
{
    public long Pixels => (long)Width * Height;
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>Photos that look alike. <see cref="SuggestedKeep"/> is the highest-resolution one (then the largest file).</summary>
public sealed record SimilarPhotoGroup(IReadOnlyList<PhotoInfo> Photos)
{
    public PhotoInfo SuggestedKeep => Photos.OrderByDescending(p => p.Pixels).ThenByDescending(p => p.Size).First();
    /// <summary>Space freed if everything except the suggested keeper were removed.</summary>
    public long ReclaimableBytes => Photos.Sum(p => p.Size) - SuggestedKeep.Size;
}

public sealed record PhotoScanOptions
{
    /// <summary>Max differing bits (of 64) for two photos to count as similar. 0 = same picture, ~10 = loosely alike.</summary>
    public int MaxDistance { get; init; } = 6;
    public long MinFileBytes { get; init; } = 10 * 1024;
    public int MaxParallel { get; init; } = Math.Max(1, Environment.ProcessorCount - 1);
    public IReadOnlyList<string> Extensions { get; init; } = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff"];
}

public sealed record PhotoProgress(long Found, long Hashed, string? Current);

public sealed class PhotoScanResult
{
    public required IReadOnlyList<SimilarPhotoGroup> Groups { get; init; }
    public required IReadOnlyList<string> Issues { get; init; }
    public long PhotosScanned { get; init; }
    /// <summary>Cloud-only photos left alone so they would not be downloaded.</summary>
    public long CloudOnlySkipped { get; init; }
    public long ReclaimableBytes => Groups.Sum(g => g.ReclaimableBytes);
}

/// <summary>
/// Finds near-duplicate photos (resized, recompressed, re-saved copies) with a perceptual "difference hash":
/// shrink to 9x8 greyscale and record whether each pixel is brighter than its right neighbour. Similar pictures
/// differ in only a few of the 64 bits. HEIC/WebP are not decoded by GDI+ and are skipped.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SimilarPhotoFinder
{
    public async Task<PhotoScanResult> ScanAsync(string root, PhotoScanOptions? options = null,
        IProgress<PhotoProgress>? progress = null, CancellationToken ct = default)
    {
        options ??= new();
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var exts = options.Extensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var issues = new ConcurrentBag<string>();
        long cloudSkipped = 0;

        var files = await Task.Run(() =>
        {
            var list = new List<FileInfo>();
            foreach (var f in FsWalker.Files(root, true, skipHiddenAndSystem: false, (p, m) => issues.Add($"{p}: {m}"), ct))
            {
                ct.ThrowIfCancellationRequested();
                if (!exts.Contains(f.Extension)) continue;
                if (CloudFiles.IsCloudOnly(f.Attributes)) { Interlocked.Increment(ref cloudSkipped); continue; } // reading it would download it
                try { if (f.Length >= options.MinFileBytes) list.Add(f); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { issues.Add($"{f.FullName}: {ex.Message}"); }
            }
            return list;
        }, ct).ConfigureAwait(false);

        var photos = new ConcurrentBag<PhotoInfo>();
        long hashed = 0;
        progress?.Report(new PhotoProgress(files.Count, 0, null));
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = options.MaxParallel, CancellationToken = ct },
            (f, token) =>
            {
                try { photos.Add(Describe(f)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException or ExternalException)
                {
                    issues.Add($"{f.FullName}: could not read as a picture");
                }
                var n = Interlocked.Increment(ref hashed);
                if (n % 20 == 0 || n == files.Count) progress?.Report(new PhotoProgress(files.Count, n, f.FullName));
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        var list = photos.OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToList();
        return new PhotoScanResult
        {
            Groups = Cluster(list, options.MaxDistance, ct),
            Issues = issues.ToList(),
            PhotosScanned = list.Count,
            CloudOnlySkipped = cloudSkipped,
        };
    }

    private static PhotoInfo Describe(FileInfo f)
    {
        using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var img = Image.FromStream(fs, useEmbeddedColorManagement: false, validateImageData: false);
        return new PhotoInfo(f.FullName, f.Length, f.LastWriteTimeUtc, img.Width, img.Height, DifferenceHash(img));
    }

    public static ulong DifferenceHash(Image img)
    {
        using var small = new Bitmap(9, 8);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(img, new Rectangle(0, 0, 9, 8));
        }
        ulong hash = 0;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                hash <<= 1;
                if (Luma(small.GetPixel(x, y)) > Luma(small.GetPixel(x + 1, y))) hash |= 1;
            }
        return hash;
    }

    private static double Luma(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;

    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    /// <summary>Groups photos transitively: if A~B and B~C they land in one group.</summary>
    public static IReadOnlyList<SimilarPhotoGroup> Cluster(IReadOnlyList<PhotoInfo> photos, int maxDistance, CancellationToken ct = default)
    {
        var parent = Enumerable.Range(0, photos.Count).ToArray();
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        for (int i = 0; i < photos.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            for (int j = i + 1; j < photos.Count; j++)
                if (Distance(photos[i].Hash, photos[j].Hash) <= maxDistance) parent[Find(j)] = Find(i);
        }
        return Enumerable.Range(0, photos.Count).GroupBy(Find).Where(g => g.Count() > 1)
            .Select(g => new SimilarPhotoGroup(g.Select(i => photos[i]).ToList()))
            .OrderByDescending(g => g.ReclaimableBytes).ToList();
    }
}

/// <summary>Removes chosen photos from a group after re-checking they are unchanged, and never removes the last remaining copy.</summary>
public sealed class PhotoRemover(ActivityLog? log = null, Action<string>? recycle = null)
{
    private readonly Action<string> _recycle = recycle ?? RecycleBin.Send;

    public IReadOnlyList<DeleteOutcome> Remove(SimilarPhotoGroup group, IEnumerable<string> pathsToRemove, IProgress<DeleteProgress>? progress = null)
    {
        var remove = new HashSet<string>(pathsToRemove, StringComparer.OrdinalIgnoreCase);
        var survivors = group.Photos.Count(p => !remove.Contains(p.Path));
        var outcomes = new List<DeleteOutcome>();
        var targets = group.Photos.Where(p => remove.Contains(p.Path)).ToList();
        foreach (var p in targets)
        {
            var o = RemoveOne(p, survivors);
            outcomes.Add(o);
            log?.Write(o);
            progress?.Report(new DeleteProgress(outcomes.Count, targets.Count, p.Path));
        }
        return outcomes;
    }

    private DeleteOutcome RemoveOne(PhotoInfo p, int survivors)
    {
        if (survivors < 1) return new(p.Path, DeleteStatus.SkippedLastCopy, 0, "At least one photo must be kept.");
        if (ProtectedPaths.IsProtected(p.Path)) return new(p.Path, DeleteStatus.SkippedProtected, 0, "Inside a protected folder.");
        try
        {
            var fi = new FileInfo(p.Path);
            if (!fi.Exists) return new(p.Path, DeleteStatus.SkippedMissing, 0);
            if (fi.Length != p.Size || fi.LastWriteTimeUtc != p.LastWriteUtc) return new(p.Path, DeleteStatus.SkippedChanged, 0, "File changed since scan.");
            _recycle(p.Path);
            return new(p.Path, DeleteStatus.Recycled, p.Size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(p.Path, DeleteStatus.Failed, 0, ex.Message);
        }
    }
}




