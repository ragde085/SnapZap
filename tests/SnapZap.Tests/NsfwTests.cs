using SnapZap.Core;
using SnapZap.Core.Data;
using SnapZap.Core.Nsfw;
using SkiaSharp;
using Xunit;

namespace SnapZap.Tests;

public class NsfwPreprocessTests
{
    static SKBitmap Solid(int size, SKColor color)
    {
        var bmp = new SKBitmap(size, size);
        using var c = new SKCanvas(bmp);
        c.Clear(color);
        return bmp;
    }

    [Fact]
    public void Tensor_shape_is_NCHW_batch1_rgb()
    {
        using var bmp = Solid(64, SKColors.Gray);
        var cfg = new PreprocessConfig { Size = 224 };
        var t = NsfwPreprocess.ToTensor(bmp, cfg);
        Assert.Equal([1, 3, 224, 224], t.Dimensions.ToArray());
    }

    [Fact]
    public void White_and_black_normalize_to_plus_and_minus_one_with_half_meanstd()
    {
        // Falconsai defaults: rescale 1/255, mean .5, std .5 → white→+1, black→-1.
        var cfg = new PreprocessConfig(); // 0.5/0.5, 224
        using var white = Solid(16, SKColors.White);
        using var black = Solid(16, SKColors.Black);
        var tw = NsfwPreprocess.ToTensor(white, cfg);
        var tb = NsfwPreprocess.ToTensor(black, cfg);

        Assert.Equal(1.0f, tw[0, 0, 0, 0], 3);
        Assert.Equal(1.0f, tw[0, 1, 100, 100], 3);
        Assert.Equal(1.0f, tw[0, 2, 223, 223], 3);
        Assert.Equal(-1.0f, tb[0, 0, 0, 0], 3);
        Assert.Equal(-1.0f, tb[0, 2, 50, 50], 3);
    }

    [Fact]
    public void Channels_are_separated_correctly_R_G_B()
    {
        // Pure red: R channel high (+1), G and B low (-1). Proves no channel swap (e.g. BGR).
        var cfg = new PreprocessConfig();
        using var red = Solid(16, new SKColor(255, 0, 0));
        var t = NsfwPreprocess.ToTensor(red, cfg);
        Assert.Equal(1.0f, t[0, 0, 5, 5], 3);   // R
        Assert.Equal(-1.0f, t[0, 1, 5, 5], 3);  // G
        Assert.Equal(-1.0f, t[0, 2, 5, 5], 3);  // B
    }

    [Fact]
    public void Softmax_sums_to_one_and_orders_by_logit()
    {
        var p = NsfwPreprocess.Softmax([2.0f, 0.0f]);
        Assert.Equal(1.0f, p[0] + p[1], 5);
        Assert.True(p[0] > p[1]);
        // Reference value: softmax([2,0])[0] = e^2/(e^2+1) ≈ 0.8808.
        Assert.Equal(0.8808f, p[0], 3);
    }

    [Fact]
    public void Config_parses_real_constants_from_json_and_overrides_defaults()
    {
        var json = """
        { "image_mean":[0.48,0.46,0.41], "image_std":[0.27,0.26,0.28],
          "size":{"height":256,"width":256}, "rescale_factor":0.00392156862745098 }
        """;
        var path = Path.Combine(Path.GetTempPath(), $"ppc_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        try
        {
            var cfg = PreprocessConfig.FromJsonFile(path);
            Assert.Equal(256, cfg.Size);
            Assert.Equal(0.48f, cfg.Mean[0], 4);
            Assert.Equal(0.28f, cfg.Std[2], 4);
        }
        finally { File.Delete(path); }
    }
}

/// <summary>
/// Exercises the full ONNX inference path (session load → Run → softmax → label index)
/// against a tiny synthetic model that mimics Falconsai's I/O contract
/// ([1,3,224,224] → [1,2] logits, idx 1 = nsfw). The synthetic model is rigged so RED scores
/// nsfw-high and BLUE scores nsfw-low, which lets us assert the plumbing deterministically
/// without the real 350 MB weights. Set PC_TEST_ONNX to the generated model to run.
/// </summary>
public class OnnxClassifierPlumbingTests
{
    static string? TestModel => Environment.GetEnvironmentVariable("PC_TEST_ONNX");

    static SKBitmap Solid(SKColor c)
    {
        var b = new SKBitmap(224, 224);
        using var canvas = new SKCanvas(b);
        canvas.Clear(c);
        return b;
    }

    [SkippableFact]
    public void Full_inference_path_produces_expected_ordering()
    {
        Skip.If(string.IsNullOrEmpty(TestModel) || !File.Exists(TestModel),
            "Set PC_TEST_ONNX to the synthetic model (scratchpad/make_test_model.py) to run.");

        using var clf = new OnnxNsfwClassifier(TestModel!);
        using var red = Solid(new SKColor(255, 0, 0));
        using var blue = Solid(new SKColor(0, 0, 255));

        float redScore = clf.ScoreBitmap(red);
        float blueScore = clf.ScoreBitmap(blue);

        // Both are valid probabilities…
        Assert.InRange(redScore, 0f, 1f);
        Assert.InRange(blueScore, 0f, 1f);
        // …and the rigged model orders them: red (nsfw-weighted) > blue.
        Assert.True(redScore > 0.9f, $"red should score nsfw-high, got {redScore}");
        Assert.True(blueScore < 0.1f, $"blue should score nsfw-low, got {blueScore}");
    }
}

/// <summary>
/// The score-validation harness required by DESIGN §8. It only runs when a real model AND a
/// labeled fixture set are provided via environment variables, because neither can live in
/// the repo (the model is ~350 MB; NSFW fixtures are not committed). To validate scores:
///
///   PC_NSFW_MODEL=/path/to/model.onnx \
///   PC_NSFW_FIXTURES=/path/to/fixtures  \   # contains nsfw/*.jpg and sfw/*.jpg
///   dotnet test --filter Category=NsfwModelValidation
///
/// Every image under fixtures/nsfw must score >= 0.6 and every image under fixtures/sfw
/// must score &lt;= 0.4, or the assumed preprocessing/label-index is wrong.
/// </summary>
[Trait("Category", "NsfwModelValidation")]
public class NsfwModelValidationTests
{
    static string? Model => Environment.GetEnvironmentVariable("PC_NSFW_MODEL");
    static string? Fixtures => Environment.GetEnvironmentVariable("PC_NSFW_FIXTURES");

    [SkippableFact]
    public void Labeled_fixtures_score_on_the_correct_side_of_the_threshold()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set PC_NSFW_MODEL to a real Falconsai ONNX model to run score validation.");
        Skip.If(string.IsNullOrEmpty(Fixtures) || !Directory.Exists(Fixtures),
            "Set PC_NSFW_FIXTURES to a folder with nsfw/ and sfw/ labeled images.");

        using var clf = new OnnxNsfwClassifier(Model!);
        Assert.True(clf.ConfigFromFile,
            "preprocessor_config.json should sit beside the model so real constants are used.");

        foreach (var f in Directory.EnumerateFiles(Path.Combine(Fixtures!, "nsfw")))
            Assert.True(clf.ScoreFile(f) >= 0.6f, $"expected NSFW>=0.6 for {Path.GetFileName(f)}");
        foreach (var f in Directory.EnumerateFiles(Path.Combine(Fixtures!, "sfw")))
            Assert.True(clf.ScoreFile(f) <= 0.4f, $"expected NSFW<=0.4 for {Path.GetFileName(f)}");
    }

    /// <summary>
    /// The rule, end to end, over the same fixtures: how much of <c>nsfw/</c> gets flagged, and
    /// how much of <c>sfw/</c> wrongly does.
    /// </summary>
    /// <remarks>
    /// <para>The test above asks whether the model scores an image sensibly. This one asks the
    /// question the app actually answers — is this photo flagged — which is a different question
    /// once the rule reads two numbers instead of one.</para>
    ///
    /// <para><b>The false-positive assertion is the point.</b> An earlier version of tiling took
    /// the maximum tile score, which on paper nearly doubled recall and in practice flagged 8 of
    /// 17 photos in an ordinary family album, because a clothed head-and-shoulders shot contains
    /// one tile that is nearly all skin. It was caught by a person recognising a photo, not by
    /// anything here. A recall-only test would have passed it happily, so precision is asserted
    /// first and strictly: not one image under <c>sfw/</c> may be flagged.</para>
    ///
    /// <para>Recall is asserted loosely (a floor, not a number) because it depends on what is in
    /// the fixture set. Measured on the folder this was developed against: 17 of 41 whole-frame,
    /// 25 of 41 tiled.</para>
    /// </remarks>
    [SkippableFact]
    public void The_flag_rule_keeps_ordinary_photos_out_of_the_review_queue()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set PC_NSFW_MODEL to a real Falconsai ONNX model to run rule validation.");
        Skip.If(string.IsNullOrEmpty(Fixtures) || !Directory.Exists(Fixtures),
            "Set PC_NSFW_FIXTURES to a folder with nsfw/ and sfw/ labeled images.");

        using var clf = new OnnxNsfwClassifier(Model!);
        var rule = NsfwSettings.Default;

        var flaggedClean = new List<string>();
        int cleanTotal = 0;
        foreach (var f in Directory.EnumerateFiles(Path.Combine(Fixtures!, "sfw")))
        {
            cleanTotal++;
            var v = clf.ScoreFile(f, rule.Depth);
            if (rule.IsExplicit(v.Whole, v.TileMean))
                flaggedClean.Add($"{Path.GetFileName(f)} (whole {v.Whole:F3}, tiles {v.TileMean:F3})");
        }

        Assert.True(flaggedClean.Count == 0,
            $"{flaggedClean.Count} of {cleanTotal} known-clean images were flagged explicit:{Environment.NewLine}"
            + string.Join(Environment.NewLine, flaggedClean));

        int explicitTotal = 0, caught = 0, caughtWholeFrame = 0;
        foreach (var f in Directory.EnumerateFiles(Path.Combine(Fixtures!, "nsfw")))
        {
            explicitTotal++;
            var v = clf.ScoreFile(f, rule.Depth);
            if (rule.IsExplicit(v.Whole, v.TileMean)) caught++;
            if (rule.IsExplicit(v.Whole, null)) caughtWholeFrame++;
        }

        Skip.If(explicitTotal == 0, "No images under fixtures/nsfw.");

        // Tiling has to earn its ten-fold cost. Equal recall means the tiles are contributing
        // nothing and the setting is pure slowdown.
        Assert.True(caught >= caughtWholeFrame,
            $"tiled recall {caught}/{explicitTotal} is below whole-frame {caughtWholeFrame}/{explicitTotal}");

        // A floor, not a target: fixtures vary. Half is well under what was measured (25/41) and
        // still far enough above chance to fail loudly if the rule is broken rather than merely
        // retuned.
        Assert.True(caught * 2 >= explicitTotal,
            $"only {caught} of {explicitTotal} known-explicit images were flagged");
    }
}

/// <summary>
/// Covers the id-scope (selection / current folder) that <c>ScoreAllAsync</c> was given so a
/// wrong-folder scan or a whole-library re-run couldn't force scoring photos the user never
/// asked about (AppState.NsfwAsync). Exercises <see cref="NsfwScorer.CollectTargets"/> directly
/// rather than the full ScoreAllAsync run, since a real run needs the ~350 MB ONNX model this
/// test suite deliberately doesn't depend on.
/// </summary>
public class NsfwScorerScopeTests : IDisposable
{
    readonly string _work =
        Path.Combine(Path.GetTempPath(), "pc_nsfw_scope_" + Guid.NewGuid().ToString("N"));
    readonly string _dbPath;

    public NsfwScorerScopeTests()
    {
        Directory.CreateDirectory(_work);
        _dbPath = Path.Combine(_work, "catalog.db");
    }

    static ImageRecord Row(string path, double? nsfwScore) => new()
    {
        Path = path,
        ContentHash = "h_" + path,
        FileSize = 1,
        Mtime = 1,
        NsfwScore = nsfwScore,
    };

    [Fact]
    public void CollectTargets_WithImageIds_ScopesToThoseIdsAndSkipsAlreadyScored()
    {
        using var db = new Database(_dbPath);
        var repo = new ImageRepository(db);

        var inScopeUnscored = repo.Upsert(Row("/lib/folder/a.jpg", nsfwScore: null));
        var inScopeAlreadyScored = repo.Upsert(Row("/lib/folder/b.jpg", nsfwScore: 0.7));
        var outOfScopeUnscored = repo.Upsert(Row("/lib/other/c.jpg", nsfwScore: null));

        var scorer = new NsfwScorer(db, modelPath: "/does/not/matter/for/CollectTargets.onnx");

        // Scope is exactly {a, b}: b is filtered out for being already scored, and c (unscored,
        // but never in the requested scope) must not sneak in just because it's eligible.
        //
        // WholeFrame explicitly: b carries a whole-frame score and no tile mean, which a tiled
        // run legitimately still owes work on (see the test below). This one is about scope.
        var targets = scorer.CollectTargets(
            root: null, imageIds: [inScopeUnscored, inScopeAlreadyScored], rescoreExisting: false,
            depth: NsfwDepth.WholeFrame);

        Assert.Equal([inScopeUnscored], targets.Select(t => t.id));
        Assert.DoesNotContain(targets, t => t.id == outOfScopeUnscored);
    }

    /// <summary>
    /// A photo scored the quick way is not done as far as a tiled run is concerned.
    /// </summary>
    /// <remarks>
    /// Without this, turning on the deeper setting would appear to do nothing at all: every
    /// already-scored photo would be skipped as "already scored", so the only photos that ever
    /// got the better treatment would be ones added afterwards. The null-ness of
    /// <c>nsfw_tile_mean</c> is what carries the distinction, which is why it has no default.
    /// </remarks>
    [Fact]
    public void CollectTargets_Tiled_ReScoresPhotosThatOnlyHaveAWholeFrameScore()
    {
        using var db = new Database(_dbPath);
        var repo = new ImageRepository(db);

        var wholeFrameOnly = repo.Upsert(Row("/lib/folder/a.jpg", nsfwScore: 0.7));
        var scorer = new NsfwScorer(db, modelPath: "/does/not/matter/for/CollectTargets.onnx");

        Assert.Equal(
            [wholeFrameOnly],
            scorer.CollectTargets(root: null, imageIds: [wholeFrameOnly], rescoreExisting: false,
                depth: NsfwDepth.Tiled).Select(t => t.id));

        Assert.Empty(
            scorer.CollectTargets(root: null, imageIds: [wholeFrameOnly], rescoreExisting: false,
                depth: NsfwDepth.WholeFrame));
    }

    [Fact]
    public void CollectTargets_WithImageIds_RescoreExisting_IncludesAlreadyScored()
    {
        using var db = new Database(_dbPath);
        var repo = new ImageRepository(db);

        var id = repo.Upsert(Row("/lib/folder/a.jpg", nsfwScore: 0.7));

        var scorer = new NsfwScorer(db, modelPath: "/does/not/matter/for/CollectTargets.onnx");
        var targets = scorer.CollectTargets(root: null, imageIds: [id], rescoreExisting: true);

        Assert.Equal([id], targets.Select(t => t.id));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_work)) Directory.Delete(_work, true); } catch { }
    }
}
