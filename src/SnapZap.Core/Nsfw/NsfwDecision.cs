namespace SnapZap.Core.Nsfw;

/// <summary>How hard SnapZap looks at one photo.</summary>
public enum NsfwDepth
{
    /// <summary>One pass over the whole frame. Fast, and misses anything small in it.</summary>
    WholeFrame,

    /// <summary>The whole frame plus <see cref="NsfwDecision.Tiles"/>. Ten times the work.</summary>
    Tiled,
}

/// <summary>
/// One image's scores: the whole frame, and — when it was scored <see cref="NsfwDepth.Tiled"/> —
/// the mean across the tiles. <c>TileMean</c> is null exactly when the photo has only ever been
/// scored whole, which is also how the scorer knows what a deeper re-run still has to do.
/// </summary>
public sealed record NsfwVerdict(double Whole, double? TileMean);

/// <summary>
/// Where "is this photo explicit" is decided, for the whole app.
///
/// The rule is here rather than at each call site for the same reason
/// <c>DupeKindExtensions.IsBulkSelectable()</c> is: the badge on a thumbnail, the filter that
/// selects photos, and the count in the status line have to be the same question. They were
/// three separate comparisons against a constant, which is survivable only while the rule is a
/// single threshold — and it no longer is.
/// </summary>
public static class NsfwDecision
{
    /// <summary>
    /// Whole-frame score at or above which a photo is explicit on that evidence alone.
    /// </summary>
    public const double WholeFlagThreshold = 0.85;

    /// <summary>
    /// Mean tile score at or above which a photo is explicit even though the whole frame looked
    /// clean.
    ///
    /// <para><b>Mean, not max, and this is the entire point.</b> Taking the maximum tile is what
    /// intuition suggests and it is unusable: a clothed head-and-shoulders portrait contains one
    /// tile that is almost entirely skin, and the model scores that tile ~0.99. Measured on a
    /// real family album, max-over-tiles flagged 8 of 17 ordinary photos as explicit. The mean
    /// asks the question that actually distinguishes the two cases — is most of this photo
    /// explicit, or just one patch of it — and on the same album flagged none of them, while
    /// still finding 25 of 41 in a folder the whole-frame pass could only find 17 of.</para>
    /// </summary>
    public const double TileMeanFlagThreshold = 0.50;

    /// <summary>
    /// Below this the photo is left alone; between here and a flag the answer is "look at it
    /// yourself".
    ///
    /// <para>Was 0.20, which put ordinary photographs of people in the review queue — the model
    /// gives a plain portrait ~0.40 on skin tone alone. The band is a queue for a human, so
    /// filling it with faces is how it stops being read.</para>
    /// </summary>
    public const double UnsureThreshold = 0.45;

    /// <summary>
    /// The 3×3 grid, as fractions of the frame: nine 40% tiles overlapping by a quarter of their
    /// own width.
    ///
    /// <para>The overlap is what stops a subject that straddles a tile boundary from being
    /// quartered into four unrecognisable pieces. Nine is where the measurements stopped
    /// improving: four corner tiles at 60% found most of what this finds, but consistently lost
    /// the cases where the subject sits centre-frame in a wide shot.</para>
    /// </summary>
    public static readonly IReadOnlyList<(double X, double Y, double W, double H)> Tiles =
    [
        (0.0, 0.0, 0.4, 0.4), (0.3, 0.0, 0.4, 0.4), (0.6, 0.0, 0.4, 0.4),
        (0.0, 0.3, 0.4, 0.4), (0.3, 0.3, 0.4, 0.4), (0.6, 0.3, 0.4, 0.4),
        (0.0, 0.6, 0.4, 0.4), (0.3, 0.6, 0.4, 0.4), (0.6, 0.6, 0.4, 0.4),
    ];

    /// <summary>
    /// True when the evidence says explicit. <paramref name="tileMean"/> is null for a photo
    /// scored whole-frame only, in which case this is the pre-existing single-threshold rule.
    /// </summary>
    public static bool IsExplicit(double? whole, double? tileMean) =>
        whole >= WholeFlagThreshold || tileMean >= TileMeanFlagThreshold;

    /// <summary>
    /// True when the photo is worth a human look without being called explicit outright.
    /// </summary>
    public static bool IsUnsure(double? whole, double? tileMean) =>
        !IsExplicit(whole, tileMean) && whole >= UnsureThreshold;

    /// <summary>
    /// A single 0–1 number to show a person, which has to agree with <see cref="IsExplicit"/> or
    /// the UI ends up saying "0.40 of 1.00 — flagged at 0.85" about a photo it just flagged.
    /// A tile-mean flag is reported as the whole-frame score it beat, so the number shown is
    /// always the one the decision was made on.
    /// </summary>
    public static double? DisplayScore(double? whole, double? tileMean) =>
        tileMean >= TileMeanFlagThreshold && tileMean > whole ? tileMean : whole;
}
