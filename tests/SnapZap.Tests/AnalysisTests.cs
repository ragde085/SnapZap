using SnapZap.Core.Analysis;
using SnapZap.Core.Imaging;
using SkiaSharp;
using Xunit;

namespace SnapZap.Tests;

public class BlurDetectorTests
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "pc_blur_" + Guid.NewGuid().ToString("N"));

    public BlurDetectorTests() => Directory.CreateDirectory(_dir);

    string Solid(int size, SKColor c)
    {
        var path = Path.Combine(_dir, $"solid_{Guid.NewGuid():N}.png");
        using var bmp = new SKBitmap(size, size);
        using (var canvas = new SKCanvas(bmp)) canvas.Clear(c);
        Save(bmp, path);
        return path;
    }

    string HighFrequency(int size)
    {
        // 1px checkerboard — maximal high-frequency content → high Laplacian variance.
        var path = Path.Combine(_dir, $"sharp_{Guid.NewGuid():N}.png");
        using var bmp = new SKBitmap(size, size);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                bmp.SetPixel(x, y, ((x + y) % 2 == 0) ? SKColors.Black : SKColors.White);
        Save(bmp, path);
        return path;
    }

    static void Save(SKBitmap bmp, string path)
    {
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }

    [Fact]
    public void Sharp_image_scores_much_higher_than_flat_image()
    {
        var det = new BlurDetector(new SkiaImageService());
        var flat = det.Score(Solid(256, SKColors.Gray));
        var sharp = det.Score(HighFrequency(256));

        Assert.NotNull(flat);
        Assert.NotNull(sharp);
        Assert.True(flat < 1.0, $"flat image should have near-zero Laplacian variance, got {flat}");
        Assert.True(sharp > flat * 100, $"sharp {sharp} should dwarf flat {flat}");
    }

    [Fact]
    public void Exif_read_on_image_without_metadata_returns_nulls_gracefully()
    {
        var info = ExifExtractor.Read(Solid(64, SKColors.Blue));
        Assert.Null(info.TakenUnixUtc);
        Assert.Null(info.Camera);
    }
}

public class ExifExtractorTests
{
    // Real EXIF parsing against a committed fixture (DateTimeOriginal=2019-07-15 13:45:30,
    // Make=TestMake, Model="TestMake CoolCam 9000").
    static string Fixture => Path.Combine(AppContext.BaseDirectory, "fixtures", "exif_sample.jpg");

    [Fact]
    public void Reads_capture_date_and_dedupes_make_from_model()
    {
        Assert.True(File.Exists(Fixture), $"fixture missing: {Fixture}");
        var info = ExifExtractor.Read(Fixture);

        Assert.NotNull(info.TakenUnixUtc);
        var dt = DateTimeOffset.FromUnixTimeSeconds(info.TakenUnixUtc!.Value).UtcDateTime;
        Assert.Equal(new DateTime(2019, 7, 15, 13, 45, 30), dt);

        // Model already starts with Make → must NOT become "TestMake TestMake CoolCam 9000".
        Assert.Equal("TestMake CoolCam 9000", info.Camera);
    }
}
