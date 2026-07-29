using SnapZap.Core;
using SnapZap.Core.Dedup;
using Xunit;

namespace SnapZap.Tests;

/// <summary>
/// Pins the rule that decides which group a photo is presented as belonging to when it belongs to
/// several — the rule standing between the burst guard and a user losing photographs.
/// </summary>
public class DupeAssignmentTests
{
    static DupeGroup Group(long id, DupeKind kind, params long[] memberIds) => new()
    {
        Id = id,
        Kind = kind,
        Members = memberIds.Select(m => new DupeMember { ImageId = m, IsKeeper = m == memberIds[0] }).ToList(),
    };

    /// <summary>
    /// The regression this type exists for, taken from a real five-frame burst fixture: the burst
    /// detector's complete-linkage clique covered four frames, while the looser variant threshold
    /// grouped all five. The variant group is a strict superset, so GroupReconciler correctly keeps
    /// both — and every frame was then presented as Variant and offered for bulk deletion.
    /// </summary>
    [Fact]
    public void A_frame_in_both_a_burst_and_a_superset_variant_group_is_presented_as_burst()
    {
        var burst = Group(4, DupeKind.Burst, 2, 3, 4, 5);
        var variant = Group(2, DupeKind.Variant, 1, 2, 3, 4, 5);

        var map = DupeAssignmentResolver.Resolve([variant, burst]);

        foreach (var id in new long[] { 2, 3, 4, 5 })
        {
            Assert.Equal(DupeKind.Burst, map[id].Kind);
            Assert.False(map[id].Kind.IsBulkSelectable(), $"image {id} must not be bulk-selectable");
        }
    }

    /// <summary>
    /// Enumeration order must not change the answer. The bug being fixed was precisely an
    /// order-dependent assignment ("last group mentioned wins").
    /// </summary>
    [Fact]
    public void Resolution_does_not_depend_on_the_order_groups_arrive_in()
    {
        var burst = Group(4, DupeKind.Burst, 2, 3);
        var variant = Group(2, DupeKind.Variant, 1, 2, 3);

        var a = DupeAssignmentResolver.Resolve([variant, burst]);
        var b = DupeAssignmentResolver.Resolve([burst, variant]);

        Assert.Equal(a, b);
        Assert.Equal(DupeKind.Burst, a[2].Kind);
    }

    /// <summary>
    /// Exact wins outright, including over Burst — GroupReconciler's documented reasoning: a
    /// byte-identical copy inherits the original's EXIF, so copies look like same-instant frames
    /// and would otherwise be withheld from selection as if they were burst frames.
    /// </summary>
    [Fact]
    public void Exact_outranks_burst()
    {
        var exact = Group(1, DupeKind.Exact, 7, 8);
        var burst = Group(9, DupeKind.Burst, 7, 8);

        var map = DupeAssignmentResolver.Resolve([burst, exact]);

        Assert.Equal(DupeKind.Exact, map[7].Kind);
        Assert.True(map[7].Kind.IsBulkSelectable());
    }

    /// <summary>
    /// A group whose keepers were all recycled must still present one. Otherwise every survivor
    /// reads IsKeeper=false and "select all extras" arms a delete against every remaining copy.
    /// </summary>
    [Fact]
    public void A_group_with_no_flagged_keeper_promotes_its_lowest_id_member()
    {
        var g = new DupeGroup
        {
            Id = 3,
            Kind = DupeKind.Variant,
            Members = [new DupeMember { ImageId = 20 }, new DupeMember { ImageId = 11 }],
        };

        var map = DupeAssignmentResolver.Resolve([g]);

        Assert.True(map[11].IsKeeper);
        Assert.False(map[20].IsKeeper);
    }

    [Fact]
    public void Ties_within_a_kind_are_broken_by_group_id()
    {
        var later = Group(9, DupeKind.Variant, 5, 6);
        var earlier = Group(2, DupeKind.Variant, 5, 7);

        var map = DupeAssignmentResolver.Resolve([later, earlier]);

        Assert.Equal(2, map[5].GroupId);
    }

    /// <summary>
    /// The second half of the burst hole, and the one precedence alone cannot close. Complete
    /// linkage legitimately excludes a frame that sits too far from the far end of the run, so no
    /// burst *group* contains it and it is described only as a Variant. Without the burst-adjacent
    /// flag it is bulk-selectable — measured: after fixing precedence, one frame of a five-frame
    /// fixture was still offered for removal.
    /// </summary>
    [Fact]
    public void A_burst_adjacent_photo_is_not_bulk_selectable_even_when_presented_as_variant()
    {
        var variant = Group(2, DupeKind.Variant, 1, 2, 3);

        var withoutFlag = DupeAssignmentResolver.Resolve([variant]);
        Assert.True(withoutFlag[2].IsBulkSelectableExtra, "precondition: a plain variant extra is selectable");

        var withFlag = DupeAssignmentResolver.Resolve([variant], new HashSet<long> { 2 });

        Assert.Equal(DupeKind.Variant, withFlag[2].Kind);   // still described as a variant…
        Assert.True(withFlag[2].BurstAdjacent);
        Assert.False(withFlag[2].IsBulkSelectableExtra);    // …but protected anyway
    }

    [Fact]
    public void A_keeper_is_never_bulk_selectable()
    {
        var g = Group(1, DupeKind.Exact, 4, 5);
        var map = DupeAssignmentResolver.Resolve([g]);

        Assert.True(map[4].IsKeeper);
        Assert.False(map[4].IsBulkSelectableExtra);
        Assert.True(map[5].IsBulkSelectableExtra);
    }

    [Fact]
    public void Precedence_matches_the_reconcilers_ordering()
    {
        Assert.True(DupeAssignmentResolver.Precedence(DupeKind.Exact)
                  < DupeAssignmentResolver.Precedence(DupeKind.Burst));
        Assert.True(DupeAssignmentResolver.Precedence(DupeKind.Burst)
                  < DupeAssignmentResolver.Precedence(DupeKind.Variant));
    }
}
