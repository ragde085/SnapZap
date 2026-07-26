using SnapZap.Core.Data;

namespace SnapZap.Core.Dedup;

public sealed record DuplicateReport(
    int ExactGroups,
    int VariantGroups,
    int BurstGroups,
    int Unhashed,
    bool Truncated,
    string? Note);

/// <summary>
/// Progress for a detection run. <see cref="Detail"/> names the detector currently working.
/// </summary>
/// <remarks>
/// Phase-level rather than a photo count. The exact pass is one SQL statement and the perceptual
/// passes are a single parallel sweep with no meaningful midpoint, so a per-photo number would be
/// invented. A bar that moves per detector with a changing label is the honest version — and still
/// an enormous improvement on the indeterminate spinner that used to sit there for minutes while a
/// deadlocked subprocess said nothing.
/// </remarks>
public sealed record DedupProgress(int Done, int Total, string? Detail);

/// <summary>
/// Runs the enabled duplicate detectors over one folder.
/// </summary>
/// <remarks>
/// <para>Everything is in-process. The <c>czkawka_cli</c> sidecar this replaced was a second full
/// decode of the library in a second process, communicated through a temp JSON file, and it
/// deadlocked on its own undrained stdout pipe. See docs/DEDUP-V2.md for the full rationale.</para>
///
/// <para><b>All detectors share one root and one settings snapshot.</b> The root is the folder the
/// view is scoped to — a duplicate the user cannot see is not one they can act on. The settings
/// are read once at the top so a mid-run change cannot leave half the catalogue detected under one
/// threshold and half under another.</para>
/// </remarks>
public sealed class DuplicateService(Database db)
{
    public async Task<DuplicateReport> DetectAsync(
        string root, IProgress<DedupProgress>? progress = null, CancellationToken ct = default)
    {
        var settings = DedupSettings.Load(db);
        var repo = new ImageRepository(db);

        // One phase per enabled detector, plus the load, plus reconciliation.
        int total = 1 + 1 + 1 + 1 + (settings.VariantEnabled ? 1 : 0);
        int done = 0;
        void Step(string? label) => progress?.Report(new DedupProgress(done, total, label));

        Step("Reading signatures");
        var images = await Task.Run(() => repo.HashedUnder(root), ct);
        int unhashed = images.Count(i => i.Hash.IsEmpty);
        done++;

        Step("Matching content hashes");
        var exact = await Task.Run(() => new ExactDuplicateFinder(db).FindAndStore(root), ct);
        done++;

        var variant = DetectorResult.None;
        if (settings.VariantEnabled)
        {
            Step("Finding the same shot at other sizes");
            variant = await Task.Run(() => new VariantFinder(db).FindAndStore(images, settings, root, ct), ct);
            done++;
        }
        else
        {
            // A disabled detector must clear its own groups, or turning it off leaves its results
            // on screen and the user cannot tell that it is no longer running.
            new DupeRepository(db).ClearKind(DupeKind.Variant, root);
        }

        // Always. Burst membership is what withholds different photographs from a bulk delete, and
        // capture time is the only evidence that identifies them — VariantFinder above cannot,
        // because a burst frame and a re-encode are indistinguishable by pixels. See DedupSettings.
        Step("Grouping bursts");
        var burst = await Task.Run(() => new BurstFinder(db).FindAndStore(images, settings, root, ct), ct);
        done++;

        // The detectors above answer independently over one image set, and their thresholds are
        // nested, so the same photos can be written as two or three groups. Collapse those before
        // anything counts or displays them.
        Step("Reconciling groups");
        var dropped = await Task.Run(() => GroupReconciler.Reconcile(db, root), ct);
        done++;

        // Only here, after every enabled detector has returned. Cancellation throws out of the
        // awaits above and leaves the folder unmarked, which is right — it was not checked.
        // The kind mask is what makes enabling a detector later re-flag folders as pending.
        repo.MarkDupeChecked(root, settings.CoveredKinds);

        progress?.Report(new DedupProgress(total, total, null));

        // Counted back off the catalogue rather than summed from the detectors, because
        // reconciliation has since retired some of what they wrote. Reporting the detector totals
        // is what produced "7 identical, 12 same shot groups" for a library with 12 findings in
        // it — a status line that disagreed with the review flow it was describing.
        var surviving = new DupeRepository(db).Groups()
            .Where(g => g.Members.Count >= 2)
            .GroupBy(g => g.Kind)
            .ToDictionary(g => g.Key, g => g.Count());

        int Surviving(DupeKind k) => surviving.GetValueOrDefault(k);

        return new DuplicateReport(
            Surviving(DupeKind.Exact), Surviving(DupeKind.Variant), Surviving(DupeKind.Burst),
            unhashed,
            variant.Truncated || burst.Truncated,
            Note(settings, unhashed, images.Count, variant.Truncated || burst.Truncated));
    }

    /// <remarks>
    /// Silence about a partial result is the failure mode worth engineering against here: a run
    /// that skipped half the library, or stopped early at the pair cap, otherwise reads exactly
    /// like a clean one that found nothing.
    /// </remarks>
    static string? Note(DedupSettings settings, int unhashed, int total, bool truncated)
    {
        var parts = new List<string>();

        if (unhashed > 0 && total > 0)
            parts.Add($"{unhashed:N0} of {total:N0} photos have no visual signature yet — re-scan to include them");

        if (truncated)
            parts.Add("too many candidate matches to compare them all; tighten the threshold for a complete answer");

        if (!settings.VariantEnabled)
            parts.Add("same-shot matching is turned off in Setup — only identical files and bursts were checked");

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
}
