using SnapZap.Core.Data;

namespace SnapZap.Core.Dedup;

/// <summary>
/// Groups frames of one burst: the same camera, seconds apart, and visually close enough that the
/// camera plainly did not move to a different subject in between.
/// </summary>
/// <remarks>
/// <para><b>These are not duplicates.</b> Five frames of the same scene are five different
/// photographs, and exactly one of them is the one where the kid was smiling. That is why
/// <see cref="DupeKind.Burst"/> is excluded from every bulk selection, why the detector is off by
/// default, and why the keeper here is the sharpest frame rather than the largest file.</para>
///
/// <para><b>Time first, pixels second.</b> Candidates come from the EXIF window, so the hash is
/// only ever compared between frames already known to be seconds apart — which is why this
/// detector is effectively free while <see cref="VariantFinder"/> is quadratic. The hash gate is
/// there to stop a camera left running across two different scenes from merging them, not to
/// establish that the frames are the same photo; it is correspondingly loose.</para>
///
/// <para>Photos with no EXIF timestamp are skipped outright. A burst is defined by capture time,
/// and file mtime is not capture time — a bulk copy rewrites every mtime to the same second and
/// would present an entire library as one enormous burst.</para>
/// </remarks>
public sealed class BurstFinder(Database db)
{
    public DetectorResult FindAndStore(
        IReadOnlyList<HashedImage> images, DedupSettings settings, string? root, CancellationToken ct = default)
    {
        var repo = new DupeRepository(db);
        repo.ClearKind(DupeKind.Burst, root);

        var usable = images
            .Where(i => !i.Hash.IsEmpty && i.TakenUtc is not null)
            .OrderBy(i => i.Camera ?? "", StringComparer.Ordinal)
            .ThenBy(i => i.TakenUtc!.Value)
            .ThenBy(i => i.Id)
            .ToList();
        if (usable.Count < 2) return DetectorResult.None;

        int window = settings.BurstWindowSeconds;
        int threshold = settings.BurstMaxBits;
        var byId = usable.ToDictionary(i => i.Id);

        var pairs = new List<SimilarPair>();
        for (int i = 0; i < usable.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var a = usable[i];

            // Sorted by (camera, time), so the candidates for a are the run of rows immediately
            // after it, and the walk stops at the first frame outside the window or on another
            // camera. No index needed, and no pair is examined twice.
            for (int j = i + 1; j < usable.Count; j++)
            {
                var b = usable[j];
                if (!string.Equals(a.Camera ?? "", b.Camera ?? "", StringComparison.Ordinal)) break;
                if (b.TakenUtc!.Value - a.TakenUtc!.Value > window) break;

                var d = a.Hash.DistanceTo(b.Hash, threshold, allowRotation: false);
                if (d <= threshold) pairs.Add(new SimilarPair(a.Id, b.Id, d));
            }
        }

        var result = SimilarityGrouper.Group(
            pairs,
            (x, y) => Within(byId[x], byId[y], window, threshold));

        StoreGroups.Write(db, DupeKind.Burst, $"within {window}s",
                          result.Groups, byId, KeeperRule.Sharpest);

        return new DetectorResult(result.Groups.Count, result.Truncated);
    }

    /// <summary>
    /// The clique test for burst membership: same camera, inside the window, and visually close.
    /// </summary>
    /// <remarks>
    /// The time check has to be repeated here, not just at candidate generation. Without it a long
    /// sequence of overlapping windows would satisfy the grouper pairwise and chain a continuous
    /// half-hour of shooting into one "burst" — the same non-transitivity problem the grouper
    /// exists to solve, arriving through the time axis instead of the pixel axis.
    /// </remarks>
    static bool Within(HashedImage a, HashedImage b, int window, int threshold) =>
        string.Equals(a.Camera ?? "", b.Camera ?? "", StringComparison.Ordinal)
        && Math.Abs(a.TakenUtc!.Value - b.TakenUtc!.Value) <= window
        && a.Hash.DistanceTo(b.Hash, threshold, allowRotation: false) <= threshold;
}
