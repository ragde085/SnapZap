namespace SnapZap.Core.Dedup;

/// <summary>
/// The columns a perceptual detector needs, loaded once per run.
/// </summary>
/// <remarks>
/// <para>Deliberately not <c>ImageRecord</c>. A detector holds every row in the scope in memory at
/// once and touches them in a tight n-squared loop; carrying thumbnails and scores through that
/// would multiply the working set for fields it never reads.</para>
///
/// <para><see cref="Path"/> is the one exception, and it earns its place: it is the only column
/// that is both unique and stable across runs, which is what makes keeper selection reproducible
/// (see <see cref="StoreGroups"/>). The n-squared loop never reads it — only the per-group
/// ordering does, once.</para>
/// </remarks>
public sealed record HashedImage(
    long Id,
    string Path,
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
