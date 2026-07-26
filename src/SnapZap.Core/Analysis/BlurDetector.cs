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
        return ScoreFrom(d.gray, d.w, d.h);
    }

    /// <summary>Score an already-decoded greyscale buffer.</summary>
    /// <remarks>
    /// Split out from <see cref="Score"/> so the scan can decode once and derive both this and the
    /// perceptual signature from the same buffer. Decoding is by far the most expensive thing the
    /// scan does; the previous arrangement paid for it twice per image, and the czkawka subprocess
    /// paid for it a third time in its own process.
    ///
    /// The Laplacian response and its sum / sum-of-squares are accumulated in one interior-only
    /// pass, rather than materialising a full <c>w*h</c> intermediate buffer and walking it twice
    /// (once for the mean, once for the squared deviations). Border pixels carry no response and
    /// are never visited, exactly as they were implicitly zero in the old buffer.
    ///
    /// <b>The denominator is <c>w*h</c>, not the interior count <c>(w-2)*(h-2)</c>.</b> This looks
    /// like a bug and is load-bearing: the old <c>Variance</c> divided by the full zero-initialized
    /// buffer's length, so writing the "obvious" fused version — dividing by the interior count
    /// this loop naturally visits — would silently re-scale every score by roughly
    /// <c>wh/((w-2)(h-2))</c>. The blur threshold is a user-facing slider already calibrated
    /// against the old scale; re-scaling it would move every photo's blur verdict without the user
    /// changing anything. Verified against golden values captured before this change (AC 12).
    /// </remarks>
    public static double? ScoreFrom(float[] gray, int w, int h)
    {
        if (w < 3 || h < 3) return null;

        double sum = 0, sumSq = 0;
        for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                int i = y * w + x;
                // 3×3 kernel:  0  1  0 / 1 -4  1 / 0  1  0.
                float lap = gray[i - w] + gray[i + w] + gray[i - 1] + gray[i + 1] - 4f * gray[i];
                sum += lap;
                sumSq += (double)lap * lap;
            }

        long n = (long)w * h;
        double mean = sum / n;
        return sumSq / n - mean * mean;
    }
}
