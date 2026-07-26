using System.Collections.Concurrent;
using SnapZap.Core.Data;

namespace SnapZap.Core.Dedup;

/// <summary>
/// Finds photos that are the same shot at a different resolution, encoding, format or
/// orientation — everything czkawka's similar-image pass used to cover, computed in-process from
/// the signatures the scan already produced.
/// </summary>
/// <remarks>
/// <para><b>Brute force, deliberately.</b> At 50k photos this is 1.25 billion pairs, which sounds
/// fatal and is not: unrelated photos differ in roughly half their 272 bits, so
/// <see cref="PerceptualHash.DistanceTo"/> blows the ceiling on the first 64-bit word and the pair
/// costs one XOR and one PopCount. Parallelised, the whole pass is well under a second. A BK-tree
/// or multi-index hashing would buy nothing here and cost a subtle index to get wrong; revisit
/// past roughly 150k photos (docs/DEDUP-V2.md §9).</para>
///
/// <para>Grouping is <see cref="SimilarityGrouper"/>'s problem, not this class's. All this does is
/// enumerate pairs.</para>
/// </remarks>
public sealed class VariantFinder(Database db)
{
    public DetectorResult FindAndStore(
        IReadOnlyList<HashedImage> images, DedupSettings settings, string? root, CancellationToken ct = default)
    {
        var repo = new DupeRepository(db);
        repo.ClearKind(DupeKind.Variant, root);

        var usable = images.Where(i => !i.Hash.IsEmpty).ToList();
        if (usable.Count < 2) return DetectorResult.None;

        int threshold = settings.VariantMaxBits;
        bool rotations = settings.VariantRotations;

        var byId = usable.ToDictionary(i => i.Id);
        var pairs = new ConcurrentBag<SimilarPair>();

        // Partitioned over the outer index; each pair is visited once (j > i).
        Parallel.For(0, usable.Count,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct },
            () => new List<SimilarPair>(),
            (i, _, local) =>
            {
                var a = usable[i];
                for (int j = i + 1; j < usable.Count; j++)
                {
                    var b = usable[j];
                    var d = a.Hash.DistanceTo(b.Hash, threshold, rotations);
                    if (d <= threshold) local.Add(new SimilarPair(a.Id, b.Id, d));
                }
                return local;
            },
            local => { foreach (var p in local) pairs.Add(p); });

        ct.ThrowIfCancellationRequested();

        var result = SimilarityGrouper.Group(
            pairs,
            (x, y) => byId[x].Hash.DistanceTo(byId[y].Hash, threshold, rotations) <= threshold);

        StoreGroups.Write(db, DupeKind.Variant, $"<={threshold} of {PerceptualHash.Bits} bits",
                          result.Groups, byId, KeeperRule.HighestResolution);

        return new DetectorResult(result.Groups.Count, result.Truncated);
    }
}
