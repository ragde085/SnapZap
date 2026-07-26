namespace SnapZap.Core;

/// <summary>A single analyzed image and its four signals. Mirrors the `images` table.</summary>
public sealed record ImageRecord
{
    public long Id { get; init; }
    public required string Path { get; init; }
    public required string ContentHash { get; init; }
    public long FileSize { get; init; }
    public long Mtime { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public string? Format { get; init; }

    // The four faceted signals.
    public double? NsfwScore { get; init; }
    public double? BlurScore { get; init; }
    public long? ExifTaken { get; init; }      // unix utc, null when absent
    public string? ExifCamera { get; init; }

    public string? ThumbPath { get; init; }
    public long? AnalyzedAt { get; init; }

    /// <summary>
    /// Unix UTC of the last duplicate-detection run that covered this row; null when it has never
    /// been checked, or when the file changed since it was.
    /// </summary>
    /// <remarks>
    /// Membership in a group is not the same fact. A photo with no duplicates and a photo nobody
    /// has run detection over both have zero groups, and without this the folder tree cannot tell
    /// the user which of the two it is looking at.
    /// </remarks>
    public long? DupeCheckedAt { get; init; }

    /// <summary>
    /// Which detectors the run recorded in <see cref="DupeCheckedAt"/> actually covered.
    /// </summary>
    /// <remarks>
    /// The timestamp alone lies once detectors are configurable: turn burst detection on tomorrow
    /// and every folder still claims to have been checked. Comparing this mask against the
    /// currently enabled set is what makes enabling a detector correctly re-flag the affected
    /// folders as pending.
    /// </remarks>
    public DupeKinds DupeCheckedKinds { get; init; }

    /// <summary>
    /// The 272-bit perceptual signature across all four rotations, or <c>default</c> when this row
    /// has never been hashed. See <c>PerceptualHash</c>.
    /// </summary>
    public byte[]? Phash { get; init; }
}

/// <summary>
/// What kind of duplicate a group represents. The split is safety-critical, not cosmetic.
/// </summary>
/// <remarks>
/// Everything that was not byte-identical used to land in one <c>Similar</c> bucket, which
/// <c>AppState.SelectDupeExtras</c> will sweep wholesale. That is correct for copies of one photo
/// and catastrophic for a burst, where the five "duplicates" are five different photographs and
/// only one of them is the good one. Detection could not widen until the kinds separated.
/// </remarks>
public enum DupeKind
{
    /// <summary>Byte-identical, by SHA-256. Safe to auto-select the extras.</summary>
    Exact,

    /// <summary>The same shot: resized, re-encoded, reformatted or rotated. Safe to auto-select.</summary>
    Variant,

    /// <summary>The same scene seconds apart. Review only — never auto-selected.</summary>
    Burst,
}

/// <summary>Bitmask form of <see cref="DupeKind"/>, for coverage tracking on a row.</summary>
[Flags]
public enum DupeKinds
{
    None = 0,
    Exact = 1 << 0,
    Variant = 1 << 1,
    Burst = 1 << 2,
}

public static class DupeKindExtensions
{
    public static DupeKinds AsFlag(this DupeKind kind) => kind switch
    {
        DupeKind.Exact => DupeKinds.Exact,
        DupeKind.Variant => DupeKinds.Variant,
        _ => DupeKinds.Burst,
    };

    /// <summary>
    /// Kinds whose non-keepers may be selected in bulk. The one rule standing between a widened
    /// detector and a user losing photographs — read it from here, never re-derive it inline.
    /// </summary>
    public static bool IsBulkSelectable(this DupeKind kind) =>
        kind is DupeKind.Exact or DupeKind.Variant;

    public static string Label(this DupeKind kind) => kind switch
    {
        DupeKind.Exact => "Identical",
        DupeKind.Variant => "Same shot",
        _ => "Burst",
    };
}

public sealed record DupeGroup
{
    public long Id { get; init; }
    public DupeKind Kind { get; init; }
    public string? Similarity { get; init; }
    public IReadOnlyList<DupeMember> Members { get; init; } = [];
}

public sealed record DupeMember
{
    public long ImageId { get; init; }
    public bool IsKeeper { get; init; }
}

public enum TransferMode { Copy, Move, Hardlink }
public enum ExportStructure { Date, Mirror, Flat }
public enum RejectAction { Leave, Recycle }
