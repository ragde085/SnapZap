using SnapZap.Core.Data;
using SnapZap.Core.Delete;
using SnapZap.Core.Platform;
using SnapZap.Core.Imaging;
using SnapZap.Core.Scanning;

namespace SnapZap.Droid;

/// <summary>
/// The Android equivalent of <c>SnapZap.App.CatalogService</c>: owns where the catalogue,
/// thumbnails and previews live, and hands out the Core services wired to them.
///
/// Deliberately a re-implementation rather than a reference. <c>CatalogService</c> lives in
/// <c>SnapZap.App</c>, which an Android head cannot reference at all — it is a non-self-contained
/// <c>Exe</c> and, more fundamentally, there is no ASP.NET Core for Android
/// (docs/ANDROID-PORT-ACS.md §1.5). What matters is that the *layout* matches the desktop's, since
/// both are reading a catalogue produced by the same Core code.
///
/// The location is confirmed on-device rather than assumed (AC-0.5): <c>LocalApplicationData</c>
/// resolves to <c>/data/user/0/com.snapzap.android/files</c> — app-private internal storage, wiped
/// on uninstall, outside any folder the user scans. That satisfies the DESIGN §7.5 invariant that
/// the app never writes into the user's photo library.
/// </summary>
public sealed class AndroidCatalog : IDisposable
{
    public string AppDataDir { get; }
    public string ThumbDir { get; }
    public Database Db { get; }
    public SkiaImageService Imaging { get; } = new();

    public AndroidCatalog()
    {
        AppDataDir = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "SnapZap");
        ThumbDir = Path.Combine(AppDataDir, "thumbs");
        Directory.CreateDirectory(ThumbDir);
        TrashDir = Path.Combine(AppDataDir, "trash");
        Directory.CreateDirectory(TrashDir);
        Db = new Database(Path.Combine(AppDataDir, "catalog.db"));
    }

    public Scanner NewScanner() => new(Db, Imaging, ThumbDir);

    /// <summary>
    /// The folder the catalogue was last scanned from — the same <c>meta</c> key the desktop's
    /// <c>CatalogService.ScanRoot</c> reads and writes, so the two heads agree on what "the folder
    /// you are working on" means for a catalogue they can both open.
    /// </summary>
    /// <remarks>
    /// Android held this only in memory, so every relaunch reset the scan target to the platform
    /// default while the library still held photos from somewhere else entirely. The Plan tab then
    /// printed the default folder directly above the count of photos that did not come from it, and
    /// "Re-scan" would have read the wrong folder.
    /// </remarks>
    public string? ScanRoot
    {
        get => Db.Meta("scan_root");
        set => Db.SetMeta("scan_root", value);
    }

    /// <summary>
    /// Trash root. App-private external storage: writable with no permission at all even under
    /// scoped storage, survives restarts, and is cleared on uninstall — which is the expected
    /// lifetime for a trash.
    ///
    /// This is <b>not</b> the system Gallery's trash. It plays the same role as <c>~/.Trash</c>
    /// does in the macOS implementation, without OS-level integration, and it is deliberately
    /// outside the folders the user scans so recycling never writes into their library
    /// (DESIGN §7.5).
    ///
    /// ⚠ It is also invisible to the user's file manager, so it needs a size read-out and an
    /// empty action before v1 — tracked as AC-3.6. Deleting from a large library moves originals
    /// here, and silently consuming storage with no way to see or reclaim it is not acceptable.
    /// </summary>
    public string TrashDir { get; }

    /// <summary>
    /// The same portable, unit-tested implementation the desktop would use for a folder-backed
    /// trash — no Android SDK types, so its behaviour is pinned by tests running on macOS rather
    /// than only ever exercised on a phone.
    /// </summary>
    public ITrashService Trash => new FolderTrashService(TrashDir);

    public DeleteService NewDeleteService() => new(Db, Trash);

    public ImageRepository Images => new(Db);

    public void Dispose() => Db.Dispose();
}
