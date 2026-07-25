using SkiaSharp;

namespace SnapZap.Core.Imaging;

public sealed record DecodedInfo(int Width, int Height, string Format);

/// <summary>
/// Image decode, dimension probe, and thumbnail generation via SkiaSharp (MIT).
/// Also exposes a grayscale downscale used by the blur detector in step 4.
/// </summary>
public sealed class SkiaImageService
{
    public int ThumbMaxEdge { get; init; } = 320;
    public int ThumbQuality { get; init; } = 80;

    /// <summary>Cheap header decode for dimensions + format. Returns null if not a decodable image.</summary>
    public DecodedInfo? Probe(string path)
    {
        using var codec = SKCodec.Create(path);
        if (codec is null) return null;
        var info = codec.Info;
        return new DecodedInfo(info.Width, info.Height, codec.EncodedFormat.ToString());
    }

    /// <summary>
    /// Decode, downscale to fit <see cref="ThumbMaxEdge"/>, write a JPEG thumbnail to
    /// <paramref name="thumbPath"/>. Returns false if the source can't be decoded.
    /// </summary>
    public bool WriteThumbnail(string sourcePath, string thumbPath)
    {
        using var bitmap = SKBitmap.Decode(sourcePath);
        if (bitmap is null) return false;

        var (w, h) = FitWithin(bitmap.Width, bitmap.Height, ThumbMaxEdge);
        using var scaled = bitmap.Resize(new SKImageInfo(w, h), SKSamplingOptions.Default);
        if (scaled is null) return false;

        var dir = Path.GetDirectoryName(thumbPath)!;
        Directory.CreateDirectory(dir);
        using var image = SKImage.FromBitmap(scaled);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, ThumbQuality);

        // Duplicates share a content hash and therefore a thumbnail path. Write to a unique
        // temp file then atomically move into place, so concurrent writers for identical
        // images never collide on File.Create.
        var tmp = Path.Combine(dir, Path.GetRandomFileName());
        try
        {
            using (var fs = File.Create(tmp)) data.SaveTo(fs);
            File.Move(tmp, thumbPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) try { File.Delete(tmp); } catch { }
        }
        return true;
    }

    /// <summary>Decode to a small single-channel (grayscale) float buffer for blur analysis (step 4).</summary>
    public (float[] gray, int w, int h)? DecodeGray(string path, int maxEdge = 512)
    {
        using var bitmap = SKBitmap.Decode(path);
        if (bitmap is null) return null;
        var (w, h) = FitWithin(bitmap.Width, bitmap.Height, maxEdge);
        using var scaled = bitmap.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul),
                                         SKSamplingOptions.Default);
        if (scaled is null) return null;

        var px = scaled.Pixels;
        var gray = new float[w * h];
        for (int i = 0; i < px.Length; i++)
            gray[i] = 0.299f * px[i].Red + 0.587f * px[i].Green + 0.114f * px[i].Blue;
        return (gray, w, h);
    }

    static (int w, int h) FitWithin(int w, int h, int maxEdge)
    {
        if (w <= maxEdge && h <= maxEdge) return (w, h);
        var scale = (double)maxEdge / Math.Max(w, h);
        return (Math.Max(1, (int)(w * scale)), Math.Max(1, (int)(h * scale)));
    }
}
