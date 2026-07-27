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

    /// <summary>Cached mid-size previews served by <c>/api/full/{id}</c> (Task 26), generated
    /// lazily on first request rather than at scan time.</summary>
    public string PreviewDir { get; }

    /// <summary>Sidecar model path: beside the binary, overridable via PC_NSFW_MODEL.</summary>
    public string NsfwModelPath { get; }

    /// <summary>Manifests folder (app-data, never the export destination).</summary>
    public string ManifestDir { get; }

    /// <summary>The most recently scanned folder — the export SourceRoot for mirror/move.</summary>
    public string? LastScannedFolder { get; private set; }

    /// <summary>Root of the app's own data (catalog, thumbnails, manifests, settings).
    /// Never inside the user's photo library.</summary>
    public string AppDataDir => _appData;

    /// <param name="appDataDir">
    /// Overrides where the catalog, thumbnails and manifests live. Production always passes
    /// null and gets the real location (DESIGN §7.5); tests pass a temp directory so they
    /// don't write into the user's actual library.
    /// </param>
    public CatalogService(string? appDataDir = null)
    {
        _appData = appDataDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SnapZap");
        ThumbDir = Path.Combine(_appData, "thumbs");
        Directory.CreateDirectory(ThumbDir);
        PreviewDir = Path.Combine(_appData, "previews");
        Directory.CreateDirectory(PreviewDir);
        _db = new Database(Path.Combine(_appData, "catalog.db"));

        NsfwModelPath = Environment.GetEnvironmentVariable("PC_NSFW_MODEL")
            ?? Path.Combine(AppContext.BaseDirectory, "models", "nsfw.onnx");
        ManifestDir = Path.Combine(_appData, "manifests");
        Directory.CreateDirectory(ManifestDir);
    }

    public Database Db => _db ?? throw new InvalidOperationException("catalog not open");

    public ImageRepository Images => new(Db);

    int _recipeMigrationNoticeTaken;

    /// <summary>
    /// The signature-recipe migration message (AC 6a: "a catalog.db backup exists at a path
    /// reported to the user"), if catalogue open just ran one — consumed on first read so it
    /// surfaces exactly once per process regardless of how many circuits/tabs call this.
    /// </summary>
    public string? TakePendingRecipeMigrationNotice()
    {
        // RowsInvalidated, not just Migrated: a brand-new catalogue's very first open also "runs a
        // migration" (from no stored recipe to the current one) but touches zero rows — nothing
        // happened that a user would recognise as their catalogue changing, so nothing to report.
        if (Db.RecipeMigration is not { RowsInvalidated: > 0 } migration) return null;
        if (Interlocked.Exchange(ref _recipeMigrationNoticeTaken, 1) != 0) return null;

        return migration.BackupPath is { } path
            ? $"Photo signatures were recalculated after an app update ({migration.RowsInvalidated:N0} photos affected). " +
              $"A backup of the previous catalogue is saved at {path}."
            : "Photo signatures were recalculated after an app update. Duplicate results will refresh next time you run Find duplicates.";
    }

    const string ScanRootKey = "scan_root";

    /// <summary>
    /// The folder the session is scoped to: everything the app shows, counts and acts on lives
    /// under it. Null means unscoped, which only happens before the first scan of a fresh
    /// catalogue.
    /// </summary>
    /// <remarks>
    /// The catalogue itself keeps every folder ever scanned, because that cache is what makes
    /// re-scanning a large library near-instant (DESIGN §5). The scope is what stops that cache
    /// leaking into the view — without it a scan of one folder left the grid, the tree and the
    /// counts spanning every folder the user had ever opened, and "select everything shown"
    /// aimed the delete at all of them.
    /// </remarks>
    public string? ScanRoot
    {
        get => Db.Meta(ScanRootKey);
        private set => Db.SetMeta(ScanRootKey, value);
    }

    public Task<ScanResult> ScanAsync(string folder, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException(folder);
        LastScannedFolder = folder;
        // Scanning a folder is what chooses the folder you are working on.
        ScanRoot = folder;
        var scanner = new Scanner(Db, Imaging, ThumbDir);
        return scanner.ScanAsync(folder, progress, ct);
    }

    /// <summary>
    /// Empty the catalogue and the thumbnail/preview caches, returning the app to a fresh install
    /// as far as analysis goes. Photos on disk are never touched, and the undo log survives so
    /// anything already in the Recycle Bin can still be restored.
    /// </summary>
    public void Forget()
    {
        Images.DeleteAll();
        ScanRoot = null;
        LastScannedFolder = null;

        // Best-effort: a cached file that will not delete is wasted disk, not a broken catalogue,
        // and the rows that referenced it are already gone.
        foreach (var file in ThumbFiles().Concat(PreviewFiles()))
        {
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Bytes currently held by the catalogue database and the thumbnail/preview caches.</summary>
    public (int Photos, long DbBytes, long ThumbBytes) Footprint()
    {
        var db = Path.Combine(_appData, "catalog.db");
        long cached = 0;
        foreach (var f in ThumbFiles().Concat(PreviewFiles()))
        {
            try { cached += new FileInfo(f).Length; } catch (IOException) { /* vanished mid-walk */ }
        }
        return (Images.TotalCount(), File.Exists(db) ? new FileInfo(db).Length : 0, cached);
    }

    /// <summary>Every cached thumbnail. Recursive: they are sharded into 256 subdirectories by
    /// hash prefix, so a top-level enumeration finds none of them and reports an empty cache.</summary>
    IEnumerable<string> ThumbFiles() =>
        Directory.Exists(ThumbDir)
            ? Directory.EnumerateFiles(ThumbDir, "*", SearchOption.AllDirectories)
            : [];

    /// <summary>Every cached preview (Task 26) — sharded the same way as thumbnails, and not
    /// every photo has one yet, since previews are generated lazily on first request.</summary>
    IEnumerable<string> PreviewFiles() =>
        Directory.Exists(PreviewDir)
            ? Directory.EnumerateFiles(PreviewDir, "*", SearchOption.AllDirectories)
            : [];

    public void Dispose()
    {
        lock (_gate) { _db?.Dispose(); _db = null; }
    }
}
