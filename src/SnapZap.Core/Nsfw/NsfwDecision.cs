namespace SnapZap.Core.Nsfw;

/// <summary>How hard SnapZap looks at one photo.</summary>
public enum NsfwDepth
{
    /// <summary>One pass over the whole frame. Fast, and misses anything small in it.</summary>
    WholeFrame,

    /// <summary>The whole frame plus <see cref="NsfwDecision.Tiles"/>. Ten times the work.</summary>
    Tiled,
}

/// <summary>Named starting points for the thresholds. <see cref="Custom"/> is not selectable —
/// it is what the app reports when the numbers match no preset.</summary>
public enum NsfwSensitivity
{
    Cautious,
    Balanced,
    Eager,
    Custom,
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
/// <para>The rule is here rather than at each call site for the same reason
/// <c>DupeKindExtensions.IsBulkSelectable()</c> is: the badge on a thumbnail, the filter that
/// selects photos, and the count in the status line have to be the same question. They were
/// three separate comparisons against a constant, which is survivable only while the rule is a
/// single threshold — and it no longer is.</para>
/// </summary>
public sealed record NsfwSettings
{
    /// <summary>See <see cref="NsfwDepth"/>. Tiled by default: a filter that quietly misses
    /// things is worse than a slow one.</summary>
    public NsfwDepth Depth { get; init; } = NsfwDepth.Tiled;

    /// <summary>Whole-frame score at or above which a photo is explicit on that evidence alone.</summary>
    public double WholeFlag { get; init; } = 0.85;

    /// <summary>
    /// Mean tile score at or above which a photo is explicit even though the whole frame looked
    /// clean.
    ///
    /// <para><b>Mean, not max, and this is the entire point.</b> Taking the maximum tile is what
    /// intuition suggests and it is unusable: a clothed head-and-shoulders portrait contains one
    /// tile that is almost entirely skin, and the model scores that tile ~0.99. Measured on a
    /// real family album, max-over-tiles flagged 8 of 17 ordinary photos as explicit. The mean
    /// asks the question that actually distinguishes the two — is most of this photo explicit,
    /// or just one patch of it.</para>
    ///
    /// <para>Measured against <see cref="SafeTileMeanFloor"/>: at 0.50 that album had no false
    /// positives at all; at 0.40, six of seventeen. The cliff is steep and it is in that gap.</para>
    /// </summary>
    public double TileMeanFlag { get; init; } = 0.50;

    /// <summary>
    /// Below this the photo is left alone; between here and a flag the answer is "look at it
    /// yourself".
    ///
    /// <para>Was 0.20, which put ordinary photographs of people in the review queue — the model
    /// gives a plain portrait ~0.40 on skin tone alone. The band is a queue for a human, so
    /// filling it with faces is how it stops being read.</para>
    /// </summary>
    public double Unsure { get; init; } = 0.45;

    /// <summary>
    /// Lowering <see cref="TileMeanFlag"/> past this is where measured false positives start,
    /// and they arrive quickly. Not a hard limit — the setting is the user's — but the UI warns
    /// on the way past it, because the failure is a family album full of content warnings and it
    /// is not obvious from the number itself.
    /// </summary>
    public const double SafeTileMeanFloor = 0.50;

    public static readonly NsfwSettings Default = Of(NsfwSensitivity.Balanced);

    /// <summary>
    /// The named starting points. Only <see cref="WholeFlag"/> moves between Balanced and Eager:
    /// buying recall by lowering the tile threshold instead costs precision far faster than it
    /// gains anything (measured — see <see cref="TileMeanFlag"/>).
    /// </summary>
    public static NsfwSettings Of(NsfwSensitivity sensitivity) => sensitivity switch
    {
        NsfwSensitivity.Cautious => new() { WholeFlag = 0.95, TileMeanFlag = 0.65, Unsure = 0.60 },
        NsfwSensitivity.Eager => new() { WholeFlag = 0.70, TileMeanFlag = 0.50, Unsure = 0.30 },
        _ => new() { WholeFlag = 0.85, TileMeanFlag = 0.50, Unsure = 0.45 },
    };

    /// <summary>Which preset these thresholds are, or <see cref="NsfwSensitivity.Custom"/>.
    /// Depth is deliberately not part of the comparison: it is a cost decision, not a
    /// sensitivity one, and pairs with any of them.</summary>
    public NsfwSensitivity Sensitivity
    {
        get
        {
            foreach (var s in new[] { NsfwSensitivity.Cautious, NsfwSensitivity.Balanced, NsfwSensitivity.Eager })
            {
                var p = Of(s);
                if (p.WholeFlag == WholeFlag && p.TileMeanFlag == TileMeanFlag && p.Unsure == Unsure)
                    return s;
            }
            return NsfwSensitivity.Custom;
        }
    }

    /// <summary>
    /// True when the evidence says explicit. <paramref name="tileMean"/> is null for a photo
    /// scored whole-frame only, in which case this is the pre-existing single-threshold rule.
    /// </summary>
    public bool IsExplicit(double? whole, double? tileMean) =>
        whole >= WholeFlag || tileMean >= TileMeanFlag;

    /// <summary>True when the photo is worth a human look without being called explicit.</summary>
    public bool IsUnsure(double? whole, double? tileMean) =>
        !IsExplicit(whole, tileMean) && whole >= Unsure;

    /// <summary>
    /// A single 0–1 number to show a person, which has to agree with <see cref="IsExplicit"/> or
    /// the UI ends up saying "0.40 of 1.00 — flagged at 0.85" about a photo it just flagged.
    /// A tile-mean flag is reported as the tile score that beat it, so the number shown is
    /// always the one the decision was made on.
    /// </summary>
    public double? DisplayScore(double? whole, double? tileMean) =>
        tileMean >= TileMeanFlag && tileMean > whole ? tileMean : whole;

    /// <summary>Clamped on the way in: these arrive from a slider and from a JSON file a user can
    /// edit, and a threshold outside 0–1 silently flags everything or nothing.</summary>
    public NsfwSettings Sanitised() => this with
    {
        WholeFlag = Math.Clamp(WholeFlag, 0.05, 1.0),
        TileMeanFlag = Math.Clamp(TileMeanFlag, 0.05, 1.0),
        Unsure = Math.Clamp(Unsure, 0.01, Math.Clamp(WholeFlag, 0.05, 1.0)),
    };
}

/// <summary>The parts of the rule that are not the user's to change.</summary>
public static class NsfwDecision
{
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
}
