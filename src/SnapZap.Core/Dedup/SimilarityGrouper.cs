namespace SnapZap.Core.Dedup;

/// <summary>One in-threshold pair, as fed to the grouper.</summary>
public readonly record struct SimilarPair(long A, long B, int Distance);

/// <summary>
/// The result of grouping, including whether anything was left out.
/// </summary>
/// <param name="Groups">Each group is a clique: every member within threshold of every other.</param>
/// <param name="Truncated">
/// True when the candidate pair list hit its cap and matching is therefore incomplete. Surfaced to
/// the user rather than swallowed — a silently truncated result reads as "we checked everything".
/// </param>
public sealed record GroupingResult(IReadOnlyList<IReadOnlyList<long>> Groups, bool Truncated);

/// <summary>
/// Turns a list of in-threshold pairs into duplicate groups, without letting near-duplicate
/// chains collapse into one enormous group.
/// </summary>
/// <remarks>
/// <para><b>Why this is not union-find.</b> Perceptual similarity is not transitive: A~B at
/// distance 8 and B~C at distance 8 says nothing about A~C, which may be 16 and well outside the
/// threshold. Union-find merges on any single edge, so on a real library — where photos form long
/// chains of gradual variation — it produces one group of several thousand photos. The keeper
/// heuristic then nominates all but one of them for deletion. That is not a cosmetic bug; it is
/// the tool destroying a library.</para>
///
/// <para><b>What it does instead.</b> Complete linkage: a group is a clique, so a photo joins only
/// if it is within threshold of <em>every</em> member already there. Chaining becomes structurally
/// impossible rather than merely unlikely.</para>
///
/// <para><b>The <c>within</c> predicate must be symmetric, and this is not a formality.</b> Nothing
/// here controls which way round a pair is tested — <see cref="Admit"/> asks
/// <c>within(member, candidate)</c> and <see cref="Merge"/> asks <c>within(left, right)</c>, both
/// determined by the order grouping happened to reach them. When
/// <see cref="PerceptualHash.DistanceTo"/> was one-sided, that made the clique guarantee a
/// one-directional check, and a real library produced a seven-member Variant group holding pairs
/// 259 bits apart under a 20-bit threshold. The predicate is symmetric now; a future caller that
/// passes an asymmetric one reopens exactly that hole.</para>
///
/// <para><b>Why closest-first.</b> Pairs are processed in ascending distance, so the tightest
/// matches claim each other before looser ones get a say, and the groups come out the shape a
/// human would draw. The tiebreak on ids is not cosmetic either: the same catalogue has to produce
/// the same groups on every run, or the keeper the user picked last week lands on a different
/// photo this week.</para>
/// </remarks>
public static class SimilarityGrouper
{
    /// <summary>
    /// Ceiling on candidate pairs. A pathological library — thousands of near-identical frames —
    /// is quadratic in pairs, and materialising that list would exhaust memory long before it
    /// produced a useful answer. Hitting the cap is reported, never silently absorbed.
    /// </summary>
    public const int DefaultPairCap = 2_000_000;

    /// <param name="pairs">
    /// Every pair already known to be within threshold. Order does not matter — this sorts.
    /// </param>
    /// <param name="within">
    /// Whether two images are within threshold. Called when admitting a member to an existing
    /// group, for pairs that may not appear in <paramref name="pairs"/> at all.
    /// </param>
    public static GroupingResult Group(
        IEnumerable<SimilarPair> pairs,
        Func<long, long, bool> within,
        int pairCap = DefaultPairCap)
    {
        var ordered = new List<SimilarPair>();
        bool truncated = false;
        foreach (var p in pairs)
        {
            if (ordered.Count >= pairCap) { truncated = true; break; }
            ordered.Add(p);
        }

        // Ascending by distance; ids break ties so the output is a function of the catalogue and
        // not of enumeration order.
        ordered.Sort(static (x, y) =>
        {
            int c = x.Distance.CompareTo(y.Distance);
            if (c != 0) return c;
            c = x.A.CompareTo(y.A);
            return c != 0 ? c : x.B.CompareTo(y.B);
        });

        var groups = new List<List<long>>();
        var owner = new Dictionary<long, int>();   // image id -> index into groups

        foreach (var (a, b, _) in ordered)
        {
            bool haveA = owner.TryGetValue(a, out var ga);
            bool haveB = owner.TryGetValue(b, out var gb);

            if (!haveA && !haveB)
            {
                groups.Add([a, b]);
                owner[a] = owner[b] = groups.Count - 1;
            }
            else if (haveA && !haveB)
            {
                Admit(groups[ga], ga, b, within, owner);
            }
            else if (!haveA)
            {
                Admit(groups[gb], gb, a, within, owner);
            }
            else if (ga != gb)
            {
                Merge(groups, owner, ga, gb, within);
            }
            // ga == gb: already together, nothing to do.
        }

        // A group can lose members to nothing here, but a merge empties one side; drop the husks.
        var result = groups.Where(g => g.Count > 1)
                           .Select(g => { g.Sort(); return (IReadOnlyList<long>)g; })
                           .ToList();
        return new GroupingResult(result, truncated);
    }

    /// <summary>
    /// Add <paramref name="candidate"/> only if it is within threshold of every current member.
    /// </summary>
    /// <remarks>
    /// Rejection is deliberately quiet and non-final: the candidate stays unassigned and may still
    /// form its own group with something else later in the pass. Forcing it in because one edge
    /// matched is exactly the chaining this class exists to prevent.
    /// </remarks>
    static void Admit(
        List<long> group, int index, long candidate,
        Func<long, long, bool> within, Dictionary<long, int> owner)
    {
        foreach (var member in group)
            if (!within(member, candidate)) return;

        group.Add(candidate);
        owner[candidate] = index;
    }

    /// <summary>Merge two groups only if the union is still a clique.</summary>
    static void Merge(
        List<List<long>> groups, Dictionary<long, int> owner,
        int ga, int gb, Func<long, long, bool> within)
    {
        var left = groups[ga];
        var right = groups[gb];

        foreach (var a in left)
            foreach (var b in right)
                if (!within(a, b)) return;

        foreach (var b in right) owner[b] = ga;
        left.AddRange(right);
        right.Clear();
    }
}
