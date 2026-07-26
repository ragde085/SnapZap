using SnapZap.Core.Data;

namespace SnapZap.Core.Dedup;

/// <summary>
/// Drops duplicate-group rows that say nothing the surviving groups do not already say.
/// </summary>
/// <remarks>
/// <para><b>Why anything needs dropping.</b> The three detectors run over the same images and
/// their thresholds are nested, not disjoint: Exact is a byte match, Variant accepts everything
/// within <see cref="DedupSettings.VariantMaxBits"/>, Burst accepts everything within
/// <see cref="DedupSettings.BurstMaxBits"/> that also falls in an EXIF time window. A set of
/// byte-identical copies satisfies all three, so it was written three times. Measured on a
/// 38,668-photo library: 19 groups over 25 distinct photos, every Exact group shadowed by a
/// Variant group with an identical member set. The user paid for that twice — once in
/// "Review duplicates (19)" when there were 12 real findings, and once stepping through the same
/// pair of photos as group 1 and again as group 12.</para>
///
/// <para><b>Why the precedence is Exact, then Burst, then Variant.</b> Not most-to-least strict —
/// most-to-least <em>trustworthy about what the photos are</em>.</para>
///
/// <list type="bullet">
/// <item><b>Exact wins outright.</b> Byte-identical files are copies of one photograph and there
/// is nothing to weigh against that. In particular a copy inherits the original's EXIF, so copies
/// look exactly like frames taken at the same instant and were being pulled into Burst groups and
/// then withheld from bulk selection — the tool refusing to reclaim the most certain duplicates
/// it can find. Reclaimable dropped from 372 KB to 74 KB on the fixture library for that reason
/// alone.</item>
///
/// <item><b>Burst beats Variant.</b> This is the safety-critical edge and it is not the intuitive
/// order. Pixel distance cannot separate the two: measured on real files, two <em>different</em>
/// frames of one burst sit 9 bits apart while one photograph and its 50% resize sit 16 apart. Any
/// threshold that keeps genuine variants together also swallows genuine burst frames. Capture time
/// is the only evidence that distinguishes them, so when both detectors claim the same photos, the
/// one that consulted the clock is the one to believe — and believing it errs toward review rather
/// than toward Delete.</item>
/// </list>
///
/// <para><b>Only exact cover is dropped.</b> A group is removed when some stronger group contains
/// every one of its members. Two groups that merely overlap are two different claims about
/// different sets and both survive; collapsing those would silently merge photographs the
/// detectors deliberately kept apart.</para>
/// </remarks>
public static class GroupReconciler
{
    /// <summary>Most trustworthy first. Ties are impossible: a group has exactly one kind.</summary>
    static int Rank(DupeKind kind) => kind switch
    {
        DupeKind.Exact => 0,
        DupeKind.Burst => 1,
        _ => 2,
    };

    /// <summary>
    /// Remove every group whose members are all covered by a single stronger group.
    /// </summary>
    /// <returns>How many groups were dropped.</returns>
    public static int Reconcile(Database db, string? root)
    {
        var repo = new DupeRepository(db);

        // Scoped to the root for the same reason detection is: a group the user cannot see is not
        // one they can act on, and reconciling against it would drop findings from the folder that
        // is on screen.
        var groups = repo.Groups()
            .Where(g => g.Members.Count >= 2)
            .Select(g => (g.Id, g.Kind, Members: g.Members.Select(m => m.ImageId).ToHashSet()))
            .OrderBy(g => Rank(g.Kind))
            .ThenBy(g => g.Id)
            .ToList();

        if (root is not null)
        {
            var inScope = new ImageRepository(db).Under(root).Select(i => i.Id).ToHashSet();
            groups = groups.Where(g => g.Members.Overlaps(inScope)).ToList();
        }

        var doomed = new List<long>();
        for (var weak = 0; weak < groups.Count; weak++)
        {
            for (var strong = 0; strong < groups.Count; strong++)
            {
                if (strong == weak) continue;

                // Strictly stronger, or the same kind and written first. The second case retires
                // an exact restatement of an earlier group of the same kind, which a re-run over
                // an overlapping root can otherwise leave behind.
                var better = Rank(groups[strong].Kind) < Rank(groups[weak].Kind)
                          || (groups[strong].Kind == groups[weak].Kind && groups[strong].Id < groups[weak].Id);

                if (better && groups[weak].Members.IsSubsetOf(groups[strong].Members))
                {
                    doomed.Add(groups[weak].Id);
                    break;
                }
            }
        }

        foreach (var id in doomed) repo.DeleteGroup(id);
        return doomed.Count;
    }
}
