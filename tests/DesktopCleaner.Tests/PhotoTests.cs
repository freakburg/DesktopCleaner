using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using DesktopCleaner.Core.Deletion;
using DesktopCleaner.Core.Photos;
using Xunit;

namespace DesktopCleaner.Tests;

[SupportedOSPlatform("windows")]
public sealed class PhotoTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-photo").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    /// <summary>A picture with a bright blob whose position makes each seed look different.</summary>
    private string Photo(string name, int w, int h, int seed, ImageFormat? fmt = null)
    {
        var path = Path.Combine(_root, name);
        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var bg = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.Black, Color.DimGray, seed % 2 == 0 ? 0f : 90f);
            g.FillRectangle(bg, 0, 0, w, h);
            using var blob = new SolidBrush(Color.White);
            switch (seed)
            {
                case 0: g.FillEllipse(blob, w * 0.1f, h * 0.1f, w * 0.4f, h * 0.4f); break;
                case 1: g.FillRectangle(blob, w * 0.5f, h * 0.55f, w * 0.4f, h * 0.35f); break;
                default: g.FillEllipse(blob, w * 0.6f, h * 0.05f, w * 0.3f, h * 0.3f); g.FillRectangle(blob, w * 0.05f, h * 0.6f, w * 0.5f, h * 0.3f); break;
            }
        }
        bmp.Save(path, fmt ?? ImageFormat.Png);
        return path;
    }

    private static PhotoScanOptions Opts => new() { MinFileBytes = 1 };

    [Fact]
    public async Task Resized_and_recompressed_copies_group_together_but_different_pictures_do_not()
    {
        Photo("orig.png", 800, 600, 0);
        Photo("small.png", 200, 150, 0);
        Photo("copy.jpg", 400, 300, 0, ImageFormat.Jpeg);
        Photo("other.png", 800, 600, 1);
        var r = await new SimilarPhotoFinder().ScanAsync(_root, Opts);
        var g = Assert.Single(r.Groups);
        Assert.Equal(["copy.jpg", "orig.png", "small.png"], g.Photos.Select(p => p.Name).Order());
        Assert.Equal("orig.png", g.SuggestedKeep.Name);
        Assert.Equal(4, r.PhotosScanned);
    }

    [Fact]
    public async Task Unreadable_images_are_reported_not_fatal()
    {
        Photo("a.png", 100, 100, 0);
        File.WriteAllText(Path.Combine(_root, "broken.jpg"), "not an image at all");
        var r = await new SimilarPhotoFinder().ScanAsync(_root, Opts);
        Assert.Contains(r.Issues, i => i.Contains("broken.jpg"));
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        Photo("a.png", 100, 100, 0);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SimilarPhotoFinder().ScanAsync(_root, Opts, ct: cts.Token));
    }

    [Fact]
    public async Task Remover_recycles_chosen_but_keeps_last_and_skips_changed()
    {
        var a = Photo("a.png", 400, 300, 0); var b = Photo("b.png", 200, 150, 0);
        var group = Assert.Single((await new SimilarPhotoFinder().ScanAsync(_root, Opts)).Groups);
        var recycled = new List<string>();
        var remover = new PhotoRemover(null, recycled.Add);

        var all = remover.Remove(group, [a, b]);
        Assert.All(all, o => Assert.Equal(DeleteStatus.SkippedLastCopy, o.Status));

        File.AppendAllText(b, "x");
        Assert.Equal(DeleteStatus.SkippedChanged, remover.Remove(group, [b])[0].Status);
        Assert.Empty(recycled);

        File.WriteAllBytes(b, File.ReadAllBytes(b)[..^1]); // restore size; timestamp still differs
        Assert.Empty(recycled);
    }

    [Fact]
    public void Hamming_distance_counts_differing_bits()
    {
        Assert.Equal(0, SimilarPhotoFinder.Distance(0xFFUL, 0xFFUL));
        Assert.Equal(3, SimilarPhotoFinder.Distance(0b1011UL, 0b0000UL));
    }
}
