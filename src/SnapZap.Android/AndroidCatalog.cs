using SnapZap.Core;
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

    /// <summary>
    /// The NSFW model on this device, or null. Most convenient location first.
    /// </summary>
    /// <remarks>
    /// Shared rather than duplicated per activity, because the order matters and is not obvious.
    /// A model the app downloaded lands in <see cref="AppDataDir"/>, which is private and cleaned up
    /// on uninstall. The shared-storage locations come first anyway: they are where a hand-placed
    /// file arrives, since neither <c>adb push</c> nor a browser download can write to app-private
    /// storage on a release build — <c>run-as</c> only works on a debuggable one.
    /// </remarks>
    public string? FindNsfwModel()
    {
        var storage = DirectoryRoots.AndroidPrimaryStorage;
        foreach (var candidate in new[]
        {
            Path.Combine(storage, "Download", "nsfw.onnx"),
            Path.Combine(storage, "SnapZap", "nsfw.onnx"),
            Path.Combine(AppDataDir, "nsfw.onnx"),
        })
        {
            try { if (File.Exists(candidate)) return candidate; }
            catch (Exception) { /* an unreadable candidate is just not the one */ }
        }
        return null;
    }

    /// <summary>
    /// The photos every screen should show: those under <see cref="ScanRoot"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Not <c>Images.All()</c>, and the difference is safety-relevant.</b> The catalogue
    /// keeps every folder ever scanned, because that cache is what makes re-scanning a large library
    /// near-instant. Reading it unscoped puts photos from a folder the user has since moved on from
    /// back in the grid — and, worse, inside the reach of "Select all" followed by Delete. The
    /// desktop's <c>AppState.LoadAsync</c> has scoped to <c>ScanRoot</c> for exactly this reason
    /// since it was written; the Android head called <c>All()</c> everywhere and never did.</para>
    ///
    /// <para>Exposed here rather than left to each activity so the five screens that read photos
    /// cannot disagree about what "the library" means.</para>
    /// </remarks>
    public IEnumerable<ImageRecord> ScopedImages => Images.Under(ScanRoot);

    /// <summary>
    /// Re-reads the scan root so files just restored from the trash reappear in the library.
    /// </summary>
    /// <remarks>
    /// <para><b>A restore is only half done without this.</b> <c>DeleteService.RecycleAsync</c> removes
    /// the catalogue row as well as moving the file, so <c>RestoreAsync</c> puts the file back on disk
    /// with nothing in the catalogue pointing at it. Every Android restore path — History's batch and
    /// per-item buttons, and the undo bars on the review queue and the preview — returned success and
    /// left the photo invisible in the app until the user happened to re-scan. Observed: four files
    /// back on disk, library still reporting one photo.</para>
    ///
    /// <para>The desktop has done this since it was written (<c>AppState.RestoreAndReloadAsync</c>
    /// re-scans, with the same comment); this is the Android head catching up. Kept here rather than
    /// in each activity so the four call sites cannot drift.</para>
    ///
    /// <para>Cheap in the ordinary case: the scan's two-tier cache probe means unchanged files are a
    /// directory entry each, so this costs a walk plus the restored files' own analysis, not a full
    /// re-hash.</para>
    /// </remarks>
    public async Task RescanAfterRestoreAsync(
        IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        var root = ScanRoot;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
        await NewScanner().ScanAsync(root!, progress, ct);
    }

    /// <summary>
    /// Empties the catalogue and the thumbnail cache — back to a fresh install as far as analysis
    /// goes. <b>Photos on disk are never touched.</b>
    /// </summary>
    /// <remarks>
    /// <para>Mirrors the desktop's <c>CatalogService.Forget</c>, including keeping the undo log:
    /// anything already moved to the trash stays restorable, because forgetting what was analysed
    /// must not strand files the app has taken responsibility for.</para>
    ///
    /// <para>⚠ It does clear the thumbnail cache, and on Android that has a visible cost the desktop
    /// does not advertise: History renders its entries from those thumbnails (the whole reason a
    /// delete keeps the thumbnail and drops only the row), so past batches lose their previews and
    /// show as blank tiles. Restore still works — it goes by path, not by preview. The confirmation
    /// copy says so rather than letting the user find out.</para>
    /// </remarks>
    public void Forget()
    {
        Images.DeleteAll();
        ScanRoot = null;

        // Best-effort: a cached file that will not delete is wasted disk, not a broken catalogue,
        // and the rows that referenced it are already gone.
        try
        {
            foreach (var f in Directory.EnumerateFiles(ThumbDir, "*", SearchOption.AllDirectories))
            {
                try { File.Delete(f); }
                catch (Exception) { /* see above */ }
            }
        }
        catch (Exception) { /* no cache directory is the same as an empty one */ }
    }

    /// <summary>How much the catalogue is holding, for the confirmation to quote before it wipes it.</summary>
    public (int Photos, long Bytes) Footprint()
    {
        long bytes = 0;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var f = Path.Combine(AppDataDir, "catalog.db" + suffix);
            try { if (File.Exists(f)) bytes += new FileInfo(f).Length; } catch (Exception) { }
        }
        try
        {
            foreach (var f in Directory.EnumerateFiles(ThumbDir, "*", SearchOption.AllDirectories))
            {
                try { bytes += new FileInfo(f).Length; } catch (Exception) { }
            }
        }
        catch (Exception) { }

        int photos;
        try { photos = Images.TotalCount(); } catch (Exception) { photos = 0; }
        return (photos, bytes);
    }

    public void Dispose() => Db.Dispose();
}
