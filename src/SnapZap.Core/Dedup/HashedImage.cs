namespace SnapZap.Core.Dedup;

/// <summary>
/// The columns a perceptual detector needs, loaded once per run.
/// </summary>
/// <remarks>
/// Deliberately not <c>ImageRecord</c>. A detector holds every row in the scope in memory at once
/// and touches them in a tight n-squared loop; carrying paths, thumbnails and scores through that
/// would multiply the working set for fields it never reads.
/// </remarks>
public sealed record HashedImage(
    long Id,
    PerceptualHash Hash,
    long Pixels,
    long Bytes,
    long? TakenUtc,
    string? Camera,
    double? BlurScore);

/// <summary>What one detector did, for the run's status line.</summary>
public sealed record DetectorResult(int Groups, bool Truncated)
{
    public static readonly DetectorResult None = new(0, false);
}
