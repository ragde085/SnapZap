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
        _state = new AppState(_catalog, new MacOsTrashService(), _session, new DependencyChecker(_catalog));
    }

    static ImageView Img(double? score, double? tileMean = null) =>
        new(new ImageRecord
        {
            Id = 1, Path = "/x/a.jpg", ContentHash = "h",
            NsfwScore = score, NsfwTileMean = tileMean,
        }, null);

    [Theory]
    [InlineData(null, AppState.NsfwBand.Unchecked)]
    [InlineData(0.0, AppState.NsfwBand.Clean)]
    [InlineData(0.44, AppState.NsfwBand.Clean)]
    [InlineData(0.45, AppState.NsfwBand.Unsure)]
    [InlineData(0.84, AppState.NsfwBand.Unsure)]
    [InlineData(0.85, AppState.NsfwBand.Likely)]
    [InlineData(1.0, AppState.NsfwBand.Likely)]
    public void Bands_split_at_the_documented_thresholds(double? score, AppState.NsfwBand expected) =>
        Assert.Equal(expected, _state.BandOf(Img(score)));

    /// <summary>
    /// The tiled pass is the whole reason the rule stopped being one comparison: a photo whose
    /// subject occupies a corner scores near zero on the whole frame. Measured case — 0.0014
    /// whole-frame, explicit on the tiles.
    /// </summary>
    [Theory]
    [InlineData(0.0014, 0.62, AppState.NsfwBand.Likely)]  // invisible whole-frame, obvious close up
    [InlineData(0.10, 0.50, AppState.NsfwBand.Likely)]    // exactly on the tile threshold
    [InlineData(0.99, 0.05, AppState.NsfwBand.Likely)]    // whole frame alone is still enough
    public void A_photo_can_be_flagged_on_the_tiles_alone(double whole, double tiles, AppState.NsfwBand expected) =>
        Assert.Equal(expected, _state.BandOf(Img(whole, tiles)));

    /// <summary>
    /// The precision half, and the reason the tiles are averaged rather than maxed.
    ///
    /// An ordinary head-and-shoulders photograph of a person scores ~0.40 whole-frame and
    /// contains one tile that is nearly all skin, which the model scores ~0.99. Under a
    /// max-over-tiles rule this photo is "Likely explicit"; measured on a real family album that
    /// rule flagged 8 of 17 perfectly ordinary photos. The mean is what tells the two apart, and
    /// this test is what stops anyone reintroducing the max.
    /// </summary>
    [Fact]
    public void An_ordinary_photo_of_a_person_is_not_flagged()
    {
        var portrait = Img(0.3965, 0.21);
        Assert.NotEqual(AppState.NsfwBand.Likely, _state.BandOf(portrait));
        Assert.Equal(AppState.NsfwBand.Clean, _state.BandOf(portrait));
    }

    /// <summary>
    /// The number shown has to be the number the decision was made on. "0.40 of 1.00 · flagged at
    /// 0.85" next to a photo the app had just flagged is what sent someone looking for the bug.
    /// </summary>
    [Fact]
    public void The_displayed_score_is_the_one_that_triggered_the_flag()
    {
        Assert.Equal(0.62, Img(0.0014, 0.62).NsfwDisplayScore);
        Assert.Equal(0.99, Img(0.99, 0.05).NsfwDisplayScore);
        Assert.Equal(0.3965, Img(0.3965, 0.21).NsfwDisplayScore);
    }

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
    [InlineData(0.44, false)]
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
