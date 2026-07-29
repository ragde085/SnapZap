using SnapZap.Core;
using SnapZap.Core.Data;
using SnapZap.Core.Delete;
using SnapZap.Core.Export;
using SnapZap.Core.Imaging;
using SnapZap.Core.Platform;
using SnapZap.Core.Scanning;
using SkiaSharp;
using Xunit;

namespace SnapZap.Tests;

/// <summary>
/// <see cref="FolderTrashService"/> and <see cref="PortableLinkStub"/> are the portable,
/// Android-ready platform-service implementations (docs/ANDROID-PORT-ACS.md §E3). Unlike
/// <c>DeleteTests</c>'s macOS-only cases, every test here runs on every OS this library
/// targets — there is nothing platform-specific left to skip.
/// </summary>
public class FolderTrashTests : IDisposable
{
    readonly string _work = Path.Combine(Path.GetTempPath(), "pc_trash_" + Guid.NewGuid().ToString("N"));
    readonly string _photos, _trash;

    public FolderTrashTests()
    {
        _photos = Path.Combine(_work, "photos");
        _trash = Path.Combine(_work, "trash");
        Directory.CreateDirectory(_photos);
    }

    static void WriteBytes(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    static byte[] Content(string seed) =>
        System.Text.Encoding.UTF8.GetBytes($"content-{seed}-{Guid.NewGuid():N}");

    [Fact]
    public async Task Trash_then_restore_round_trips_the_files_exact_bytes()
    {
        var original = Path.Combine(_photos, "gone.png");
        var bytes = Content("a");
        WriteBytes(original, bytes);
        var svc = new FolderTrashService(_trash);

        var loc = await svc.SendToTrashAsync(original);
        Assert.NotNull(loc);
        Assert.False(File.Exists(original), "the original path should be empty after trashing");
        Assert.True(File.Exists(loc), "the trashed file should exist at the returned location");
        Assert.Equal(bytes, await File.ReadAllBytesAsync(loc!));

        var restored = await svc.RestoreAsync(original, loc);
        Assert.True(restored);
        Assert.True(File.Exists(original));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(original));
        Assert.False(File.Exists(loc!), "the trash copy should be gone once restored, not duplicated");
    }

    [Fact]
    public async Task Two_files_with_the_same_name_from_different_folders_both_survive_one_flat_trash_root()
    {
        var pathA = Path.Combine(_photos, "folderA", "dup.png");
        var pathB = Path.Combine(_photos, "folderB", "dup.png");
        var bytesA = Content("a");
        var bytesB = Content("b");
        WriteBytes(pathA, bytesA);
        WriteBytes(pathB, bytesB);
        var svc = new FolderTrashService(_trash);

        var locA = await svc.SendToTrashAsync(pathA);
        var locB = await svc.SendToTrashAsync(pathB);

        Assert.NotEqual(locA, locB); // GUID prefix keeps the identical basename from colliding
        Assert.True(File.Exists(locA));
        Assert.True(File.Exists(locB));
        Assert.Equal(bytesA, await File.ReadAllBytesAsync(locA!));
        Assert.Equal(bytesB, await File.ReadAllBytesAsync(locB!));
        Assert.Equal(2, Directory.GetFiles(_trash).Length);
    }

    [Fact]
    public async Task Restoring_recreates_a_missing_parent_directory()
    {
        var original = Path.Combine(_photos, "nested", "deep", "photo.png");
        WriteBytes(original, Content("x"));
        var svc = new FolderTrashService(_trash);
        var loc = await svc.SendToTrashAsync(original);

        Directory.Delete(Path.Combine(_photos, "nested"), recursive: true);
        Assert.False(Directory.Exists(Path.GetDirectoryName(original)));

        var restored = await svc.RestoreAsync(original, loc);
        Assert.True(restored);
        Assert.True(File.Exists(original));
    }

    [Fact]
    public async Task Trashing_a_missing_source_throws_FileNotFoundException_predictably()
    {
        // Documented, deliberate behavior (AC-3.4): both real callers (DeleteService,
        // ExportEngine's reject path) already guard with File.Exists before calling in, so this
        // only fires for a caller bug — failing loud beats a silent no-op that looks like a
        // completed delete.
        var svc = new FolderTrashService(_trash);
        var missing = Path.Combine(_photos, "nope.png");
        await Assert.ThrowsAsync<FileNotFoundException>(() => svc.SendToTrashAsync(missing));
    }

    [Fact]
    public async Task Restoring_when_the_original_path_is_occupied_returns_false_and_does_not_overwrite()
    {
        var original = Path.Combine(_photos, "occupied.png");
        var trashedBytes = Content("trashed");
        WriteBytes(original, trashedBytes);
        var svc = new FolderTrashService(_trash);
        var loc = await svc.SendToTrashAsync(original);

        // Something new now lives at the original path (e.g. a rescan wrote a fresh file there).
        var occupantBytes = Content("occupant");
        WriteBytes(original, occupantBytes);

        var restored = await svc.RestoreAsync(original, loc);

        Assert.False(restored); // no throw (the plan's draft used overwrite:false, which throws)
        Assert.Equal(occupantBytes, await File.ReadAllBytesAsync(original)); // never overwritten
        Assert.True(File.Exists(loc), "the trashed copy must survive a rejected restore, not vanish");
    }

    [Fact]
    public async Task Restoring_a_null_trashed_location_returns_false()
    {
        var svc = new FolderTrashService(_trash);
        var restored = await svc.RestoreAsync(Path.Combine(_photos, "whatever.png"), null);
        Assert.False(restored);
    }

    [Fact]
    public async Task Restoring_a_nonexistent_trashed_location_returns_false()
    {
        var svc = new FolderTrashService(_trash);
        var bogus = Path.Combine(_trash, "never-existed.png");
        var restored = await svc.RestoreAsync(Path.Combine(_photos, "whatever.png"), bogus);
        Assert.False(restored);
    }

    [Fact]
    public async Task Cross_volume_trash_falls_back_to_copy_and_delete_and_succeeds()
    {
        var original = Path.Combine(_photos, "moved.png");
        var bytes = Content("cross");
        WriteBytes(original, bytes);

        // Simulate the fast-rename step failing the way a real cross-volume move does (Unix
        // EXDEV; distinct drive roots on Windows) without needing a second real volume mounted
        // in CI — the internal ctor's primaryMove seam exists for exactly this.
        var svc = new FolderTrashService(_trash, primaryMove: (_, _) =>
            throw new IOException("simulated EXDEV: Invalid cross-device link"));

        var loc = await svc.SendToTrashAsync(original);

        Assert.NotNull(loc);
        Assert.False(File.Exists(original), "the fallback must still release the source");
        Assert.True(File.Exists(loc));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(loc!));
    }

    [Fact]
    public async Task Cross_volume_trash_that_also_fails_the_fallback_reports_one_clear_error()
    {
        var original = Path.Combine(_photos, "unrecoverable.png");
        WriteBytes(original, Content("y"));

        // The rename fails AND leaves the source gone — the kind of partial/interrupted move a
        // flaky removable volume can genuinely produce — so the copy+delete fallback's own
        // File.Copy has nothing left to read either. Exercises "the fallback also fails" branch
        // of MoveWithCrossVolumeFallback deterministically.
        var svc = new FolderTrashService(_trash, primaryMove: (s, _) =>
        {
            File.Delete(s);
            throw new IOException("simulated EXDEV: Invalid cross-device link");
        });

        var ex = await Assert.ThrowsAsync<IOException>(() => svc.SendToTrashAsync(original));

        // "Caught, reported" means one contextual exception carrying both paths, not whatever
        // bare framework exception happened to bubble out of File.Copy.
        Assert.Contains(original, ex.Message);
        Assert.Empty(Directory.GetFiles(_trash)); // no partial-copy debris left behind
    }

    [Fact]
    public async Task Export_hardlink_mode_degrades_to_copy_instead_of_throwing_with_the_portable_stub()
    {
        // AC-3.5: exercises ExportEngine's real fallback (Transfer/RunAsync's mode-downgrade
        // check in ExportEngine.cs), not just PortableLinkStub in isolation.
        var dbPath = Path.Combine(_work, "catalog.db");
        var dest = Path.Combine(_work, "clean");
        var thumbs = Path.Combine(_work, "thumbs");
        WritePng(Path.Combine(_photos, "h.png"), SKColors.Orange);

        using var db = new Database(dbPath);
        await new Scanner(db, new SkiaImageService(), thumbs).ScanAsync(_photos);
        var id = new ImageRepository(db).All().Single().Id;

        var engine = new ExportEngine(db, new PortableLinkStub(), new FolderTrashService(_trash));
        var req = new ExportRequest
        {
            Destination = dest, SourceRoot = _photos,
            Mode = TransferMode.Hardlink, Structure = ExportStructure.Flat,
            KeeperIds = new List<long> { id },
        };

        var plan = engine.Plan(req);
        Assert.False(plan.HardlinkPossible, "the stub never reports a shared volume");

        var result = await engine.RunAsync(req);

        Assert.Equal(1, result.Exported);
        Assert.Equal(0, result.Failed);
        Assert.True(File.Exists(Path.Combine(dest, "h.png")));
    }

    static void WritePng(string path, SKColor c)
    {
        using var bmp = new SKBitmap(80, 60);
        using (var canvas = new SKCanvas(bmp)) canvas.Clear(c);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_work)) Directory.Delete(_work, true); } catch { }
    }
}
