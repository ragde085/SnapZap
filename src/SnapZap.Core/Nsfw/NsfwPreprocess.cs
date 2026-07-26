using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

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
    public static DenseTensor<float> ToTensor(SKBitmap bitmap, PreprocessConfig cfg)
    {
        int s = cfg.Size;
        using var resized = bitmap.Resize(
            new SKImageInfo(s, s, SKColorType.Rgba8888, SKAlphaType.Unpremul),
            SKSamplingOptions.Default)
            ?? throw new InvalidOperationException("resize failed");

        var tensor = new DenseTensor<float>([1, 3, s, s]);
        var px = resized.Pixels; // row-major, length s*s
        for (int y = 0; y < s; y++)
        {
            for (int x = 0; x < s; x++)
            {
                var c = px[y * s + x];
                // rescale to [0,1] then (v - mean) / std, per channel.
                tensor[0, 0, y, x] = (c.Red * cfg.RescaleFactor - cfg.Mean[0]) / cfg.Std[0];
                tensor[0, 1, y, x] = (c.Green * cfg.RescaleFactor - cfg.Mean[1]) / cfg.Std[1];
                tensor[0, 2, y, x] = (c.Blue * cfg.RescaleFactor - cfg.Mean[2]) / cfg.Std[2];
            }
        }
        return tensor;
    }

    public static DenseTensor<float> ToTensor(string imagePath, PreprocessConfig cfg)
    {
        using var bmp = SKBitmap.Decode(imagePath)
            ?? throw new InvalidOperationException($"cannot decode {imagePath}");
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
