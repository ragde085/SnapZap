using SnapZap.Core.Dedup;
using Xunit;

namespace SnapZap.Tests;

/// <summary>
/// The grouper is the one place in duplicate detection where a bug destroys photographs rather
/// than merely annoying someone, so it is tested against a distance matrix directly — no images,
/// no database, no thresholds to argue about.
/// </summary>
public class GrouperTests
{
    /// <summary>Build a <c>within</c> predicate and a pair list from an explicit distance table.</summary>
    static (List<SimilarPair> pairs, Func<long, long, bool> within) Matrix(
        int threshold, params (long a, long b, int d)[] distances)
    {
        var table = new Dictionary<(long, long), int>();
        foreach (var (a, b, d) in distances)
        {
            table[(a, b)] = d;
            table[(b, a)] = d;
        }

        // Anything not listed is unrelated — the honest default for a sparse matrix.
        int Distance(long x, long y) => x == y ? 0 : table.GetValueOrDefault((x, y), int.MaxValue);

        var pairs = distances.Where(t => t.d <= threshold)
                             .Select(t => new SimilarPair(t.a, t.b, t.d))
                             .ToList();
        return (pairs, (x, y) => Distance(x, y) <= threshold);
    }

    /// <summary>
    /// The test that matters. A~B and B~C are both inside the threshold; A~C is not. Union-find
    /// merges all three on either single edge, and on a real library that is how one group ends up
    /// holding several thousand photos with all but one flagged for deletion.
    /// </summary>
    [Fact]
    public void A_chain_of_near_matches_does_not_collapse_into_one_group()
    {
        var (pairs, within) = Matrix(10, (1, 2, 8), (2, 3, 8), (1, 3, 16));

        var result = SimilarityGrouper.Group(pairs, within);

        Assert.Single(result.Groups);
        // The closest pair claims each other first; 3 is not within threshold of 1, so it is
        // refused rather than dragged in on the strength of its edge to 2.
        Assert.Equal([1, 2], result.Groups[0]);
    }

    [Fact]
    public void A_true_clique_groups_together()
    {
        var (pairs, within) = Matrix(10, (1, 2, 3), (2, 3, 4), (1, 3, 5));

        var result = SimilarityGrouper.Group(pairs, within);

        Assert.Single(result.Groups);
        Assert.Equal([1, 2, 3], result.Groups[0]);
    }

    /// <summary>
    /// Two tight pairs that are nowhere near each other must stay two groups, not merge because
    /// one straddling edge happened to sneak under the threshold.
    /// </summary>
    [Fact]
    public void Separate_cliques_stay_separate()
    {
        var (pairs, within) = Matrix(10,
            (1, 2, 1),
            (3, 4, 1),
            (2, 3, 9));   // an edge between the two pairs, but 1-3, 1-4 and 2-4 are unrelated

        var result = SimilarityGrouper.Group(pairs, within);

        Assert.Equal(2, result.Groups.Count);
        Assert.Contains(result.Groups, g => g.SequenceEqual([1L, 2L]));
        Assert.Contains(result.Groups, g => g.SequenceEqual([3L, 4L]));
    }

    /// <summary>
    /// The same catalogue must produce the same groups every run. Without the id tiebreak the
    /// output depends on enumeration order, and the keeper a user spent an evening choosing lands
    /// on a different photo next week for no reason they can see.
    /// </summary>
    [Fact]
    public void Output_does_not_depend_on_input_order()
    {
        var (pairs, within) = Matrix(10,
            (1, 2, 5), (3, 4, 5), (5, 6, 5), (2, 3, 5), (4, 5, 5));

        var forward = SimilarityGrouper.Group(pairs, within);
        var reversed = SimilarityGrouper.Group(Enumerable.Reverse(pairs), within);

        Assert.Equal(
            forward.Groups.Select(g => string.Join(",", g)).OrderBy(s => s, StringComparer.Ordinal),
            reversed.Groups.Select(g => string.Join(",", g)).OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public void Hitting_the_pair_cap_is_reported_not_swallowed()
    {
        var (pairs, within) = Matrix(10, (1, 2, 1), (3, 4, 1), (5, 6, 1));

        var result = SimilarityGrouper.Group(pairs, within, pairCap: 1);

        Assert.True(result.Truncated);
        Assert.Single(result.Groups);
    }

    [Fact]
    public void No_pairs_means_no_groups()
    {
        var result = SimilarityGrouper.Group([], (_, _) => true);

        Assert.Empty(result.Groups);
        Assert.False(result.Truncated);
    }
}
