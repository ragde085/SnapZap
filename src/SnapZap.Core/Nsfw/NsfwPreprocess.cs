using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;
using SnapZap.Core.Resources;

namespace SnapZap.Core.Nsfw;

/// <summary>
/// Pure, unit-testable preprocessing: decode → resize to Size×Size → rescale → normalize →
/// pack as NCHW float tensor (batch=1, channels=RGB). This is the highest-risk code in the
/// NSFW path (DESIGN §8), so it is isolated and directly asserted in tests independent of
/// the model.
/// </summary>
public static class NsfwPreprocess
{
    /// <summary>Build the input tensor from an already-decoded bitmap.</summary>
    /// <remarks>
    /// Reads pixels via <c>GetPixelSpan()</c> (raw RGBA bytes) and writes through
    /// <c>tensor.Buffer.Span</c> with a flat plane index, instead of the allocating
    /// <c>SKBitmap.Pixels</c> property and the four-dimensional tensor indexer. This is eligible
    /// under the NSFW no-quality-tradeoff rule (TD-1) only because it is bit-identical: same
    /// source bytes, same arithmetic, same result — the resize filter and decode are untouched.
    /// </remarks>
    public static DenseTensor<float> ToTensor(SKBitmap bitmap, PreprocessConfig cfg)
    {
        int s = cfg.Size;
        using var resized = bitmap.Resize(
            new SKImageInfo(s, s, SKColorType.Rgba8888, SKAlphaType.Unpremul),
            SKSamplingOptions.Default)
            ?? throw new InvalidOperationException(CoreStrings.Get("ResizeFailed"));

        var tensor = new DenseTensor<float>([1, 3, s, s]);
        var buf = tensor.Buffer.Span;
        var px = resized.GetPixelSpan(); // raw RGBA bytes, stride resized.RowBytes
        int plane = s * s;
        int stride = resized.RowBytes;

        int i = 0;
        for (int y = 0; y < s; y++)
        {
            int row = y * stride;
            for (int x = 0; x < s; x++, i++)
            {
                int off = row + x * 4;
                // rescale to [0,1] then (v - mean) / std, per channel.
                buf[i] = (px[off] * cfg.RescaleFactor - cfg.Mean[0]) / cfg.Std[0];
                buf[plane + i] = (px[off + 1] * cfg.RescaleFactor - cfg.Mean[1]) / cfg.Std[1];
                buf[2 * plane + i] = (px[off + 2] * cfg.RescaleFactor - cfg.Mean[2]) / cfg.Std[2];
            }
        }
        return tensor;
    }

    public static DenseTensor<float> ToTensor(string imagePath, PreprocessConfig cfg)
    {
        using var bmp = SKBitmap.Decode(imagePath)
            ?? throw new InvalidOperationException(CoreStrings.Get("CannotDecode", imagePath));
        return ToTensor(bmp, cfg);
    }

    /// <summary>Numerically stable softmax over a logit vector.</summary>
    public static float[] Softmax(ReadOnlySpan<float> logits)
    {
        float max = float.NegativeInfinity;
        foreach (var v in logits) if (v > max) max = v;
        var exp = new float[logits.Length];
        float sum = 0;
        for (int i = 0; i < logits.Length; i++) { exp[i] = MathF.Exp(logits[i] - max); sum += exp[i]; }
        for (int i = 0; i < exp.Length; i++) exp[i] /= sum;
        return exp;
    }
}
