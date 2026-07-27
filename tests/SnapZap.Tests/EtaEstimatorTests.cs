using SnapZap.App.Services;
using Xunit;

namespace SnapZap.Tests;

/// <summary>
/// EtaEstimator drives the "~Xs left" text on the progress bar (Toolbar.razor's CountLabel).
/// Ticks are fed explicitly rather than read from the wall clock, so these run instantly and
/// deterministically instead of needing real sleeps.
/// </summary>
public class EtaEstimatorTests
{
    [Fact]
    public void First_sample_has_no_estimate()
    {
        var eta = new EtaEstimator();
        Assert.Null(eta.Estimate(nowTicks: 0, done: 10, total: 100));
    }

    [Fact]
    public void Unknown_total_never_estimates()
    {
        var eta = new EtaEstimator();
        eta.Estimate(0, 10, total: 0);
        Assert.Null(eta.Estimate(1000, 20, total: 0));
    }

    [Fact]
    public void Already_finished_has_no_estimate()
    {
        var eta = new EtaEstimator();
        eta.Estimate(0, 0, total: 100);
        Assert.Null(eta.Estimate(1000, 100, total: 100));
    }

    [Fact]
    public void Steady_rate_projects_remaining_work_linearly()
    {
        var eta = new EtaEstimator();
        eta.Estimate(nowTicks: 0, done: 0, total: 100);
        // 10 items in 1000ms => 10 items/sec; 90 left => 9s.
        var estimate = eta.Estimate(nowTicks: 1000, done: 10, total: 100);

        Assert.NotNull(estimate);
        Assert.Equal(9.0, estimate!.Value.TotalSeconds, precision: 3);
    }

    [Fact]
    public void Rate_change_is_reflected_after_the_window_rolls_past_the_old_samples()
    {
        const long WindowMs = 4000;
        var eta = new EtaEstimator(WindowMs);

        // Fast stretch: cache hits, ~100 items/sec.
        eta.Estimate(0, 0, total: 1000);
        eta.Estimate(1000, 100, total: 1000);

        // Slow stretch starts: real analysis, ~5 items/sec. Once enough time has passed that
        // the fast samples have aged out of the window, the estimate should reflect only the
        // slow rate, not a blend that still remembers the fast start.
        var midRate = eta.Estimate(2000, 105, total: 1000);   // window still spans the fast sample
        var laterTicks = 1000 + WindowMs + 1000;              // now well past the fast sample's age
        var lateDone = 105 + 5 * (int)((laterTicks - 2000) / 1000);
        var lateRate = eta.Estimate(laterTicks, lateDone, total: 1000);

        Assert.NotNull(midRate);
        Assert.NotNull(lateRate);
        // The blended (still-fast-influenced) estimate remains far more optimistic than the
        // purely-slow one once the window has rolled past the fast samples.
        Assert.True(midRate!.Value.TotalSeconds < lateRate!.Value.TotalSeconds,
            $"expected mid ({midRate.Value.TotalSeconds}s) < late ({lateRate.Value.TotalSeconds}s)");
    }

    [Fact]
    public void No_progress_since_last_sample_gives_no_estimate()
    {
        var eta = new EtaEstimator();
        eta.Estimate(0, 50, total: 100);
        // Time passes (e.g. Stop was pressed and done is frozen) but done doesn't move.
        Assert.Null(eta.Estimate(1000, 50, total: 100));
    }

    [Theory]
    [InlineData(9, "a few seconds")]
    [InlineData(45, "45s")]
    [InlineData(90, "2m")]        // 1m30s rounds up, not down — "1m left" on 90s reads as almost done
    [InlineData(3600, "1h 0m")]
    [InlineData(4000, "1h 6m")]
    public void Format_matches_the_boundary_used_by_every_progress_display(int seconds, string expected)
    {
        // Toolbar's busy line and ExportDialog's result text both call this — a mismatch here
        // would silently reintroduce the two-copies-of-the-same-switch drift this method exists
        // to prevent.
        Assert.Equal(expected, EtaEstimator.Format(TimeSpan.FromSeconds(seconds)));
    }
}
