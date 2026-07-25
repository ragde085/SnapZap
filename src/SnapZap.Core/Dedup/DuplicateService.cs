using SnapZap.Core.Data;

namespace SnapZap.Core.Dedup;

public sealed record DuplicateReport(
    int ExactGroups,
    int SimilarGroups,
    bool CzkawkaAvailable,
    string? Note);

/// <summary>
/// Runs both detectors: exact (always, from content hashes) and similar (via Czkawka if
/// present). Exact-dedup value is delivered even with no external tool installed.
/// </summary>
public sealed class DuplicateService(Database db, string? czkawkaPath = null)
{
    public async Task<DuplicateReport> DetectAsync(string root, CancellationToken ct = default)
    {
        var exact = new ExactDuplicateFinder(db).FindAndStore();
        var czk = await new CzkawkaFinder(db, czkawkaPath).FindSimilarAsync(root, ct);
        return new DuplicateReport(exact, czk.GroupsFound, czk.Available, czk.Message);
    }
}
