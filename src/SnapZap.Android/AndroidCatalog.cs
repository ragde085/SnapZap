using SnapZap.Core.Data;
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
        Db = new Database(Path.Combine(AppDataDir, "catalog.db"));
    }

    public Scanner NewScanner() => new(Db, Imaging, ThumbDir);

    public ImageRepository Images => new(Db);

    public void Dispose() => Db.Dispose();
}
