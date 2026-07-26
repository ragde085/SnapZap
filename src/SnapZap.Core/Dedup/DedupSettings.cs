using SnapZap.Core.Data;

namespace SnapZap.Core.Dedup;

/// <summary>
/// Which duplicate detectors run, and how strict each one is.
/// </summary>
/// <remarks>
/// Persisted in the catalogue's <c>meta</c> table rather than in app-data's
/// <c>settings.json</c>. That file exists and is the right home for app-level preferences, but
/// these are not app-level: <c>images.dupe_checked_kinds</c> records which detectors covered each
/// row, and that record only means anything against the settings it was produced under. Both have
/// to reset together when <c>catalog.db</c> is deleted — the same argument the <c>meta</c> schema
/// comment already makes for <c>scan_root</c>.
///
/// Exact detection has no toggle. It is free, it is exact, and a duplicate finder that cannot
/// find identical files is not one.
/// </remarks>
public sealed record DedupSettings
{
    // ---- Variant: same framing, different resolution / encoding / format / orientation ----

    public bool VariantEnabled { get; init; } = true;

    /// <summary>
    /// Maximum Hamming distance, in bits out of <see cref="PerceptualHash.Bits"/>, for two photos
    /// to count as the same shot.
    /// </summary>
    /// <remarks>
    /// Not comparable to czkawka's <c>--max-difference</c>, which was 10 bits out of 256 on a
    /// different grid. Any intuition calibrated on that number has to be re-derived here. Starts
    /// strict on purpose: this feeds a tool that deletes things, so a miss costs less than a false
    /// positive.
    /// </remarks>
    public int VariantMaxBits { get; init; } = 20;

    /// <summary>Also match a photo against 90/180/270-degree rotations of another.</summary>
    public bool VariantRotations { get; init; } = true;

    // ---- Burst: the same scene, seconds apart ----

    /// <summary>
    /// Off by default, and deliberately so. A burst is a set of *different photographs*, not
    /// copies of one — which is why <see cref="DupeKind.Burst"/> is excluded from every bulk
    /// selection. A user who wants them grouped should say so.
    /// </summary>
    public bool BurstEnabled { get; init; }

    /// <summary>Seconds between EXIF capture times for two frames to be candidates.</summary>
    public int BurstWindowSeconds { get; init; } = 3;

    /// <summary>
    /// Hash distance gate for burst candidates — loose, because consecutive frames genuinely
    /// differ. It exists to stop a camera left running across two different scenes from merging
    /// them, not to establish that the frames are the same photo.
    /// </summary>
    public int BurstMaxBits { get; init; } = 60;

    /// <summary>
    /// Kinds a run with these settings actually covers, as stored in
    /// <c>images.dupe_checked_kinds</c>. Exact is always present.
    /// </summary>
    public DupeKinds CoveredKinds =>
        DupeKinds.Exact
        | (VariantEnabled ? DupeKinds.Variant : 0)
        | (BurstEnabled ? DupeKinds.Burst : 0);

    // ---- Persistence -------------------------------------------------------

    const string Prefix = "dedup.";

    public static DedupSettings Load(Database db)
    {
        var fallback = new DedupSettings();
        return new DedupSettings
        {
            VariantEnabled = Bool(db, "variant.enabled", fallback.VariantEnabled),
            VariantMaxBits = Int(db, "variant.maxbits", fallback.VariantMaxBits, 0, PerceptualHash.Bits),
            VariantRotations = Bool(db, "variant.rotations", fallback.VariantRotations),
            BurstEnabled = Bool(db, "burst.enabled", fallback.BurstEnabled),
            BurstWindowSeconds = Int(db, "burst.windowsec", fallback.BurstWindowSeconds, 0, 3600),
            BurstMaxBits = Int(db, "burst.maxbits", fallback.BurstMaxBits, 0, PerceptualHash.Bits),
        };
    }

    public void Save(Database db)
    {
        db.SetMeta(Prefix + "variant.enabled", VariantEnabled ? "1" : "0");
        db.SetMeta(Prefix + "variant.maxbits", VariantMaxBits.ToString());
        db.SetMeta(Prefix + "variant.rotations", VariantRotations ? "1" : "0");
        db.SetMeta(Prefix + "burst.enabled", BurstEnabled ? "1" : "0");
        db.SetMeta(Prefix + "burst.windowsec", BurstWindowSeconds.ToString());
        db.SetMeta(Prefix + "burst.maxbits", BurstMaxBits.ToString());
    }

    // A malformed or out-of-range stored value falls back to the default rather than throwing.
    // These are read on the path that opens a catalogue; a hand-edited or downgraded value must
    // not be able to stop the app from starting.
    static bool Bool(Database db, string key, bool fallback) =>
        db.Meta(Prefix + key) switch { "1" => true, "0" => false, _ => fallback };

    static int Int(Database db, string key, int fallback, int min, int max) =>
        int.TryParse(db.Meta(Prefix + key), out var v) && v >= min && v <= max ? v : fallback;
}
