using SnapZap.Core.Data;

namespace SnapZap.Core.Dedup;

/// <summary>Which group a photo is presented as belonging to, and whether it is that group's keeper.</summary>
/// <param name="BurstAdjacent">
/// True when <c>BurstFinder</c> saw this photo in a burst relationship, whether or not it reached
/// a burst group. See <see cref="ImageRepository.BurstAdjacentIds"/>.
/// </param>
public readonly record struct DupeAssignment(long GroupId, DupeKind Kind, bool IsKeeper, bool BurstAdjacent)
{
    /// <summary>
    /// Whether this photo may be swept up by a bulk "select the extra copies" action — the single
    /// predicate standing between a widened detector and a user losing photographs.
    /// </summary>
    /// <remarks>
    /// Three conditions, and all three are load-bearing:
    /// <list type="bullet">
    /// <item>Not the group's keeper — every group always retains one.</item>
    /// <item>The group's kind is bulk-selectable (Exact or Variant, never Burst).</item>
    /// <item>Not burst-adjacent. Kind alone is <b>not</b> enough: complete linkage can exclude a
    /// genuine burst frame from the clique, after which the only detector describing it is Variant
    /// — which is bulk-selectable. This is the condition that closes that hole.</item>
    /// </list>
    /// Read this rather than re-deriving the comparison; a badge, a filter and a count that answer
    /// the question three separate ways is precisely how they start disagreeing.
    /// </remarks>
    public bool IsBulkSelectableExtra => !IsKeeper && Kind.IsBulkSelectable() && !BurstAdjacent;
}

/// <summary>
/// Resolves the one group a photo is presented as belonging to, when it belongs to several.
/// </summary>
/// <remarks>
/// <para><b>Why a photo can be in several groups at once.</b> <see cref="GroupReconciler"/> drops a
/// group only when a stronger group contains <em>every</em> one of its members — deliberately, since
/// two merely-overlapping groups are two different claims. So a Variant group that is a strict
/// <em>superset</em> of a Burst group survives alongside it, and every frame of that burst is a
/// member of both.</para>
///
/// <para><b>Why that was dangerous.</b> Callers built this map by assigning in enumeration order,
/// so the last group to mention a photo won, arbitrarily. When the Variant group won, a burst frame
/// reported <see cref="DupeKind.Variant"/>, passed <c>IsBulkSelectable()</c>, and became eligible
/// for bulk deletion — defeating the burst guard entirely. Measured on a five-frame burst fixture:
/// a Burst group of four frames and a Variant group of all five, with every frame offered for
/// removal. A burst is five <em>different photographs</em>; that is the failure this type exists to
/// prevent.</para>
///
/// <para><b>The precedence is <see cref="GroupReconciler"/>'s, not a new one</b> — Exact, then
/// Burst, then Variant. Reusing it matters: the reconciler already argues why Exact wins outright
/// (byte-identical copies inherit EXIF, so they look like same-instant frames and were being
/// withheld from selection as if they were bursts) and why Burst beats Variant (pixel distance
/// cannot separate them, so the detector that consulted the capture clock is the one to believe).
/// Those arguments are just as true here, and having two orders would be a bug waiting to happen.</para>
/// </remarks>
public static class DupeAssignmentResolver
{
    /// <summary>
    /// Most trustworthy first. Mirrors <see cref="GroupReconciler"/>'s ranking — if one changes,
    /// change both, or a photo can be reconciled under one order and presented under another.
    /// </summary>
    public static int Precedence(DupeKind kind) => kind switch
    {
        DupeKind.Exact => 0,
        DupeKind.Burst => 1,
        _ => 2,
    };

    /// <summary>
    /// Maps every image mentioned by <paramref name="groups"/> to the single group it should be
    /// presented as belonging to.
    /// </summary>
    /// <param name="groups">
    /// Groups to resolve over. Callers that hide recycled members should filter first — a group
    /// that has dropped below two surviving members is no longer a duplicate of anything.
    /// </param>
    /// <remarks>
    /// Ties within a kind are broken by group id so the result is a function of the catalogue
    /// rather than of enumeration order — the same reproducibility rule the grouper follows.
    ///
    /// A group always keeps at least one keeper: if every keeper was recycled, the lowest-id
    /// survivor is promoted. Without that, every survivor reads <c>IsKeeper=false</c> and "select
    /// all extras" would arm a delete against every remaining copy of the photo.
    /// </remarks>
    /// <param name="burstAdjacent">
    /// Photos that took part in a burst relationship, from
    /// <see cref="ImageRepository.BurstAdjacentIds"/>. Optional only so tests that care solely
    /// about precedence need not supply it — production callers deciding selectability must.
    /// </param>
    public static Dictionary<long, DupeAssignment> Resolve(
        IEnumerable<DupeGroup> groups, IReadOnlySet<long>? burstAdjacent = null)
    {
        var result = new Dictionary<long, DupeAssignment>();

        foreach (var g in groups.OrderBy(g => Precedence(g.Kind)).ThenBy(g => g.Id))
        {
            var keeperIds = g.Members.Where(m => m.IsKeeper).Select(m => m.ImageId).ToHashSet();
            if (keeperIds.Count == 0 && g.Members.Count > 0)
                keeperIds.Add(g.Members.Min(m => m.ImageId));

            foreach (var m in g.Members)
            {
                // First writer wins, and the enumeration is ordered strongest-first — so a photo
                // in both a Burst and a Variant group is presented as Burst, and is therefore not
                // bulk-selectable.
                if (result.ContainsKey(m.ImageId)) continue;
                result[m.ImageId] = new DupeAssignment(
                    g.Id, g.Kind, keeperIds.Contains(m.ImageId),
                    burstAdjacent?.Contains(m.ImageId) ?? false);
            }
        }

        return result;
    }
}
