using SnapZap.App;
using SnapZap.App.Services;
using SnapZap.Core;
using SnapZap.Core.Platform;
using Xunit;

namespace SnapZap.Tests;

/// <summary>
/// The band is what the badge, the preview and the filter all read, so it is the one place that
/// decides whether a photo is called explicit. Its failure mode is not a wrong pixel: it is a
/// content warning on a photo of a wine barrel, or silence on one that needed the warning.
/// </summary>
public class NsfwBandTests : IDisposable
{
    readonly string _work = Path.Combine(Path.GetTempPath(), "pc_band_" + Guid.NewGuid().ToString("N"));
    readonly CatalogService _catalog;
    readonly SessionStore _session;
    readonly AppState _state;

    public NsfwBandTests()
    {
        Directory.CreateDirectory(_work);
        _catalog = new CatalogService(_work);
        _session = new SessionStore(_catalog, TimeSpan.FromMilliseconds(50));
        _state = new AppState(_catalog, new MacOsTrashService(), _session);
    }

    static ImageView Img(double? score) =>
        new(new ImageRecord { Id = 1, Path = "/x/a.jpg", ContentHash = "h", NsfwScore = score }, null);

    [Theory]
    [InlineData(null, AppState.NsfwBand.Unchecked)]
    [InlineData(0.0, AppState.NsfwBand.Clean)]
    [InlineData(0.19, AppState.NsfwBand.Clean)]
    [InlineData(0.20, AppState.NsfwBand.Unsure)]
    [InlineData(0.84, AppState.NsfwBand.Unsure)]
    [InlineData(0.85, AppState.NsfwBand.Likely)]
    [InlineData(1.0, AppState.NsfwBand.Likely)]
    public void Bands_split_at_the_documented_thresholds(double? score, AppState.NsfwBand expected) =>
        Assert.Equal(expected, _state.BandOf(Img(score)));

    /// <summary>
    /// An unscored photo is not a safe one. Treating null as clean is how a library nobody had
    /// scored would have reported itself entirely free of explicit content.
    /// </summary>
    [Fact]
    public void Unscored_is_never_clean()
    {
        Assert.Equal(AppState.NsfwBand.Unchecked, _state.BandOf(Img(null)));
        Assert.NotEqual(AppState.NsfwBand.Clean, _state.BandOf(Img(null)));
    }

    /// <summary>The badge only ever appears for what the app is actually flagging.</summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData(0.001, false)]
    [InlineData(0.5, true)]
    [InlineData(0.99, true)]
    public void Only_flagged_photos_are_badged(double? score, bool badged)
    {
        var band = _state.BandOf(Img(score));
        Assert.Equal(badged, band is AppState.NsfwBand.Likely or AppState.NsfwBand.Unsure);
    }

    [Fact]
    public void The_threshold_the_preview_prints_is_the_one_the_band_uses()
    {
        // The preview renders "flagged at {NsfwThreshold}". If that were a separate literal it
        // could say "below threshold" about a photo the filter had just called explicit — the
        // exact contradiction the blur threshold was refactored to remove.
        Assert.Equal(AppState.NsfwBand.Likely, _state.BandOf(Img(_state.NsfwThreshold)));
        Assert.Equal(AppState.NsfwBand.Unsure, _state.BandOf(Img(_state.NsfwThreshold - 0.01)));
    }

    [Theory]
    [InlineData("Likely explicit", AppState.NsfwBand.Likely)]
    [InlineData("Not sure", AppState.NsfwBand.Unsure)]
    [InlineData("Looks clean", AppState.NsfwBand.Clean)]
    [InlineData("Not checked", AppState.NsfwBand.Unchecked)]
    public void Every_band_reads_as_plain_language(string expected, AppState.NsfwBand band) =>
        Assert.Equal(expected, AppState.BandLabel(band));

    public void Dispose()
    {
        _session.Dispose();
        _catalog.Dispose();
        try { Directory.Delete(_work, true); } catch { /* temp dir */ }
    }
}
