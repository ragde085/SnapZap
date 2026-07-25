using SnapZap.Core.Data;
using SnapZap.Core.Imaging;
using SnapZap.Core.Scanning;

namespace SnapZap.App;

/// <summary>
/// Process-wide catalog: owns the SQLite database, imaging service, and scanner for the
/// currently-open folder. App-data (db + thumbnails) lives outside the scanned folder so we
/// never write into the user's library (DESIGN safety invariant §7.5).
/// </summary>
public sealed class CatalogService : IDisposable
{
    readonly string _appData;
    Database? _db;
    readonly object _gate = new();

    public SkiaImageService Imaging { get; } = new();
    public string ThumbDir { get; }

    /// <summary>Sidecar model path: beside the binary, overridable via PC_NSFW_MODEL.</summary>
    public string NsfwModelPath { get; }

    /// <summary>Manifests folder (app-data, never the export destination).</summary>
    public string ManifestDir { get; }

    /// <summary>The most recently scanned folder — the export SourceRoot for mirror/move.</summary>
    public string? LastScannedFolder { get; private set; }

    /// <summary>Root of the app's own data (catalog, thumbnails, manifests, settings).
    /// Never inside the user's photo library.</summary>
    public string AppDataDir => _appData;

    public CatalogService()
    {
        _appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SnapZap");
        ThumbDir = Path.Combine(_appData, "thumbs");
        Directory.CreateDirectory(ThumbDir);
        _db = new Database(Path.Combine(_appData, "catalog.db"));

        NsfwModelPath = Environment.GetEnvironmentVariable("PC_NSFW_MODEL")
            ?? Path.Combine(AppContext.BaseDirectory, "models", "nsfw.onnx");
        ManifestDir = Path.Combine(_appData, "manifests");
        Directory.CreateDirectory(ManifestDir);
    }

    public Database Db => _db ?? throw new InvalidOperationException("catalog not open");

    public ImageRepository Images => new(Db);

    public Task<ScanResult> ScanAsync(string folder, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException(folder);
        LastScannedFolder = folder;
        var scanner = new Scanner(Db, Imaging, ThumbDir);
        return scanner.ScanAsync(folder, progress, ct);
    }

    public void Dispose()
    {
        lock (_gate) { _db?.Dispose(); _db = null; }
    }
}
