namespace SnapZap.App.Services;

/// <summary>
/// Rolling-rate time-remaining estimate for one <see cref="AppState.RunAsync"/> run (scan, NSFW
/// scoring, dedup, export — whichever reports through <c>Report(done, total, detail)</c>).
/// </summary>
/// <remarks>
/// A cumulative done/elapsed average skews toward whatever mix of cheap and expensive items came
/// first — a scan's tier-1 cache hits are near-free, so a run that starts on cached files and
/// then hits new ones would report an ETA far too optimistic for the work actually left. Keeping
/// only samples from the last <see cref="_windowMs"/> makes the rate track whatever throughput is
/// happening right now, and it costs nothing extra to feed: every operation already calls
/// <c>Report</c> with a fresh done count, and <see cref="Estimate"/> just needs a timestamp too.
/// </remarks>
public sealed class EtaEstimator(long windowMs = 4000)
{
    readonly Queue<(long ticks, int done)> _samples = new();

    /// <summary>
    /// Feed one progress point and get back the estimated time remaining, or null when there
    /// isn't enough signal yet — the very first sample, a run with an unknown total, one that's
    /// already finished, or one where the window hasn't seen any progress (rate would be zero or
    /// negative, e.g. right after Stop freezes <c>done</c>).
    /// </summary>
    public TimeSpan? Estimate(long nowTicks, int done, int total)
    {
        _samples.Enqueue((nowTicks, done));
        while (_samples.Count > 1 && nowTicks - _samples.Peek().ticks > windowMs)
            _samples.Dequeue();

        if (total <= 0 || done >= total) return null;

        var (oldestTicks, oldestDone) = _samples.Peek();
        var elapsedMs = nowTicks - oldestTicks;
        var doneDelta = done - oldestDone;
        if (elapsedMs <= 0 || doneDelta <= 0) return null;

        var itemsPerSecond = doneDelta / (elapsedMs / 1000.0);
        return TimeSpan.FromSeconds((total - done) / itemsPerSecond);
    }

    /// <summary>Shared by every progress display (Toolbar's busy line, ExportDialog's result
    /// text) so "~2m left" doesn't drift into two different phrasings across the app.</summary>
    public static string Format(TimeSpan t) => t switch
    {
        { TotalSeconds: < 10 } => "a few seconds",
        { TotalMinutes: < 1 } => $"{(int)Math.Ceiling(t.TotalSeconds)}s",
        { TotalHours: < 1 } => $"{(int)Math.Ceiling(t.TotalMinutes)}m",
        _ => $"{(int)t.TotalHours}h {t.Minutes}m",
    };
}
