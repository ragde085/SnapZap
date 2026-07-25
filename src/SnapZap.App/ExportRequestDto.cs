using SnapZap.Core;
using SnapZap.Core.Export;

namespace SnapZap.App;

/// <summary>Selected image ids to recycle in-place.</summary>
public sealed record DeleteDto
{
    public long[] ImageIds { get; init; } = [];
}

/// <summary>Wire shape for export requests from the SPA. Enums arrive as strings.</summary>
public sealed record ExportRequestDto
{
    public string Destination { get; init; } = "";
    public string Mode { get; init; } = "copy";        // copy | move | hardlink
    public string Structure { get; init; } = "date";   // date | mirror | flat
    public string RejectAction { get; init; } = "leave"; // leave | recycle
    public long[] KeeperIds { get; init; } = [];
    public long[] RejectIds { get; init; } = [];

    public ExportRequest ToRequest(string sourceRoot) => new()
    {
        Destination = Destination,
        SourceRoot = sourceRoot,
        Mode = Enum.Parse<TransferMode>(Mode, ignoreCase: true),
        Structure = Enum.Parse<ExportStructure>(Structure, ignoreCase: true),
        RejectAction = Enum.Parse<RejectAction>(RejectAction, ignoreCase: true),
        KeeperIds = KeeperIds,
        RejectIds = RejectIds,
    };
}
