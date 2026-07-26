using System.Security.Cryptography;
using SkiaSharp;
using SnapZap.Core.Analysis;
using SnapZap.Core.Imaging;
using SnapZap.Core.Nsfw;
using SnapZap.Core.Scanning;
using Xunit;

namespace SnapZap.Tests;

/// <summary>
/// Golden values captured before any Phase B/C code change (tech-spec "CPU Performance
/// Optimisation", Task 1). These are the spec's defence against a silent behavioural change
/// dressed as an optimisation: AC 9 (hashing), AC 10 (NSFW tensor packing), AC 12 (blur denominator).
/// </summary>
/// <remarks>
/// <b>Scope caveat.</b> The tech-spec asks for these to be captured on a Windows test machine
/// against a real ≥20k-photo library and a real <c>catalog.db</c> copy. This environment is a
/// macOS dev sandbox with no such library or Windows hardware available, so these golden values are
/// captured against small, deterministically-generated fixtures instead — sufficient to catch a
/// bit-for-bit regression in the functions under test, but not a substitute for the Windows
/// baseline the spec calls for. See <see cref="PerfBaselineTests"/> for the same caveat on the
/// performance side.
/// </remarks>
public class GoldenValueTests
{
    /// <summary>The same asymmetric-scene generator <c>PerceptualHashTests</c> uses, so the fixture
    /// is deterministic (no RNG) and stable across runs without committing a binary to the repo.</summary>
    internal static SKBitmap Scene(int w, int h)
    {
        var bmp = new SKBitmap(w, h);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(new SKColor(30, 30, 40));
        using var paint = new SKPaint { IsAntialias = true };
        paint.Color = new SKColor(220, 200, 120);
        canvas.DrawRect(new SKRect(w * 0.05f, h * 0.05f, w * 0.45f, h * 0.30f), paint);
        paint.Color = new SKColor(90, 160, 220);
        canvas.DrawCircle(w * 0.70f, h * 0.35f, MathF.Min(w, h) * 0.18f, paint);
        paint.Color = new SKColor(200, 90, 90);
        canvas.DrawRect(new SKRect(w * 0.15f, h * 0.55f, w * 0.85f, h * 0.95f), paint);
        return bmp;
    }

    static string WritePng(SKBitmap bmp, string path)
    {
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.Create(path);
        data.SaveTo(fs);
        return path;
    }

    // ---- AC 9: SHA-256 must be byte-identical after Task 6 (allocation-free hashing) ----

    /// <summary>Already-committed fixture, so this hash is stable regardless of how this test
    /// generates anything else — a direct golden value for <see cref="Hasher"/>.</summary>
    const string ExifSampleSha256 = "7b1dcc796b5cac8fc791e11f40baf58da8c16ecdba3f8ba14f6c360685059453";

    [Fact]
    public void Hash_of_committed_fixture_matches_golden_value()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "exif_sample.jpg");
        Assert.Equal(ExifSampleSha256, Hasher.HashFile(path));
    }

    [Fact]
    public void Hash_with_out_length_overload_matches_golden_value()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "exif_sample.jpg");
        var hash = Hasher.HashFile(path, out var length);
        Assert.Equal(ExifSampleSha256, hash);
        Assert.Equal(new FileInfo(path).Length, length);
    }

    // ---- AC 12: blur score must match within tolerance after Task 12 (fused Laplacian/variance) ----

    const double GoldenBlurScore = 291.10588906864876;

    [Fact]
    public void Blur_score_of_generated_scene_matches_golden_value()
    {
        using var scene = Scene(256, 256);
        var imaging = new SkiaImageService();
        var path = Path.Combine(Path.GetTempPath(), "snapzap_golden_" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            WritePng(scene, path);
            var decoded = imaging.DecodeGray(path, 512);
            Assert.NotNull(decoded);
            var score = BlurDetector.ScoreFrom(decoded!.Value.gray, decoded.Value.w, decoded.Value.h);
            Assert.NotNull(score);
            Assert.Equal(GoldenBlurScore, score!.Value, 6);
        }
        finally { File.Delete(path); }
    }

    // ---- AC 10: NSFW tensor must be element-wise identical after Task 4 (GetPixelSpan packing) ----

    const string GoldenTensorSha256 = "25a38dbe9931417bea8b8a86b2a68f57f1a5cdffe1b1b95615fb44e3e51df442";

    [Fact]
    public void Nsfw_tensor_of_generated_scene_matches_golden_value()
    {
        using var scene = Scene(300, 200);
        var cfg = new PreprocessConfig();
        var tensor = NsfwPreprocess.ToTensor(scene, cfg);

        var bytes = new byte[tensor.Length * sizeof(float)];
        Buffer.BlockCopy(tensor.ToArray(), 0, bytes, 0, bytes.Length);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));

        Assert.Equal(GoldenTensorSha256, hash);
    }
}
