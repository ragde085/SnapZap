using SnapZap.Core.Imaging;

namespace SnapZap.Core.Analysis;

/// <summary>
/// Blur score via variance-of-Laplacian: convolve grayscale with the 3×3 Laplacian kernel
/// and take the variance of the response. Sharp images have high-frequency edges → high
/// variance; blurry images → low. The absolute value is resolution- and content-dependent,
/// so the UI exposes the threshold as a slider (DESIGN §8) rather than hardcoding a cutoff.
///
/// Higher score = sharper. Lower = blurrier.
/// </summary>
public sealed class BlurDetector(SkiaImageService imaging)
{
    /// <summary>Working resolution for the Laplacian; normalizes score across image sizes.</summary>
    public int WorkEdge { get; init; } = 512;

    public double? Score(string imagePath)
    {
        var decoded = imaging.DecodeGray(imagePath, WorkEdge);
        if (decoded is not { } d) return null;
        return Variance(Laplacian(d.gray, d.w, d.h));
    }

    static float[] Laplacian(float[] g, int w, int h)
    {
        // 3×3 kernel:  0  1  0 / 1 -4  1 / 0  1  0. Interior pixels only.
        var outp = new float[w * h];
        for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                int i = y * w + x;
                outp[i] = g[i - w] + g[i + w] + g[i - 1] + g[i + 1] - 4f * g[i];
            }
        return outp;
    }

    static double Variance(float[] v)
    {
        // Only interior pixels carry a response; include all, mean is ~0 for the borders.
        double mean = 0;
        for (int i = 0; i < v.Length; i++) mean += v[i];
        mean /= v.Length;
        double acc = 0;
        for (int i = 0; i < v.Length; i++) { var d = v[i] - mean; acc += d * d; }
        return acc / v.Length;
    }
}
