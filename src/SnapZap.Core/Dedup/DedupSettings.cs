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
    /// Group frames taken seconds apart as a burst, and hold them back from bulk selection.
    /// <b>On by default, and turning it off is a safety decision, not a preference.</b>
    /// </summary>
    /// <remarks>
    /// <para><b>What "off" actually does, which is not what it sounds like.</b> The frames do not
    /// become ungrouped. <see cref="VariantFinder"/> picks them up instead, because pixel distance
    /// cannot tell a burst frame from a re-encode — measured: two different burst frames 9 bits
    /// apart, one photo and its 50% resize 16 bits apart. They land in a Variant group, which
    /// <i>is</i> bulk-selectable, so "Select duplicate extras" will sweep up all but one frame of
    /// every burst. Capture time is the only evidence that separates the two cases, and this is the
    /// switch that decides whether anything consults it.</para>
    ///
    /// <para><b>This setting existed before, defaulted to off, and was removed for being a trap.</b>
    /// It read as "group my bursts?" and was reasoned about as "a burst is a set of different
    /// photographs, so a user should opt in" — which inverted it: opting out did not leave bursts
    /// alone, it made them deletable. The guard was inert in the shipped configuration. It is back
    /// only because the alternative was worse — a user who genuinely wants burst frames treated as
    /// copies had no way to say so and no way to find out why "select extras" kept skipping them —
    /// and it is back with the default flipped and the consequence stated at every surface that
    /// offers it. <b>Do not reword either to something milder.</b></para>
    ///
    /// <para>Exact detection still has no switch, and never will: a duplicate finder that cannot
    /// find identical files is not one.</para>
    ///
    /// <para>Capture time is gathered during the scan regardless of this setting, so turning it back
    /// on needs a re-detect but never a re-scan.</para>
    /// </remarks>
    public bool BurstEnabled { get; init; } = true;

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
    /// <remarks>
    /// Burst is conditional now that <see cref="BurstEnabled"/> exists, and it has to be: this mask
    /// is what tells a later run which folders it still owes work on, so a folder checked with burst
    /// off must come back as pending when it is switched on again. Recording Burst unconditionally
    /// would make re-enabling the protection appear to do nothing — the same failure
    /// <c>nsfw_tile_mean</c>'s nullability exists to avoid.
    /// </remarks>
    public DupeKinds CoveredKinds =>
        DupeKinds.Exact
        | (BurstEnabled ? DupeKinds.Burst : 0)
        | (VariantEnabled ? DupeKinds.Variant : 0);

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
