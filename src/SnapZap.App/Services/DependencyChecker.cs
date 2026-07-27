using System.Text.Json;

namespace SnapZap.App.Services;

/// <summary>
/// One file to fetch when installing by hand. Deliberately points at the file itself rather
/// than a project page: both upstreams publish dozens of assets and picking the wrong one
/// produces a sidecar that loads but misbehaves.
/// </summary>
/// <param name="SaveAs">Name the file must have once it is in place, which is never the name
/// it downloads under.</param>
public sealed record DownloadLink(string Url, string Label, string SaveAs, string? Size = null);

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

    /// <summary>Directory to drop the file(s) into for the app to pick it up.</summary>
    public required string InstallTo { get; init; }

    /// <summary>Everything the manual route has to fetch — more than one when the sidecar is
    /// useless without a companion file.</summary>
    public required IReadOnlyList<DownloadLink> Downloads { get; init; }

    /// <summary>One command that installs this and puts it where the app looks. Shown first,
    /// because the manual route below it is the fallback, not the happy path.</summary>
    public string? Command { get; init; }

    /// <summary>What has to happen after the command before the app will see the file.
    /// Null when it takes effect as soon as <em>Check again</em> is pressed.</summary>
    public string? CommandNote { get; init; }

    /// <summary>
    /// Shown instead of <see cref="Command"/> when SnapZap was installed by the Windows
    /// installer. An installed copy has no <c>scripts/</c> directory, so quoting a command
    /// from it sends the user looking for a file that isn't there; the installer offers this
    /// as a component and is the honest answer.
    /// </summary>
    public string? InstallerHint { get; init; }

    /// <summary>Optional extra step after a manual download (e.g. chmod on macOS).</summary>
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

    // Kept in step with scripts/install-deps.{sh,bat} — the dialog quotes those scripts, so a
    // version bump there has to land here too or the app will advertise a stale command.
    //
    // Only one sidecar remains. czkawka was removed when similar-image detection moved in-process
    // (docs/DEDUP-V2.md); the list is still built as a list because the shape of the dialog, the
    // Setup panel and the rail pip all render from it, and collapsing it to a single item would
    // just have to be undone the next time something optional appears.
    const string ModelRevision = "1ceb3c7fe1e9f3f2507e6df577437f23a9149fd5";
    const string ModelBase =
        "https://huggingface.co/onnx-community/nsfw_image_detection-ONNX/resolve/" + ModelRevision;

    IReadOnlyList<DependencyInfo> Detect()
    {
        var sidecarDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var win = OperatingSystem.IsWindows();

        var installer = win ? @"scripts\install-deps.bat" : "scripts/install-deps.sh";
        var repoRoot = FindRepoRoot(sidecarDir, installer);

        // An installed copy (installer/SnapZap.iss) leaves its uninstaller beside the binary.
        // Nothing else in the layout distinguishes it from an unzipped publish folder, and the
        // difference matters: one of them can run scripts\install-deps.bat and the other has
        // never had a scripts directory.
        var installedCopy = win && Directory.EnumerateFiles(sidecarDir, "unins*.exe").Any();

        // Two shapes, because the right one depends on how the app was started. From a repo
        // build the plain command installs into <repo>/models and <repo>/tools, which the next
        // build copies into this directory — shorter, and it survives deleting bin/. From a
        // published folder there is no repo above us, so target this directory explicitly.
        bool FromRepo(string? destDir) =>
            repoRoot is not null && destDir is not null && destDir.StartsWith(repoRoot, StringComparison.Ordinal);

        string Install(string flag, string? destDir) => FromRepo(destDir)
            ? $"{installer} {flag}"
            : $"{installer} {flag} --dest \"{destDir ?? sidecarDir}\"";

        // The repo-level install lands in <repo>/models; the build is what copies it here, so
        // "Check again" alone would still report it missing.
        string? Note(string? destDir) => FromRepo(destDir)
            ? "Then restart the app — the build copies it into place."
            : null;

        var modelPath = _catalog.NsfwModelPath;
        var modelFound = File.Exists(modelPath);
        var modelDir = Path.GetDirectoryName(modelPath) ?? sidecarDir;

        // The installer writes to <dest>/models, so it can only be pointed at a model directory
        // whose last segment is "models". PC_NSFW_MODEL can name any path at all; when it names
        // something else, offering a command that would install elsewhere is worse than
        // offering none, and the manual steps below still spell out the exact folder.
        var modelDest = Path.GetFileName(modelDir) == "models" ? Path.GetDirectoryName(modelDir) : null;

        return
        [
            new DependencyInfo
            {
                Id = "nsfw-model",
                Name = "NSFW scoring",
                Unlocks = "Scores every photo so you can filter explicit images.",
                WithoutIt = "Every other filter — duplicates, blur, date, folder — is unaffected.",
                Found = modelFound,
                FoundAt = modelFound ? modelPath : null,
                FileName = "nsfw.onnx",
                InstallTo = modelDir,
                Command = modelDest is null || installedCopy ? null : Install("--model-only", modelDest),
                CommandNote = modelDest is null || installedCopy ? null : Note(modelDest),
                InstallerHint = installedCopy
                    ? "Run the SnapZap installer again and tick “Explicit-content scoring " +
                      "model”. It downloads the file, checks it, and puts it where SnapZap " +
                      "looks — your settings and catalogue are left alone."
                    : null,
                Downloads =
                [
                    new DownloadLink($"{ModelBase}/onnx/model.onnx", "model.onnx", "nsfw.onnx", "328 MB"),
                    // Listed as a download of its own rather than a footnote: the app reads the
                    // real mean/std/size from it, and a plausible-but-wrong score is worse here
                    // than no score at all.
                    new DownloadLink($"{ModelBase}/preprocessor_config.json",
                        "preprocessor_config.json", "preprocessor_config.json", "1 KB"),
                ],
            },
        ];
    }

    /// <summary>
    /// The SnapZap checkout this binary was built from, or null when there isn't one — the
    /// normal case for a published app. Identified by the installer script itself rather than
    /// by <c>.git</c>, because the script is what the command we print actually needs.
    /// </summary>
    static string? FindRepoRoot(string startDir, string installerRelativePath)
    {
        var dir = new DirectoryInfo(startDir);
        // bin/<config>/<tfm> is three levels below the project, four below the repo. A couple
        // of extra levels cost nothing and cover deeper output layouts (e.g. a RID subfolder).
        for (int i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, installerRelativePath)))
                return dir.FullName;
        }
        return null;
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
