using SkiaSharp;
using SnapZap.Core.Imaging;
using Xunit;

namespace SnapZap.Tests;

/// <summary>Task 26: the mid-size preview <c>/api/full/{id}</c> serves instead of the original.</summary>
public class SkiaImageServiceTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "snapzap_preview_" + Guid.NewGuid().ToString("N"));
    readonly SkiaImageService _imaging = new() { PreviewMaxEdge = 200 }; // small, so the test fixture doesn't need to be huge

    public SkiaImageServiceTests() => Directory.CreateDirectory(_dir);

    static void WriteJpeg(string path, int w, int h, SKColor color)
    {
        using var bmp = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bmp)) canvas.Clear(color);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 100);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }

    [Fact]
    public void WritePreview_downscales_a_large_source_to_fit_PreviewMaxEdge()
    {
        var src = Path.Combine(_dir, "big.jpg");
        WriteJpeg(src, 1000, 500, SKColors.Orange);
        var previewPath = Path.Combine(_dir, "preview.jpg");

        Assert.True(_imaging.WritePreview(src, previewPath));
        Assert.True(File.Exists(previewPath));

        using var preview = SKBitmap.Decode(previewPath);
        Assert.True(preview!.Width <= 200 && preview.Height <= 200);
        // Aspect ratio preserved (2:1 source).
        Assert.Equal(2.0, (double)preview.Width / preview.Height, 1);
    }

    [Fact]
    public void WritePreview_leaves_a_source_already_smaller_than_PreviewMaxEdge_unscaled()
    {
        var src = Path.Combine(_dir, "small.jpg");
        WriteJpeg(src, 80, 60, SKColors.Teal);
        var previewPath = Path.Combine(_dir, "preview_small.jpg");

        Assert.True(_imaging.WritePreview(src, previewPath));
        using var preview = SKBitmap.Decode(previewPath);
        Assert.Equal(80, preview!.Width);
        Assert.Equal(60, preview.Height);
    }

    [Fact]
    public void WritePreview_returns_false_for_an_undecodable_source_so_the_caller_can_fall_back()
    {
        var src = Path.Combine(_dir, "not-an-image.jpg");
        File.WriteAllText(src, "definitely not a jpeg");
        var previewPath = Path.Combine(_dir, "preview_fail.jpg");

        Assert.False(_imaging.WritePreview(src, previewPath));
        Assert.False(File.Exists(previewPath));
    }

    [Fact]
    public void WritePreview_is_concurrency_safe_for_two_writers_of_the_same_content_hashed_path()
    {
        // Mirrors the thumbnail-cache regression test: duplicates share a content hash and
        // therefore a preview path, so concurrent generation must not collide on File.Create.
        var src = Path.Combine(_dir, "shared.jpg");
        WriteJpeg(src, 300, 300, SKColors.Purple);
        var previewPath = Path.Combine(_dir, "shared_preview.jpg");

        var results = new bool[8];
        Parallel.For(0, 8, i => results[i] = _imaging.WritePreview(src, previewPath));

        Assert.All(results, Assert.True);
        Assert.True(File.Exists(previewPath));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }
}
