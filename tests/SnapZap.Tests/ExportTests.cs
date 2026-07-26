using SnapZap.Core;
using SnapZap.Core.Data;
using SnapZap.Core.Export;
using SnapZap.Core.Imaging;
using SnapZap.Core.Platform;
using SnapZap.Core.Scanning;
using SkiaSharp;
using Xunit;

namespace SnapZap.Tests;

public class ExportTests : IDisposable
{
    readonly string _work = Path.Combine(Path.GetTempPath(), "pc_exp_" + Guid.NewGuid().ToString("N"));
    readonly string _photos, _dest, _thumbs, _dbPath;

    public ExportTests()
    {
        _photos = Path.Combine(_work, "photos");
        _dest = Path.Combine(_work, "clean");
        _thumbs = Path.Combine(_work, "thumbs");
        _dbPath = Path.Combine(_work, "catalog.db");
        Directory.CreateDirectory(_photos);
    }

    static void WritePng(string path, int w, int h, SKColor c)
    {
        using var bmp = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bmp)) canvas.Clear(c);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }

    Database Scan()
    {
        var db = new Database(_dbPath);
        new Scanner(db, new SkiaImageService(), _thumbs).ScanAsync(_photos).GetAwaiter().GetResult();
        return db;
    }

    ExportEngine Engine(Database db) =>
        new(db, new MacOsLinkService(), new MacOsTrashService());

    static List<long> Ids(Database db, params string[] fileNames)
    {
        var repo = new ImageRepository(db);
        var all = repo.All().ToList();
        return fileNames
            .Select(fn => all.First(i => Path.GetFileName(i.Path) == fn).Id)
            .ToList();
    }

    [Fact]
    public async Task Copy_flat_export_places_files_and_leaves_source_intact()
    {
        WritePng(Path.Combine(_photos, "a.png"), 100, 100, SKColors.Red);
        WritePng(Path.Combine(_photos, "b.png"), 120, 90, SKColors.Green);
        using var db = Scan();

        var req = new ExportRequest
        {
            Destination = _dest, SourceRoot = _photos,
            Mode = TransferMode.Copy, Structure = ExportStructure.Flat,
            KeeperIds = Ids(db, "a.png", "b.png"),
        };
        var result = await Engine(db).RunAsync(req);

        Assert.Equal(2, result.Exported);
        Assert.Equal(0, result.Failed);
        Assert.True(File.Exists(Path.Combine(_dest, "a.png")));
        Assert.True(File.Exists(Path.Combine(_dest, "b.png")));
        // Source untouched by a copy.
        Assert.True(File.Exists(Path.Combine(_photos, "a.png")));
    }

    [Fact]
    public async Task Move_export_verifies_then_removes_source()
    {
        WritePng(Path.Combine(_photos, "m.png"), 100, 100, SKColors.Blue);
        using var db = Scan();
        var req = new ExportRequest
        {
            Destination = _dest, SourceRoot = _photos,
            Mode = TransferMode.Move, Structure = ExportStructure.Flat,
            KeeperIds = Ids(db, "m.png"),
        };
        var result = await Engine(db).RunAsync(req);

        Assert.Equal(1, result.Exported);
        Assert.True(File.Exists(Path.Combine(_dest, "m.png")));
        Assert.False(File.Exists(Path.Combine(_photos, "m.png"))); // source released after verify
    }

    [Fact]
    public async Task Move_export_is_logged_and_reversible()
    {
        // DESIGN §7.4: every destructive operation is logged and every batch is reversible.
        // Move releases the original, so it must appear in the undo log and restore from the
        // destination — it previously deleted the source silently with no trail.
        var source = Path.Combine(_photos, "m.png");
        WritePng(source, 100, 100, SKColors.Blue);
        using var db = Scan();

        var req = new ExportRequest
        {
            Destination = _dest, SourceRoot = _photos,
            Mode = TransferMode.Move, Structure = ExportStructure.Flat,
            KeeperIds = Ids(db, "m.png"),
        };
        await Engine(db).RunAsync(req);
        Assert.False(File.Exists(source));

        var deletes = new SnapZap.Core.Delete.DeleteService(db, new MacOsTrashService());
        var batch = Assert.Single(deletes.Batches(), b => b.BatchId.StartsWith("move-"));
        Assert.Equal(1, batch.Total);

        var restore = await deletes.RestoreAsync(batch.BatchId);

        Assert.Equal(1, restore.Restored);
        Assert.True(File.Exists(source));                              // back where it came from
        Assert.True(File.Exists(Path.Combine(_dest, "m.png")));        // undo is not itself destructive

        // ...and the catalog follows the file back, rather than still describing the copy the
        // user just undid their way out of.
        Assert.Equal(source, Assert.Single(new ImageRepository(db).All()).Path);
    }

    [Fact]
    public async Task Move_export_repoints_the_catalog_at_the_new_location()
    {
        // Move deletes the original after verifying. If the catalog keeps pointing at the old
        // path, every row it holds becomes a lie: thumbnails and previews 404, and a later
        // delete tries to recycle a file that isn't there. The bytes were hash-verified at the
        // destination, so the row is still valid — it just lives somewhere else now.
        var source = Path.Combine(_photos, "m.png");
        WritePng(source, 100, 100, SKColors.Blue);
        using var db = Scan();

        var req = new ExportRequest
        {
            Destination = _dest, SourceRoot = _photos,
            Mode = TransferMode.Move, Structure = ExportStructure.Flat,
            KeeperIds = Ids(db, "m.png"),
        };
        await Engine(db).RunAsync(req);

        var row = Assert.Single(new ImageRepository(db).All());
        Assert.Equal(Path.Combine(_dest, "m.png"), row.Path);
        Assert.True(File.Exists(row.Path));
    }

    [Fact]
    public async Task Different_content_same_name_gets_suffixed_never_overwritten()
    {
        // Two DIFFERENT images that will collide on filename in a flat export.
        WritePng(Path.Combine(_photos, "x", "IMG.png"), 100, 100, SKColors.Red);
        WritePng(Path.Combine(_photos, "y", "IMG.png"), 100, 100, SKColors.Green);
        using var db = Scan();
        var req = new ExportRequest
        {
            Destination = _dest, SourceRoot = _photos,
            Mode = TransferMode.Copy, Structure = ExportStructure.Flat,
            KeeperIds = new ImageRepository(db).All().Select(i => i.Id).ToList(),
        };
        var result = await Engine(db).RunAsync(req);

        Assert.Equal(2, result.Exported);
        Assert.True(File.Exists(Path.Combine(_dest, "IMG.png")));
        Assert.True(File.Exists(Path.Combine(_dest, "IMG (2).png"))); // suffixed, not overwritten
    }

    [Fact]
    public async Task Identical_content_same_name_is_skipped_not_duplicated()
    {
        WritePng(Path.Combine(_photos, "p", "PIC.png"), 100, 100, SKColors.Teal);
        File.Copy(Path.Combine(_photos, "p", "PIC.png"),
                  CreateDir(Path.Combine(_photos, "q", "PIC.png")));
        using var db = Scan();
        var req = new ExportRequest
        {
            Destination = _dest, SourceRoot = _photos,
            Mode = TransferMode.Copy, Structure = ExportStructure.Flat,
            KeeperIds = new ImageRepository(db).All().Select(i => i.Id).ToList(),
        };
        var result = await Engine(db).RunAsync(req);

        // One exported, the identical twin skipped — no "PIC (2).png".
        Assert.Equal(1, result.Exported);
        Assert.Equal(1, result.SkippedExisting);
        Assert.False(File.Exists(Path.Combine(_dest, "PIC (2).png")));
    }

    [Fact]
    public async Task Hardlink_export_creates_zero_copy_links_on_same_volume()
    {
        WritePng(Path.Combine(_photos, "h.png"), 100, 100, SKColors.Orange);
        using var db = Scan();
        var req = new ExportRequest
        {
            Destination = _dest, SourceRoot = _photos,
            Mode = TransferMode.Hardlink, Structure = ExportStructure.Flat,
            KeeperIds = Ids(db, "h.png"),
        };
        var pre = Engine(db).Plan(req);
        Assert.True(pre.HardlinkPossible); // temp dir → same volume

        var result = await Engine(db).RunAsync(req);
        Assert.Equal(1, result.Exported);

        var dest = Path.Combine(_dest, "h.png");
        Assert.True(File.Exists(dest));
        // Editing source is visible through the link (same inode).
        File.WriteAllText(Path.Combine(_photos, "h.png"), "changed");
        Assert.Equal("changed", File.ReadAllText(dest));
    }

    [Fact]
    public async Task Date_structure_routes_by_exif_and_undated_bucket()
    {
        WritePng(Path.Combine(_photos, "d.png"), 100, 100, SKColors.Purple);
        using var db = Scan();
        // Inject an EXIF date directly into the catalog row (no EXIF on generated PNG).
        using (var cmd = db.Connection.CreateCommand())
        {
            var when = new DateTimeOffset(new DateTime(2019, 3, 4, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();
            cmd.CommandText = "UPDATE images SET exif_taken=$t WHERE path LIKE '%d.png'";
            cmd.Parameters.AddWithValue("$t", when);
            cmd.ExecuteNonQuery();
        }
        var req = new ExportRequest
        {
            Destination = _dest, SourceRoot = _photos,
            Mode = TransferMode.Copy, Structure = ExportStructure.Date,
            KeeperIds = Ids(db, "d.png"),
        };
        await Engine(db).RunAsync(req);
        Assert.True(File.Exists(Path.Combine(_dest, "2019", "2019-03", "d.png")));
    }

    [Fact]
    public async Task Reject_recycle_happens_only_after_keepers_verified()
    {
        WritePng(Path.Combine(_photos, "keep.png"), 100, 100, SKColors.Green);
        WritePng(Path.Combine(_photos, "junk.png"), 50, 50, SKColors.Gray);
        using var db = Scan();
        var req = new ExportRequest
        {
            Destination = _dest, SourceRoot = _photos,
            Mode = TransferMode.Copy, Structure = ExportStructure.Flat,
            RejectAction = RejectAction.Recycle,
            KeeperIds = Ids(db, "keep.png"),
            RejectIds = Ids(db, "junk.png"),
        };
        var result = await Engine(db).RunAsync(req);

        Assert.Equal(1, result.Exported);
        Assert.Equal(1, result.Recycled);
        Assert.True(File.Exists(Path.Combine(_dest, "keep.png"))); // keeper safe
        Assert.False(File.Exists(Path.Combine(_photos, "junk.png"))); // reject recycled

        // Undo log recorded the recycle for restore (step 9).
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM undo_log WHERE op='recycle'";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public async Task Rerun_resumes_and_does_not_recopy_verified_items()
    {
        WritePng(Path.Combine(_photos, "r.png"), 100, 100, SKColors.Red);
        using var db = Scan();
        var req = new ExportRequest
        {
            Destination = _dest, SourceRoot = _photos,
            Mode = TransferMode.Copy, Structure = ExportStructure.Flat,
            KeeperIds = Ids(db, "r.png"),
        };
        var first = await Engine(db).RunAsync(req);
        Assert.Equal(1, first.Exported);

        // A brand-new run over the same request: the destination already has the identical
        // file, so it is skipped rather than re-exported (idempotent re-export).
        var second = await Engine(db).RunAsync(req);
        Assert.Equal(0, second.Exported);
        Assert.Equal(1, second.SkippedExisting);
    }

    [Fact]
    public async Task Move_that_fails_verification_leaves_source_intact()
    {
        // THE core safety invariant (§7.2): a destructive step never precedes verification.
        // Corrupt the stored hash so the destination re-hash can never match → Verify throws.
        WritePng(Path.Combine(_photos, "safe.png"), 100, 100, SKColors.Crimson);
        using var db = Scan();
        using (var cmd = db.Connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE images SET content_hash='deadbeef_wrong' WHERE path LIKE '%safe.png'";
            cmd.ExecuteNonQuery();
        }
        var req = new ExportRequest
        {
            Destination = _dest, SourceRoot = _photos,
            Mode = TransferMode.Move, Structure = ExportStructure.Flat,
            KeeperIds = Ids(db, "safe.png"),
        };
        var result = await Engine(db).RunAsync(req);

        Assert.Equal(0, result.Exported);
        Assert.Equal(1, result.Failed);
        // Source MUST still be there — verification failed, so the source was never released.
        Assert.True(File.Exists(Path.Combine(_photos, "safe.png")),
            "source must survive a failed-verification move");
    }

    static string CreateDir(string path) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); return path; }

    public void Dispose()
    {
        try { if (Directory.Exists(_work)) Directory.Delete(_work, true); } catch { }
    }
}
