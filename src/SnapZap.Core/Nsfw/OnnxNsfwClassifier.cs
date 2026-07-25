using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

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
            throw new FileNotFoundException("NSFW ONNX model not found", modelPath);

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
            ?? throw new InvalidOperationException($"cannot decode {imagePath}");
        return ScoreBitmap(bmp);
    }

    public void Dispose() => _session.Dispose();
}
