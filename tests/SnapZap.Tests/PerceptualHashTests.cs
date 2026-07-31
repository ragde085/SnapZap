using SkiaSharp;
using SnapZap.Core.Dedup;
using SnapZap.Core.Imaging;
using Xunit;

namespace SnapZap.Tests;

/// <summary>
/// The similarity fixture, generated rather than committed: the images are deterministic, so
/// checking them in would only make the repository bigger and the expectations harder to read.
///
/// Every claim the design makes about the hash is asserted here — resolution invariance,
/// re-encode tolerance, format independence, rotation matching — because those are the claims that
/// justified deleting a 45 MB sidecar.
/// </summary>
public class PerceptualHashTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "snapzap_phash_" + Guid.NewGuid().ToString("N"));
    readonly SkiaImageService _imaging = new();

    public PerceptualHashTests() => Directory.CreateDirectory(_dir);

    /// <summary>
    /// A structured, asymmetric test image. Asymmetry is the point: a symmetric pattern hashes the
    /// same under rotation for the wrong reason and would make the rotation tests vacuous.
    /// </summary>
    static SKBitmap Scene(int w, int h)
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
        using var path = new SKPath();
        path.MoveTo(w * 0.15f, h * 0.90f);
        path.LineTo(w * 0.55f, h * 0.55f);
        path.LineTo(w * 0.85f, h * 0.95f);
        path.Close();
        canvas.DrawPath(path, paint);

        return bmp;
    }

    string Write(string name, SKBitmap bmp, SKEncodedImageFormat format, int quality)
    {
        var path = Path.Combine(_dir, name);
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(format, quality);
        using var fs = File.Create(path);
        data.SaveTo(fs);
        return path;
    }

    PerceptualHash HashOf(string path)
    {
        var g = _imaging.DecodeGray(path, 512);
        Assert.NotNull(g);
        return PerceptualHash.FromGray(g!.Value.gray, g.Value.w, g.Value.h);
    }

    static SKBitmap Resized(SKBitmap src, int w, int h) =>
        src.Resize(new SKImageInfo(w, h), SKSamplingOptions.Default)!;

    static SKBitmap Rotated(SKBitmap src, int quarterTurns)
    {
        if (quarterTurns % 2 == 0)
        {
            var same = new SKBitmap(src.Width, src.Height);
            using var c0 = new SKCanvas(same);
            c0.Translate(src.Width / 2f, src.Height / 2f);
            c0.RotateDegrees(90 * quarterTurns);
            c0.Translate(-src.Width / 2f, -src.Height / 2f);
            c0.DrawBitmap(src, 0, 0);
            return same;
        }

        var swapped = new SKBitmap(src.Height, src.Width);
        using var c = new SKCanvas(swapped);
        c.Translate(src.Height / 2f, src.Width / 2f);
        c.RotateDegrees(90 * quarterTurns);
        c.Translate(-src.Width / 2f, -src.Height / 2f);
        c.DrawBitmap(src, 0, 0);
        return swapped;
    }

    static readonly int Threshold = new SnapZap.Core.Dedup.DedupSettings().VariantMaxBits;   // 40, of 544

    [Fact]
    public void A_downscaled_copy_matches_the_original()
    {
        using var big = Scene(1200, 900);
        using var small = Resized(big, 300, 225);

        var a = HashOf(Write("big.png", big, SKEncodedImageFormat.Png, 100));
        var b = HashOf(Write("small.png", small, SKEncodedImageFormat.Png, 100));

        var d = a.DistanceTo(b, PerceptualHash.Bits, allowRotation: false);
        Assert.True(d <= Threshold, $"a 4x downscale should match; distance was {d} of {PerceptualHash.Bits}");
    }

    [Fact]
    public void A_heavily_recompressed_copy_matches_the_original()
    {
        using var scene = Scene(900, 700);

        var a = HashOf(Write("orig.png", scene, SKEncodedImageFormat.Png, 100));
        var b = HashOf(Write("crushed.jpg", scene, SKEncodedImageFormat.Jpeg, 25));

        var d = a.DistanceTo(b, PerceptualHash.Bits, allowRotation: false);
        Assert.True(d <= Threshold, $"a format change plus quality-25 JPEG should match; distance was {d}");
    }

    [Fact]
    public void Rotated_copies_match_only_when_rotation_matching_is_on()
    {
        using var scene = Scene(800, 800);
        var a = HashOf(Write("up.png", scene, SKEncodedImageFormat.Png, 100));

        for (int turns = 1; turns <= 3; turns++)
        {
            using var turned = Rotated(scene, turns);
            var b = HashOf(Write($"turn{turns}.png", turned, SKEncodedImageFormat.Png, 100));

            var withRotation = b.DistanceTo(a, PerceptualHash.Bits, allowRotation: true);
            Assert.True(withRotation <= Threshold,
                $"{turns * 90} degrees should match with rotation on; distance was {withRotation}");

            var without = b.DistanceTo(a, PerceptualHash.Bits, allowRotation: false);
            Assert.True(without > Threshold,
                $"{turns * 90} degrees must not match with rotation off; distance was {without}");
        }
    }

    [Fact]
    public void Unrelated_images_are_nowhere_near_the_threshold()
    {
        using var scene = Scene(800, 600);
        using var other = new SKBitmap(800, 600);
        using (var canvas = new SKCanvas(other))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black };
            for (int i = 0; i < 12; i++)
                canvas.DrawRect(new SKRect(i * 60, 0, i * 60 + 30, 600), paint);
        }

        var a = HashOf(Write("scene.png", scene, SKEncodedImageFormat.Png, 100));
        var b = HashOf(Write("stripes.png", other, SKEncodedImageFormat.Png, 100));

        var d = a.DistanceTo(b, PerceptualHash.Bits, allowRotation: true);
        Assert.True(d > Threshold * 2,
            $"unrelated images should be far apart even allowing rotation; distance was {d}");
    }

    // ---- The vertical half of the signature (PhashRecipeVersion 4) ----------------------------

    /// <summary>
    /// Paint a frame whose brightness is <c>f(x) * g(y)</c>, so the horizontal ordering within
    /// every row is decided by <c>f</c> alone and is therefore identical for any positive
    /// <c>g</c>.
    /// </summary>
    static SKBitmap Separable(int w, int h, Func<float, float> f, Func<float, float> g)
    {
        var bmp = new SKBitmap(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var v = (byte)Math.Clamp(255f * f(x / (float)(w - 1)) * g(y / (float)(h - 1)), 0f, 255f);
                bmp.SetPixel(x, y, new SKColor(v, v, v));
            }
        return bmp;
    }

    /// <summary>
    /// The defect that produced the six-photo "duplicate" group of four desktop wallpapers and two
    /// sunsets: with only horizontal comparisons encoded, a photo's entire signature was its
    /// left-to-right luminance profile, so <em>any</em> two images sharing one were indistinguishable
    /// no matter what else was in the frame.
    /// </summary>
    /// <remarks>
    /// Both frames here use the same triangular horizontal ramp — brightness rising to the middle
    /// and falling after, which is what a centre-glow wallpaper and a sun in the sky both reduce to
    /// on a 17-cell grid. They differ only vertically: one is constant down each column, the other
    /// brightens toward the bottom. Under the horizontal-only encoding their signatures were
    /// <b>bit-for-bit identical</b> — distance 0 against a threshold of 20 — and no threshold could
    /// have separated them. This asserts the vertical half is carrying that distinction, not merely
    /// that the two happen to land apart.
    /// </remarks>
    [Fact]
    public void Frames_with_identical_horizontal_profiles_are_still_told_apart()
    {
        static float Ramp(float t) => 0.25f + 0.75f * (1f - Math.Abs(2f * t - 1f)); // peaks at centre

        using var flatDown = Separable(800, 600, Ramp, _ => 1f);              // constant down each column
        using var brightBelow = Separable(800, 600, Ramp, t => 0.35f + 0.65f * t); // brightens downward

        var a = HashOf(Write("ramp-flat.png", flatDown, SKEncodedImageFormat.Png, 100));
        var b = HashOf(Write("ramp-below.png", brightBelow, SKEncodedImageFormat.Png, 100));

        var d = a.DistanceTo(b, PerceptualHash.Bits, allowRotation: false);
        Assert.True(d > PerceptualHash.VerticalBits / 2,
            $"the vertical half must separate two frames sharing a horizontal profile; distance was only {d}");
    }

    /// <summary>
    /// <see cref="PerceptualHash.DistanceTo"/> must give the same answer whichever argument is the
    /// receiver.
    /// </summary>
    /// <remarks>
    /// It did not. Rotations are produced by turning the cell grid and re-encoding, so a rotated
    /// signature is not a bit-permutation of the unrotated one, and rotating only the receiver made
    /// the comparison order-dependent — 55,269 of 79,800 sampled pairs from a real library
    /// disagreed, the worst by 247 bits. <see cref="SimilarityGrouper"/> tests pairs in whichever
    /// order it reaches them, so that turned complete linkage into a one-directional check and put
    /// photos 259 bits apart in one 20-bit group. Do not "optimise" this back to one side.
    /// </remarks>
    [Fact]
    public void Distance_is_symmetric_in_both_argument_orders()
    {
        using var scene = Scene(800, 800);
        using var turned = Rotated(scene, 1);
        using var stripes = new SKBitmap(800, 800);
        using (var canvas = new SKCanvas(stripes))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black };
            for (int i = 0; i < 12; i++) canvas.DrawRect(new SKRect(i * 60, 0, i * 60 + 30, 800), paint);
        }

        var hashes = new[]
        {
            HashOf(Write("sym-a.png", scene, SKEncodedImageFormat.Png, 100)),
            HashOf(Write("sym-b.png", turned, SKEncodedImageFormat.Png, 100)),
            HashOf(Write("sym-c.png", stripes, SKEncodedImageFormat.Png, 100)),
            HashOf(Write("sym-d.jpg", scene, SKEncodedImageFormat.Jpeg, 30)),
        };

        for (int i = 0; i < hashes.Length; i++)
            for (int j = 0; j < hashes.Length; j++)
                Assert.Equal(
                    hashes[i].DistanceTo(hashes[j], PerceptualHash.Bits, allowRotation: true),
                    hashes[j].DistanceTo(hashes[i], PerceptualHash.Bits, allowRotation: true));
    }

    /// <summary>
    /// A frame of one flat colour ties every comparison, ties encode as 0, so solid black and solid
    /// white produce byte-identical all-zero signatures and would sit 0 bits apart. In a tool that
    /// offers duplicates for deletion, "these two blank frames are the same photo" is not an
    /// acceptable answer, so such a signature matches nothing at all.
    /// </summary>
    /// <remarks>
    /// Deliberately narrower than a general low-contrast gate. A featureless-but-not-flat photo has
    /// its bits decided by noise, and noise does not collide: measured over 179,675 unrelated pairs
    /// from a real library, a minimum-contrast floor anywhere from 2 to 8 grey levels changed the
    /// number of pairs under every tested threshold by exactly zero while discarding up to 8% of
    /// the library from matching. Only the perfectly flat case is a real collision.
    /// </remarks>
    [Fact]
    public void Flat_frames_are_degenerate_signatures_and_match_nothing()
    {
        using var black = Separable(400, 300, _ => 0f, _ => 1f);
        using var white = Separable(400, 300, _ => 1f, _ => 1f);
        using var scene = Scene(400, 300);

        var a = HashOf(Write("flat-black.png", black, SKEncodedImageFormat.Png, 100));
        var b = HashOf(Write("flat-white.png", white, SKEncodedImageFormat.Png, 100));
        var real = HashOf(Write("not-flat.png", scene, SKEncodedImageFormat.Png, 100));

        Assert.True(a.IsDegenerate);
        Assert.True(b.IsDegenerate);
        Assert.False(real.IsDegenerate);

        // They really are identical bit patterns — the guard is what stops them matching, not luck.
        Assert.Equal(a, b);

        Assert.Equal(int.MaxValue, a.DistanceTo(b, PerceptualHash.Bits, allowRotation: true));
        Assert.Equal(int.MaxValue, a.DistanceTo(real, PerceptualHash.Bits, allowRotation: true));
        Assert.Equal(int.MaxValue, real.DistanceTo(a, PerceptualHash.Bits, allowRotation: true));

        // A degenerate signature is present, not absent: it round-trips and is not IsEmpty, so a
        // catalogue never re-decodes the file hoping for a better answer.
        Assert.False(a.IsEmpty);
        Assert.True(a.IsUnusable);
    }

    /// <summary>
    /// The blob is what survives a restart, so a round trip that quietly loses bits would produce
    /// a catalogue whose stored signatures no longer mean what the matcher thinks they mean.
    /// </summary>
    [Fact]
    public void Serialization_round_trips_exactly()
    {
        using var scene = Scene(640, 480);
        var a = HashOf(Write("rt.png", scene, SKEncodedImageFormat.Png, 100));

        var bytes = a.ToBytes();
        Assert.Equal(PerceptualHash.ByteLength, bytes.Length);

        var back = PerceptualHash.FromBytes(bytes);
        Assert.False(back.IsEmpty);
        Assert.Equal(0, a.DistanceTo(back, PerceptualHash.Bits, allowRotation: false));
        Assert.Equal(a, back);
    }

    /// <summary>
    /// A blob of the wrong length was written by a build with a different grid, so its bits mean
    /// something else. Reading it as "not hashed" turns a grid change into a re-scan instead of a
    /// silently wrong comparison.
    /// </summary>
    [Fact]
    public void A_blob_of_the_wrong_size_reads_as_absent()
    {
        Assert.True(PerceptualHash.FromBytes(null).IsEmpty);
        Assert.True(PerceptualHash.FromBytes([]).IsEmpty);
        Assert.True(PerceptualHash.FromBytes(new byte[PerceptualHash.ByteLength - 8]).IsEmpty);
    }

    [Fact]
    public void An_absent_signature_never_matches_anything()
    {
        using var scene = Scene(400, 300);
        var real = HashOf(Write("real.png", scene, SKEncodedImageFormat.Png, 100));

        Assert.Equal(int.MaxValue, default(PerceptualHash).DistanceTo(real, PerceptualHash.Bits, true));
        Assert.Equal(int.MaxValue, real.DistanceTo(default, PerceptualHash.Bits, true));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }
}
