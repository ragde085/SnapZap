using SnapZap.Core.Data;

namespace SnapZap.Core.Dedup;

public sealed record DuplicateReport(
    int ExactGroups,
    int SimilarGroups,
    bool CzkawkaAvailable,
    string? Note);

/// <summary>
/// Coarse progress for a detection run. <see cref="Detail"/> is whatever the current phase can
/// say about itself — for the similar pass that is czkawka's own output, relayed line by line.
/// </summary>
/// <remarks>
/// Two phases, not a photo count. Neither detector can report per-photo progress honestly: the
/// exact pass is one SQL statement, and the similar pass is an opaque subprocess. A bar that moves
/// twice with a line of text that keeps changing is the truthful version, and it is still a large
/// improvement on the indeterminate spinner that sat saying nothing for the whole run.
/// </remarks>
public sealed record DedupProgress(int Done, int Total, string? Detail);

/// <summary>
/// Runs both detectors: exact (always, from content hashes) and similar (via Czkawka if
/// present). Exact-dedup value is delivered even with no external tool installed.
/// </summary>
public sealed class DuplicateService(Database db, string? czkawkaPath = null)
{
    public const int Phases = 2;

    public async Task<DuplicateReport> DetectAsync(
        string root, IProgress<DedupProgress>? progress = null, CancellationToken ct = default)
    {
        // Both detectors get the same root the view is scoped to: a duplicate the user cannot
        // see is not one they can act on.
        progress?.Report(new DedupProgress(0, Phases, "Matching content hashes"));
        var exact = new ExactDuplicateFinder(db).FindAndStore(root);

        progress?.Report(new DedupProgress(1, Phases, "Comparing similar images"));
        var relay = progress is null
            ? null
            : new Progress<string>(line => progress.Report(new DedupProgress(1, Phases, line)));
        var czk = await new CzkawkaFinder(db, czkawkaPath).FindSimilarAsync(root, relay, ct);

        // Only here, after both passes have returned. Cancellation throws out of the await above
        // and leaves the folder unmarked, which is right — it was not checked.
        //
        // A missing czkawka still counts as checked. The run did everything this installation is
        // capable of, DependencyChecker already reports the similar pass as unavailable app-wide,
        // and withholding the mark would strand a permanent pending dot on every folder with no
        // action the user could take to clear it.
        new ImageRepository(db).MarkDupeChecked(root);

        progress?.Report(new DedupProgress(Phases, Phases, null));
        return new DuplicateReport(exact, czk.GroupsFound, czk.Available, czk.Message);
    }
}
