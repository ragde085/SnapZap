using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;
using SnapZap.Core.Resources;

namespace SnapZap.Core.Nsfw;

/// <summary>
/// NSFW classifier over an ONNX model (Falconsai ViT). Produces a single 0–1 score = P(nsfw).
/// The model ships as a sidecar (~350 MB) beside the binary; if absent, callers should treat
/// NSFW scoring as unavailable and continue (same graceful-degrade contract as Czkawka).
/// </summary>
public sealed class OnnxNsfwClassifier : IDisposable
{
    readonly InferenceSession _session;
    readonly PreprocessConfig _cfg;
    readonly string _inputName;
    readonly string _outputName;

    public PreprocessConfig Config => _cfg;
    public bool ConfigFromFile { get; }

    public OnnxNsfwClassifier(string modelPath, SessionOptions? options = null)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException(CoreStrings.Get("NsfwModelNotFound"), modelPath);

        _session = options is null ? new InferenceSession(modelPath) : new InferenceSession(modelPath, options);
        _cfg = PreprocessConfig.ForModel(modelPath, out var fromFile);
        ConfigFromFile = fromFile;
        _inputName = _session.InputMetadata.Keys.First();
        _outputName = _session.OutputMetadata.Keys.First();
    }

    /// <summary>P(nsfw) in [0,1] for a decoded bitmap.</summary>
    public float ScoreBitmap(SKBitmap bitmap)
    {
        var input = NsfwPreprocess.ToTensor(bitmap, _cfg);
        using var results = _session.Run(
            [NamedOnnxValue.CreateFromTensor(_inputName, input)]);
        var logits = results.First(r => r.Name == _outputName).AsEnumerable<float>().ToArray();
        var probs = NsfwPreprocess.Softmax(logits);
        // Clamp label index defensively in case a model has a different head arity.
        int idx = Math.Min(_cfg.NsfwLabelIndex, probs.Length - 1);
        return probs[idx];
    }

    public float ScoreFile(string imagePath)
    {
        using var bmp = SKBitmap.Decode(imagePath)
            ?? throw new InvalidOperationException(CoreStrings.Get("CannotDecode", imagePath));
        return ScoreBitmap(bmp);
    }

    /// <summary>
    /// Score one image at the requested depth.
    /// </summary>
    /// <remarks>
    /// The tiled pass exists because the model sees a 224×224 thumbnail of whatever it is given.
    /// A person occupying a corner of a wide photo survives that resize as a smear, and the model
    /// then reports — with confidence — that the photo is clean. One measured case scored 0.0014
    /// whole-frame and 0.9983 on the tile containing the subject. Nothing about the threshold can
    /// recover that; the information is gone before inference starts.
    /// </remarks>
    public NsfwVerdict Score(SKBitmap bitmap, NsfwDepth depth)
    {
        double whole = ScoreBitmap(bitmap);
        if (depth == NsfwDepth.WholeFrame) return new NsfwVerdict(whole, null);

        double sum = 0;
        int n = 0;
        foreach (var (fx, fy, fw, fh) in NsfwDecision.Tiles)
        {
            using var tile = Crop(bitmap, fx, fy, fw, fh);
            if (tile is null) continue;
            sum += ScoreBitmap(tile);
            n++;
        }

        // Every tile too small to crop (a thumbnail-sized original) means there is no tile
        // evidence, not tile evidence of zero — null keeps it out of the mean-based rule.
        return new NsfwVerdict(whole, n == 0 ? null : sum / n);
    }

    public NsfwVerdict ScoreFile(string imagePath, NsfwDepth depth)
    {
        using var bmp = SKBitmap.Decode(imagePath)
            ?? throw new InvalidOperationException(CoreStrings.Get("CannotDecode", imagePath));
        return Score(bmp, depth);
    }

    /// <summary>A sub-rectangle given as fractions of the frame, or null when it would be too
    /// small for the model to make anything of.</summary>
    static SKBitmap? Crop(SKBitmap src, double fx, double fy, double fw, double fh)
    {
        int x = (int)(src.Width * fx), y = (int)(src.Height * fy);
        int w = Math.Min((int)(src.Width * fw), src.Width - x);
        int h = Math.Min((int)(src.Height * fh), src.Height - y);
        if (w < 32 || h < 32) return null;

        var dst = new SKBitmap(w, h);
        if (src.ExtractSubset(dst, SKRectI.Create(x, y, w, h))) return dst;
        dst.Dispose();
        return null;
    }

    public void Dispose() => _session.Dispose();
}
