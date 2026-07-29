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

        // Protect every photo that took part in a burst *relationship*, which is a strictly wider
        // set than the members of the burst *groups* above.
        //
        // Grouping is complete linkage: a group is a clique, so a frame that is close to its
        // neighbour but too far from the far end of the run is correctly excluded. It is still a
        // burst frame. Having been excluded, the only detector left describing it is Variant —
        // which IS bulk-selectable — so it became eligible for deletion. Measured on a five-frame
        // fixture: the clique covered four frames, a superset Variant group covered all five, and
        // the fifth frame was offered for removal.
        //
        // The fix deliberately does NOT widen grouping. Single linkage here would chain a
        // continuous half-hour of shooting into one "burst" through overlapping time windows —
        // see Within's remarks, which is the same non-transitivity the grouper exists to solve.
        // Instead the groups stay exactly as they were and the *protection* widens, which errs the
        // safe way: the worst case is a photo that must be reviewed individually rather than
        // selected in bulk.
        MarkBurstAdjacent(pairs.SelectMany(p => new[] { p.A, p.B }).ToHashSet());

        return new DetectorResult(result.Groups.Count, result.Truncated);
    }

    /// <summary>
    /// Records the burst-adjacent set for this root. Cleared first, for the same reason
    /// <c>ClearKind</c> is: a rerun under different settings must not leave yesterday's protection
    /// behind, or a photo stays unselectable forever with nothing on screen explaining why.
    /// </summary>
    void MarkBurstAdjacent(IReadOnlyCollection<long> imageIds)
    {
        lock (db.WriteLock)
        {
            using var tx = db.Writer.BeginTransaction();

            using (var clear = db.Writer.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "UPDATE images SET burst_adjacent = 0 WHERE burst_adjacent = 1";
                clear.ExecuteNonQuery();
            }

            if (imageIds.Count > 0)
            {
                using var set = db.Writer.CreateCommand();
                set.Transaction = tx;
                set.CommandText = "UPDATE images SET burst_adjacent = 1 WHERE id = $id";
                var p = set.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
                foreach (var id in imageIds) { p.Value = id; set.ExecuteNonQuery(); }
            }

            tx.Commit();
        }
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
