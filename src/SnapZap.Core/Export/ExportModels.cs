namespace SnapZap.Core.Export;

/// <summary>What to export and how. Structure/mode/reject are chosen at export time (DESIGN §6).</summary>
public sealed record ExportRequest
{
    public required string Destination { get; init; }
    public required string SourceRoot { get; init; }   // for 'mirror' relative paths
    public TransferMode Mode { get; init; } = TransferMode.Copy;
    public ExportStructure Structure { get; init; } = ExportStructure.Date;
    public RejectAction RejectAction { get; init; } = RejectAction.Leave;

    /// <summary>Keeper image ids to export.</summary>
    public required IReadOnlyList<long> KeeperIds { get; init; }

    /// <summary>Reject image ids to recycle *after* keepers are verified (when RejectAction=Recycle).</summary>
    public IReadOnlyList<long> RejectIds { get; init; } = [];
}

public sealed record Preflight(
    int KeeperCount,
    long TotalBytes,
    /// <summary>Free bytes at the destination, or null when the drive couldn't be queried.
    /// Null is not zero and is not "fine" — it means the check did not happen, and the UI has
    /// to say so rather than render a green reassurance it has no evidence for.</summary>
    long? DestinationFreeBytes,
    bool EnoughSpace,
    bool HardlinkPossible,
    IReadOnlyList<string> SampleDestinations);

public sealed record ExportProgress(int Done, int Total, string? Current, string Phase);

public sealed record ExportItemOutcome(long ImageId, string Status, string? DestPath, string? Detail);

public sealed record ExportResult(
    long RunId,
    int Exported,
    int SkippedExisting,
    int Failed,
    int Recycled,
    string Destination,
    IReadOnlyList<ExportItemOutcome> Items);
