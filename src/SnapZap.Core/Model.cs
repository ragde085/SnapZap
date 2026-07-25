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
}

public enum DupeKind { Exact, Similar }

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
