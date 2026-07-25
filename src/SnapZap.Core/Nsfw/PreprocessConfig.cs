using System.Text.Json;

namespace SnapZap.Core.Nsfw;

/// <summary>
/// Image-preprocessing parameters for the NSFW model. These MUST match what the model was
/// trained with — wrong mean/std/size produces confident, meaningless scores (DESIGN §8).
///
/// Defaults are the Falconsai/nsfw_image_detection (ViT) values, but the real constants are
/// read from the model's <c>preprocessor_config.json</c> when present beside the .onnx file.
/// Never assume; the config file wins.
/// </summary>
public sealed record PreprocessConfig
{
    public int Size { get; init; } = 224;
    public float[] Mean { get; init; } = [0.5f, 0.5f, 0.5f];
    public float[] Std { get; init; } = [0.5f, 0.5f, 0.5f];
    public float RescaleFactor { get; init; } = 1f / 255f;

    /// <summary>Index of the "nsfw" class in the model's output logits. Falconsai: 0=normal, 1=nsfw.</summary>
    public int NsfwLabelIndex { get; init; } = 1;

    /// <summary>Load from a HuggingFace-style preprocessor_config.json; missing fields keep defaults.</summary>
    public static PreprocessConfig FromJsonFile(string path)
    {
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var cfg = new PreprocessConfig();
        int size = cfg.Size;
        if (root.TryGetProperty("size", out var sz))
        {
            if (sz.ValueKind == JsonValueKind.Number) size = sz.GetInt32();
            else if (sz.ValueKind == JsonValueKind.Object)
            {
                if (sz.TryGetProperty("height", out var hh)) size = hh.GetInt32();
                else if (sz.TryGetProperty("shortest_edge", out var se)) size = se.GetInt32();
            }
        }

        return cfg with
        {
            Size = size,
            Mean = ReadTriple(root, "image_mean", cfg.Mean),
            Std = ReadTriple(root, "image_std", cfg.Std),
            RescaleFactor = root.TryGetProperty("rescale_factor", out var rf) && rf.ValueKind == JsonValueKind.Number
                ? rf.GetSingle() : cfg.RescaleFactor,
        };
    }

    /// <summary>Config beside the model file, or defaults if none. Also logs which was used.</summary>
    public static PreprocessConfig ForModel(string modelPath, out bool fromFile)
    {
        var cfgPath = Path.Combine(Path.GetDirectoryName(modelPath) ?? ".", "preprocessor_config.json");
        if (File.Exists(cfgPath)) { fromFile = true; return FromJsonFile(cfgPath); }
        fromFile = false;
        return new PreprocessConfig();
    }

    static float[] ReadTriple(JsonElement root, string name, float[] fallback)
    {
        if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return fallback;
        var vals = arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number)
                      .Select(e => e.GetSingle()).ToArray();
        return vals.Length == 3 ? vals : fallback;
    }
}
