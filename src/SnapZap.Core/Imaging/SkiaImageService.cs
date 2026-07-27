using System.Runtime.InteropServices;
using SkiaSharp;

namespace SnapZap.Core.Imaging;

public sealed record DecodedInfo(int Width, int Height, string Format);

/// <summary>
/// One shared, correctly-sampled, correctly-oriented decode that thumbnail generation and the
/// greyscale (blur/perceptual-hash) pass both derive from, instead of each re-decoding the source
/// file. See <see cref="SkiaImageService.DecodeScaled"/>.
/// </summary>
public sealed class DecodedImage(SKBitmap bitmap, string format) : IDisposable
{
    public SKBitmap Bitmap { get; } = bitmap;
    public int Width => Bitmap.Width;
    public int Height => Bitmap.Height;
    public string Format { get; } = format;
    public void Dispose() => Bitmap.Dispose();
}

/// <summary>
/// Image decode, dimension probe, and thumbnail generation via SkiaSharp (MIT).
/// Also exposes a grayscale downscale used by the blur detector and perceptual hash.
/// </summary>
public sealed class SkiaImageService
{
    public int ThumbMaxEdge { get; init; } = 320;
    public int ThumbQuality { get; init; } = 80;

    /// <summary>Mid-size preview served by <c>/api/full/{id}</c> instead of the original file
    /// (Task 26) — big enough for the modal, far cheaper than a 24 MP original over the wire.</summary>
    public int PreviewMaxEdge { get; init; } = 1600;
    public int PreviewQuality { get; init; } = 88;

    /// <summary>
    /// Every downscale in the app uses this. <c>SKSamplingOptions.Default</c> is
    /// <c>Filter=Nearest, Mipmap=None</c> (verified, SkiaSharp 4.150.1) — point sampling, which
    /// keeps roughly 1 pixel in 61 on a 512-from-4000 decode and is exactly what
    /// <see cref="SnapZap.Core.Dedup.PerceptualHash.BoxSample"/>'s box-average exists to defend
    /// against. Linear+mipmap low-pass-filters before it discards pixels.
    /// </summary>
    static readonly SKSamplingOptions LinearMipmap = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    /// <summary>Cheap header decode for dimensions + format. Returns null if not a decodable image.</summary>
    /// <remarks>Pre-orientation dimensions — a caller that needs the as-displayed size (the scan
    /// does) should read it off a <see cref="DecodedImage"/> from <see cref="DecodeScaled"/> instead.</remarks>
    public DecodedInfo? Probe(string path)
    {
        using var codec = SKCodec.Create(path);
        if (codec is null) return null;
        var info = codec.Info;
        return new DecodedInfo(info.Width, info.Height, codec.EncodedFormat.ToString());
    }

    /// <summary>
    /// Decode once, scaled to approximately <paramref name="maxEdge"/> on the long side, oriented
    /// for display. Opens the codec, lets it pick a decode size via
    /// <c>GetScaledDimensions</c> (cheaper than a full-resolution decode followed by a resize —
    /// libjpeg-turbo can DCT-scale during decode itself), then applies
    /// <c>codec.EncodedOrigin</c> so the result reads as it should be displayed.
    /// </summary>
    /// <remarks>
    /// Decode at the larger of a scan's consumers' requirements (512 for the greyscale buffer, 320
    /// for the thumbnail) so both are derived from this one call via <see cref="ResizeWithin"/>,
    /// instead of decoding the source file twice. <c>GetScaledDimensions</c> returns the nearest
    /// codec-supported size, not the requested one (measured: 512/4000 → 500×375) — read the
    /// actual dimensions off the returned <see cref="DecodedImage"/> rather than assuming
    /// <paramref name="maxEdge"/>.
    /// </remarks>
    public DecodedImage? DecodeScaled(string path, int maxEdge)
    {
        using var codec = SKCodec.Create(path);
        if (codec is null) return null;
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0) return null;

        var scale = (float)maxEdge / Math.Max(info.Width, info.Height);
        var dims = scale >= 1f ? new SKSizeI(info.Width, info.Height) : codec.GetScaledDimensions(scale);
        if (dims.Width <= 0 || dims.Height <= 0) return null;

        var decodeInfo = new SKImageInfo(dims.Width, dims.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bitmap = SKBitmap.Decode(codec, decodeInfo);
        if (bitmap is null) return null;

        var oriented = ApplyOrientation(bitmap, codec.EncodedOrigin);
        if (!ReferenceEquals(oriented, bitmap)) bitmap.Dispose();

        return new DecodedImage(oriented, codec.EncodedFormat.ToString());
    }

    /// <summary>Resize a decoded bitmap to fit within <paramref name="maxEdge"/>, linear+mipmap
    /// sampled. Returns <paramref name="src"/> itself (not a copy) when it already fits — callers
    /// must check <c>ReferenceEquals</c> before disposing.</summary>
    static SKBitmap ResizeWithin(SKBitmap src, int maxEdge)
    {
        var (w, h) = FitWithin(src.Width, src.Height, maxEdge);
        if (w == src.Width && h == src.Height) return src;
        return src.Resize(new SKImageInfo(w, h, src.ColorType, src.AlphaType), LinearMipmap) ?? src;
    }

    /// <summary>
    /// Encode a JPEG thumbnail from an already-decoded, already-oriented bitmap. Splitting this
    /// from decoding is what lets the scan derive a thumbnail and the greyscale buffer from one
    /// shared <see cref="DecodedImage"/> (<see cref="DecodeScaled"/>) rather than decoding the
    /// source file a second time.
    /// </summary>
    public bool WriteThumbnailFrom(DecodedImage decoded, string thumbPath)
    {
        var scaled = ResizeWithin(decoded.Bitmap, ThumbMaxEdge);
        try { return EncodeJpegAtomic(scaled, thumbPath, ThumbQuality); }
        finally
        {
            if (!ReferenceEquals(scaled, decoded.Bitmap)) scaled.Dispose();
        }
    }

    /// <summary>
    /// Decode, downscale to fit <see cref="PreviewMaxEdge"/>, write a JPEG preview to
    /// <paramref name="previewPath"/>. Returns false if the source can't be decoded, so the caller
    /// can fall back to serving the original file (Task 26) rather than a broken response.
    /// </summary>
    /// <remarks>Orientation-normalised like every other decode here — a preview generated from
    /// <see cref="DecodeScaled"/> bakes the display rotation into its pixels, unlike the original
    /// file (which relies on the browser applying EXIF), so the modal and the grid never disagree
    /// about which way up a photo goes.</remarks>
    public bool WritePreview(string sourcePath, string previewPath)
    {
        using var decoded = DecodeScaled(sourcePath, PreviewMaxEdge);
        if (decoded is null) return false;

        var scaled = ResizeWithin(decoded.Bitmap, PreviewMaxEdge);
        try { return EncodeJpegAtomic(scaled, previewPath, PreviewQuality); }
        finally
        {
            if (!ReferenceEquals(scaled, decoded.Bitmap)) scaled.Dispose();
        }
    }

    static bool EncodeJpegAtomic(SKBitmap bitmap, string path, int quality)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);

        // Duplicates share a content hash and therefore a cache path. Write to a unique temp file
        // then atomically move into place, so concurrent writers for identical images (or a
        // concurrent request for the same preview) never collide on File.Create.
        var tmp = Path.Combine(dir, Path.GetRandomFileName());
        try
        {
            using (var fs = File.Create(tmp)) data.SaveTo(fs);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) try { File.Delete(tmp); } catch { }
        }
        return true;
    }

    /// <summary>
    /// Decode, downscale to fit <see cref="ThumbMaxEdge"/>, write a JPEG thumbnail to
    /// <paramref name="thumbPath"/>. Returns false if the source can't be decoded.
    /// </summary>
    /// <remarks>Standalone entry point for a caller with nothing else to derive from the same
    /// decode. The scan path uses <see cref="DecodeScaled"/> + <see cref="WriteThumbnailFrom"/>
    /// so the decode is shared with the greyscale pass instead of repeated.</remarks>
    public bool WriteThumbnail(string sourcePath, string thumbPath)
    {
        using var decoded = DecodeScaled(sourcePath, ThumbMaxEdge);
        return decoded is not null && WriteThumbnailFrom(decoded, thumbPath);
    }

    /// <summary>Greyscale buffer for blur/perceptual-hash analysis, from an already-decoded bitmap.</summary>
    public static (float[] gray, int w, int h) GrayFrom(DecodedImage decoded, int maxEdge)
    {
        var scaled = ResizeWithin(decoded.Bitmap, maxEdge);
        try
        {
            int w = scaled.Width, h = scaled.Height;
            var px = MemoryMarshal.Cast<byte, uint>(scaled.GetPixelSpan());
            var gray = new float[w * h];
            int stride = scaled.RowBytes / 4;
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    var argb = px[row + x];
                    // Rgba8888: byte order R,G,B,A in memory == 0xAABBGGRR when read as a little-endian uint.
                    byte r = (byte)argb, g = (byte)(argb >> 8), b = (byte)(argb >> 16);
                    gray[y * w + x] = 0.299f * r + 0.587f * g + 0.114f * b;
                }
            }
            return (gray, w, h);
        }
        finally
        {
            if (!ReferenceEquals(scaled, decoded.Bitmap)) scaled.Dispose();
        }
    }

    /// <summary>Decode to a small single-channel (grayscale) float buffer for blur/perceptual-hash
    /// analysis. Standalone entry point (tests, and any caller with no thumbnail to derive
    /// alongside) — the scan path shares one <see cref="DecodeScaled"/> result instead.</summary>
    public (float[] gray, int w, int h)? DecodeGray(string path, int maxEdge = 512)
    {
        using var decoded = DecodeScaled(path, maxEdge);
        return decoded is null ? null : GrayFrom(decoded, maxEdge);
    }

    static (int w, int h) FitWithin(int w, int h, int maxEdge)
    {
        if (w <= maxEdge && h <= maxEdge) return (w, h);
        var scale = (double)maxEdge / Math.Max(w, h);
        return (Math.Max(1, (int)(w * scale)), Math.Max(1, (int)(h * scale)));
    }

    // ---- EXIF orientation (tech-spec §6.2 / Task 14) ------------------------------------------

    /// <summary>
    /// Apply the transform <paramref name="origin"/> implies, so decoded pixels read as they
    /// should be displayed rather than as the sensor stored them.
    /// </summary>
    /// <remarks>
    /// <para><b><c>SKBitmap.Decode</c> ignores EXIF orientation entirely.</b> Verified against a
    /// JPEG tagged <c>Orientation=6</c>: <c>SKCodec.EncodedOrigin</c> correctly reports
    /// <c>RightTop</c>, but the decoded bitmap comes back unrotated. Every perceptual hash was
    /// therefore computed on raw sensor pixels, and a portrait-tagged photo's thumbnail rendered
    /// sideways while <c>/api/full/{id}</c> — served as the original file — rendered upright,
    /// because the browser applies EXIF orientation and this decoder did not.</para>
    ///
    /// <para><b>All eight values are handled explicitly</b>, via direct pixel remapping rather
    /// than canvas transforms — a canvas matrix stack is easy to get subtly wrong for the four
    /// mirrored forms (<c>TopRight</c>, <c>BottomLeft</c>, <c>LeftTop</c>, <c>RightBottom</c>),
    /// which is exactly the failure mode <see href="../../../tests/SnapZap.Tests/AnalysisTests.cs">
    /// AC 5</see> exists to catch: every one of the eight must hash identically to the untagged
    /// upright original.</para>
    /// </remarks>
    /// <summary>Internal rather than private so tests can build "as the camera stored it" fixture
    /// bytes for all eight <see cref="SKEncodedOrigin"/> values (AC 5) without re-deriving the
    /// transform a second time and risking the two disagreeing.</summary>
    internal static SKBitmap ApplyOrientation(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft) return src; // identity; Default shares this value

        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
                            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int sw = src.Width, sh = src.Height;
        int dw = swap ? sh : sw;
        int dh = swap ? sw : sh;

        var dst = new SKBitmap(new SKImageInfo(dw, dh, src.ColorType, src.AlphaType));
        var s = MemoryMarshal.Cast<byte, uint>(src.GetPixelSpan());
        var d = MemoryMarshal.Cast<byte, uint>(dst.GetPixelSpan());
        int sStride = src.RowBytes / 4, dStride = dst.RowBytes / 4;

        for (int y = 0; y < dh; y++)
        {
            int dRow = y * dStride;
            for (int x = 0; x < dw; x++)
            {
                // Formulas derived directly from the EXIF orientation definitions (each destination
                // pixel's source coordinate), not composed canvas transforms — see remarks above.
                var (sx, sy) = origin switch
                {
                    SKEncodedOrigin.TopRight => (sw - 1 - x, y),                    // mirror horizontal
                    SKEncodedOrigin.BottomRight => (sw - 1 - x, sh - 1 - y),         // rotate 180
                    SKEncodedOrigin.BottomLeft => (x, sh - 1 - y),                  // mirror vertical
                    SKEncodedOrigin.LeftTop => (y, x),                              // transpose
                    SKEncodedOrigin.RightTop => (y, sh - 1 - x),                    // rotate 90 CW
                    SKEncodedOrigin.RightBottom => (sw - 1 - y, sh - 1 - x),         // transverse
                    SKEncodedOrigin.LeftBottom => (sw - 1 - y, x),                  // rotate 270 CW
                    _ => (x, y),
                };
                d[dRow + x] = s[sy * sStride + sx];
            }
        }
        return dst;
    }
}
