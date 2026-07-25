using System.Text.Json;
using SnapZap.Core.Dedup;

namespace SnapZap.App.Services;

/// <summary>
/// One optional sidecar the app can use. Everything here is optional by design: SnapZap's
/// core workflow (scan, exact-duplicate detection, export, delete) works with none of them
/// installed, so a missing dependency is reported as reduced capability, never as an error.
/// </summary>
public sealed record DependencyInfo
{
    public required string Id { get; init; }

    /// <summary>What the user calls the capability, not the vendor's product name.</summary>
    public required string Name { get; init; }

    /// <summary>What works only when this is present.</summary>
    public required string Unlocks { get; init; }

    /// <summary>What still works without it — so a missing sidecar doesn't read as breakage.</summary>
    public required string WithoutIt { get; init; }

    public required bool Found { get; init; }

    /// <summary>Where it was actually resolved from, shown so the user can confirm which copy is in use.</summary>
    public string? FoundAt { get; init; }

    /// <summary>Exact file name the app looks for.</summary>
    public required string FileName { get; init; }

    /// <summary>Directory to drop the file into for the app to pick it up.</summary>
    public required string InstallTo { get; init; }

    public required string DownloadUrl { get; init; }
    public required string DownloadLabel { get; init; }

    /// <summary>Optional extra step (e.g. a script to run, or chmod on macOS).</summary>
    public string? ExtraStep { get; init; }
}

sealed class StoredSettings
{
    public bool SuppressDependencyPrompt { get; set; }
}

/// <summary>
/// Resolves the optional sidecars at launch and remembers whether the user asked not to be
/// prompted again. Detection deliberately reuses the same lookup the features themselves use,
/// so "Ready" here can never disagree with what happens when the feature runs.
/// </summary>
public sealed class DependencyChecker
{
    readonly CatalogService _catalog;
    readonly string _settingsPath;
    StoredSettings _settings;

    public DependencyChecker(CatalogService catalog)
    {
        _catalog = catalog;
        _settingsPath = Path.Combine(catalog.AppDataDir, "settings.json");
        _settings = Load(_settingsPath);
    }

    public bool PromptSuppressed
    {
        get => _settings.SuppressDependencyPrompt;
        set
        {
            if (_settings.SuppressDependencyPrompt == value) return;
            _settings.SuppressDependencyPrompt = value;
            Save();
        }
    }

    /// <summary>Raised after a re-check, so every view of dependency state updates together
    /// rather than each holding its own snapshot.</summary>
    public event Action? Changed;

    IReadOnlyList<DependencyInfo>? _current;

    /// <summary>Last known state, detected on first use. Cheap to read repeatedly.</summary>
    public IReadOnlyList<DependencyInfo> Current => _current ??= Detect();

    public int MissingCount => Current.Count(d => !d.Found);

    /// <summary>Re-runs detection from scratch, so "Check again" reflects a file the user
    /// just dropped in without needing an app restart.</summary>
    public IReadOnlyList<DependencyInfo> Check()
    {
        _current = Detect();
        Changed?.Invoke();
        return _current;
    }

    IReadOnlyList<DependencyInfo> Detect()
    {
        var sidecarDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        // Same resolution the dedup run uses: explicit config → beside the binary → PATH.
        var czkawkaPath = new CzkawkaFinder(_catalog.Db).LocateBinary();
        var czkawkaName = OperatingSystem.IsWindows() ? "czkawka_cli.exe" : "czkawka_cli";

        var modelPath = _catalog.NsfwModelPath;
        var modelFound = File.Exists(modelPath);

        return
        [
            new DependencyInfo
            {
                Id = "czkawka",
                Name = "Similar-photo detection",
                Unlocks = "Finds photos that look alike — bursts, re-edits, resized copies.",
                WithoutIt = "Exact duplicates are still found, from SnapZap's own checksums.",
                Found = czkawkaPath is not null,
                FoundAt = czkawkaPath,
                FileName = czkawkaName,
                InstallTo = sidecarDir,
                DownloadUrl = "https://github.com/qarmin/czkawka/releases",
                DownloadLabel = "Download czkawka_cli",
                ExtraStep = OperatingSystem.IsWindows()
                    ? null
                    : $"Then make it runnable:  chmod +x \"{Path.Combine(sidecarDir, czkawkaName)}\"",
            },
            new DependencyInfo
            {
                Id = "nsfw-model",
                Name = "NSFW scoring",
                Unlocks = "Scores every photo so you can filter explicit images.",
                WithoutIt = "Every other filter — duplicates, blur, date, folder — is unaffected.",
                Found = modelFound,
                FoundAt = modelFound ? modelPath : null,
                FileName = "nsfw.onnx",
                InstallTo = Path.GetDirectoryName(modelPath) ?? sidecarDir,
                DownloadUrl = "https://huggingface.co/Falconsai/nsfw_image_detection",
                DownloadLabel = "About the model",
                ExtraStep = "Produced by scripts/get-nsfw-model.sh in the SnapZap repo — it exports the model to ONNX. Or set PC_NSFW_MODEL to a copy you already have.",
            },
        ];
    }

    static StoredSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(path)) ?? new();
        }
        catch { /* unreadable settings are not worth failing a launch over */ }
        return new StoredSettings();
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath,
                JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* preference persistence is best-effort */ }
    }
}
