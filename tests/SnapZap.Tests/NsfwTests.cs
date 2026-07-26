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
}
