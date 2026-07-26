using SnapZap.Core.Data;

namespace SnapZap.Core.Dedup;

/// <summary>How a detector picks the member it suggests keeping.</summary>
public enum KeeperRule
{
    /// <summary>Most pixels, then largest file, then lowest id. For copies of one photo, where
    /// "largest at the same dimensions" means the less-compressed original.</summary>
    HighestResolution,

    /// <summary>Sharpest (highest Laplacian variance), then most pixels. For bursts, where the
    /// frames are different photographs and resolution says nothing about which one is good.</summary>
    Sharpest,
}

/// <summary>Writes detector output to the catalogue, so every detector stores groups identically.</summary>
public static class StoreGroups
{
    public static void Write(
        Database db,
        DupeKind kind,
        string? similarity,
        IReadOnlyList<IReadOnlyList<long>> groups,
        IReadOnlyDictionary<long, HashedImage> byId,
        KeeperRule rule)
    {
        if (groups.Count == 0) return;

        var repo = new DupeRepository(db);
        using var tx = db.Connection.BeginTransaction();
        foreach (var group in groups)
        {
            var members = group.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            if (members.Count < 2) continue;

            var keeper = Keeper(members, rule);
            repo.AddGroup(kind, similarity,
                members.Select(m => (m.Id, m.Id == keeper)).ToList());
        }
        tx.Commit();
    }

    /// <remarks>
    /// Every ordering ends on <c>Id</c>. Without that last key a tie is broken by enumeration
    /// order, and the keeper flag — which the user may have spent an evening overriding — would
    /// land on a different photo on the next run for no reason they could see.
    /// </remarks>
    static long Keeper(List<HashedImage> members, KeeperRule rule) => rule switch
    {
        KeeperRule.Sharpest => members
            .OrderByDescending(m => m.BlurScore ?? double.NegativeInfinity)
            .ThenByDescending(m => m.Pixels)
            .ThenByDescending(m => m.Bytes)
            .ThenBy(m => m.Id)
            .First().Id,

        _ => members
            .OrderByDescending(m => m.Pixels)
            .ThenByDescending(m => m.Bytes)
            .ThenBy(m => m.Id)
            .First().Id,
    };
}
