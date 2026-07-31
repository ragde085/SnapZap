using SkiaSharp;
using SnapZap.Core.Dedup;
using SnapZap.Core.Imaging;
using Xunit;

namespace SnapZap.Tests;

/// <summary>
/// AC 5: a fixture per <c>SKEncodedOrigin</c> value — all eight, including the four mirrored
/// forms — must decode to the same perceptual hash (and the same displayed dimensions) as the
/// untagged upright original.
/// </summary>
/// <remarks>
/// <b>Independent of the production transform under test.</b> The "as the camera stored it" pixel
/// data for each of the eight tags is built here via <see cref="SKCanvas"/> transforms and direct
/// <c>GetPixel</c>/<c>SetPixel</c> reflection — not by algebraically inverting
/// <see cref="SkiaImageService.ApplyOrientation"/> — so a bug in that method's own formulas would
/// not cancel itself out here. Each of the eight source images is then wrapped in a hand-built
/// minimal EXIF APP1 segment (no library can both encode arbitrary orientation tags and be
/// trusted as independent of the code under test), and decoded through the real
/// <c>SkiaImageService.DecodeScaled</c> path.
/// </remarks>
public class OrientationTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "snapzap_orient_" + Guid.NewGuid().ToString("N"));
    readonly SkiaImageService _imaging = new();

    public OrientationTests() => Directory.CreateDirectory(_dir);

    // ---- independent geometric primitives (canvas transforms / direct pixel reflection) -------

    static SKBitmap MirrorHorizontal(SKBitmap src)
    {
        var dst = new SKBitmap(src.Width, src.Height);
        using var c = new SKCanvas(dst);
        c.Translate(src.Width, 0);
        c.Scale(-1, 1);
        c.DrawBitmap(src, 0, 0);
        return dst;
    }

    static SKBitmap MirrorVertical(SKBitmap src)
    {
        var dst = new SKBitmap(src.Width, src.Height);
        using var c = new SKCanvas(dst);
        c.Translate(0, src.Height);
        c.Scale(1, -1);
        c.DrawBitmap(src, 0, 0);
        return dst;
    }

    static SKBitmap Rotate180(SKBitmap src)
    {
        var dst = new SKBitmap(src.Width, src.Height);
        using var c = new SKCanvas(dst);
        c.Translate(src.Width, src.Height);
        c.RotateDegrees(180);
        c.DrawBitmap(src, 0, 0);
        return dst;
    }

    static SKBitmap Rotate90Cw(SKBitmap src)
    {
        var dst = new SKBitmap(src.Height, src.Width);
        using var c = new SKCanvas(dst);
        c.Translate(src.Height, 0);
        c.RotateDegrees(90);
        c.DrawBitmap(src, 0, 0);
        return dst;
    }

    static SKBitmap Rotate270Cw(SKBitmap src)
    {
        var dst = new SKBitmap(src.Height, src.Width);
        using var c = new SKCanvas(dst);
        c.Translate(0, src.Width);
        c.RotateDegrees(-90);
        c.DrawBitmap(src, 0, 0);
        return dst;
    }

    /// <summary>Reflect across the main diagonal: dst(x,y) = src(y,x). Self-inverse.</summary>
    static SKBitmap Transpose(SKBitmap src)
    {
        var dst = new SKBitmap(src.Height, src.Width);
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
                dst.SetPixel(y, x, src.GetPixel(x, y));
        return dst;
    }

    /// <summary>Reflect across the anti-diagonal. Self-inverse.</summary>
    static SKBitmap Transverse(SKBitmap src)
    {
        int w = src.Width, h = src.Height;
        var dst = new SKBitmap(h, w);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                dst.SetPixel(h - 1 - y, w - 1 - x, src.GetPixel(x, y));
        return dst;
    }

    /// <summary>
    /// Pixel data "as the camera would have stored it" for EXIF orientation tag
    /// <paramref name="exifValue"/>, given the correctly-displayed (upright) reference. Each of the
    /// canonical eight operations (ExifTool's Orientation-value table) is either self-inverse or
    /// paired with its own inverse, so building the stored form is just applying that same named
    /// operation (never a composite requiring separate un-derivation).
    /// </summary>
    static SKBitmap AsStored(SKBitmap upright, int exifValue) => exifValue switch
    {
        1 => upright.Copy(),
        2 => MirrorHorizontal(upright),
        3 => Rotate180(upright),
        4 => MirrorVertical(upright),
        5 => Transpose(upright),
        6 => Rotate270Cw(upright),   // inverse of "rotate 90 CW" (tag 6's display operation)
        7 => Transverse(upright),
        8 => Rotate90Cw(upright),    // inverse of "rotate 270 CW" (tag 8's display operation)
        _ => throw new ArgumentOutOfRangeException(nameof(exifValue)),
    };

    // ---- minimal hand-built EXIF APP1 injection -------------------------------------------------

    /// <summary>
    /// Insert a minimal EXIF APP1 segment carrying only the Orientation tag right after a JPEG's
    /// SOI marker. Hand-built (not via any EXIF-writing library) so this fixture generator is
    /// independent of anything under test.
    /// </summary>
    static byte[] InjectOrientation(byte[] jpeg, ushort orientation)
    {
        if (jpeg.Length < 2 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            throw new ArgumentException("not a JPEG (missing SOI marker)");

        // TIFF header (little-endian) + IFD0 with exactly one entry: Orientation (0x0112), SHORT, count 1.
        byte[] tiff =
        [
            0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, // "II", magic 42, IFD0 offset = 8
            0x01, 0x00,                                     // 1 entry
            0x12, 0x01,                                     // tag 0x0112 = Orientation
            0x03, 0x00,                                     // type 3 = SHORT
            0x01, 0x00, 0x00, 0x00,                         // count = 1
            (byte)orientation, 0x00, 0x00, 0x00,             // value (SHORT, little-endian) + pad
            0x00, 0x00, 0x00, 0x00,                         // next IFD offset = none
        ];
        byte[] exifHeader = "Exif\0\0"u8.ToArray();
        int segLen = 2 /* the length field itself */ + exifHeader.Length + tiff.Length;

        var app1 = new byte[2 /* marker */ + segLen];
        app1[0] = 0xFF; app1[1] = 0xE1;
        app1[2] = (byte)(segLen >> 8);
        app1[3] = (byte)segLen;
        Buffer.BlockCopy(exifHeader, 0, app1, 4, exifHeader.Length);
        Buffer.BlockCopy(tiff, 0, app1, 4 + exifHeader.Length, tiff.Length);

        var result = new byte[2 + app1.Length + (jpeg.Length - 2)];
        result[0] = 0xFF; result[1] = 0xD8;
        Buffer.BlockCopy(app1, 0, result, 2, app1.Length);
        Buffer.BlockCopy(jpeg, 2, result, 2 + app1.Length, jpeg.Length - 2);
        return result;
    }

    static byte[] EncodeJpeg(SKBitmap bmp)
    {
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 100);
        return data.ToArray();
    }

    // 40 of 544 — the same tolerance PerceptualHashTests uses for a lossy re-encode.
    static readonly int Threshold = new DedupSettings().VariantMaxBits;

    public static IEnumerable<object[]> AllOrigins()
    {
        for (int v = 1; v <= 8; v++) yield return [v];
    }

    [Theory]
    [MemberData(nameof(AllOrigins))]
    public void Every_orientation_matches_the_untagged_upright_original(int exifValue)
    {
        using var upright = GoldenValueTests.Scene(320, 240);
        using var stored = AsStored(upright, exifValue);

        var uprightPath = Path.Combine(_dir, "upright.jpg");
        File.WriteAllBytes(uprightPath, EncodeJpeg(upright));

        var taggedPath = Path.Combine(_dir, $"origin_{exifValue}.jpg");
        File.WriteAllBytes(taggedPath, InjectOrientation(EncodeJpeg(stored), (ushort)exifValue));

        using var uprightDecoded = _imaging.DecodeScaled(uprightPath, 512);
        using var taggedDecoded = _imaging.DecodeScaled(taggedPath, 512);
        Assert.NotNull(uprightDecoded);
        Assert.NotNull(taggedDecoded);

        // Dimensions must be post-orientation: a 90/270 tag on a 320x240 source must report as
        // decoded (240x320-ish after the shared scaled decode), matching the untagged upright's
        // own decoded dimensions exactly.
        Assert.Equal(uprightDecoded!.Width, taggedDecoded!.Width);
        Assert.Equal(uprightDecoded.Height, taggedDecoded.Height);

        var (ug, uw, uh) = SkiaImageService.GrayFrom(uprightDecoded, 512);
        var (tg, tw, th) = SkiaImageService.GrayFrom(taggedDecoded, 512);
        var uprightHash = PerceptualHash.FromGray(ug, uw, uh);
        var taggedHash = PerceptualHash.FromGray(tg, tw, th);

        var d = uprightHash.DistanceTo(taggedHash, PerceptualHash.Bits, allowRotation: false);
        Assert.True(d <= Threshold,
            $"SKEncodedOrigin value {exifValue} should decode to the upright hash; distance was {d}");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }
}
