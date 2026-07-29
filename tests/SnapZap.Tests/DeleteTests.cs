using SnapZap.Core.Data;
using SnapZap.Core.Delete;
using SnapZap.Core.Imaging;
using SnapZap.Core.Platform;
using SnapZap.Core.Scanning;
using SkiaSharp;
using Xunit;

namespace SnapZap.Tests;

public class DeleteTests : IDisposable
{
    readonly string _work = Path.Combine(Path.GetTempPath(), "pc_del_" + Guid.NewGuid().ToString("N"));
    readonly string _photos, _thumbs, _dbPath;

    public DeleteTests()
    {
        _photos = Path.Combine(_work, "photos");
        _thumbs = Path.Combine(_work, "thumbs");
        _dbPath = Path.Combine(_work, "catalog.db");
        Directory.CreateDirectory(_photos);
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

    Database Scan()
    {
        var db = new Database(_dbPath);
        new Scanner(db, new SkiaImageService(), _thumbs).ScanAsync(_photos).GetAwaiter().GetResult();
        return db;
    }

    static List<long> Ids(Database db, params string[] names)
    {
        var all = new ImageRepository(db).All().ToList();
        return names.Select(n => all.First(i => Path.GetFileName(i.Path) == n).Id).ToList();
    }

    [SkippableFact]
    public async Task Recycle_then_restore_round_trips_the_file_and_catalog_note()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "uses the macOS Finder trash dev implementation");

        WritePng(Path.Combine(_photos, "gone.png"), SKColors.Red);
        WritePng(Path.Combine(_photos, "stay.png"), SKColors.Green);
        using var db = Scan();
        var svc = new DeleteService(db, new MacOsTrashService());
        var original = Path.Combine(_photos, "gone.png");

        // Delete → file leaves the folder, row leaves the catalog, undo batch recorded.
        var del = await svc.RecycleAsync(Ids(db, "gone.png"));
        Assert.Equal(1, del.Recycled);
        Assert.False(File.Exists(original), "recycled file should leave the source folder");
        Assert.Single(new ImageRepository(db).All()); // only stay.png remains

        var batches = svc.Batches();
        Assert.Single(batches);
        Assert.Equal(del.BatchId, batches[0].BatchId);
        Assert.Equal(0, batches[0].Restored);

        // Undo → file comes back to its exact original path.
        var restore = await svc.RestoreAsync(del.BatchId);
        Assert.Equal(1, restore.Restored);
        Assert.True(File.Exists(original), "restored file should return to its original path");
        Assert.Equal(1, svc.Batches()[0].Restored);

        // stay.png was never touched.
        Assert.True(File.Exists(Path.Combine(_photos, "stay.png")));
    }

    [SkippableFact]
    public async Task Recycled_batch_items_carry_the_content_hash_for_preview()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "uses the macOS Finder trash dev implementation");

        WritePng(Path.Combine(_photos, "gone.png"), SKColors.Red);
        using var db = Scan();
        var expectedHash = new ImageRepository(db).All().Single().ContentHash;
        var svc = new DeleteService(db, new MacOsTrashService());

        var del = await svc.RecycleAsync(Ids(db, "gone.png"));

        var items = svc.ItemsInBatch(del.BatchId);
        Assert.Single(items);
        Assert.Equal(expectedHash, items[0].ContentHash);
        Assert.False(items[0].Restored);

        await svc.RestoreAsync(del.BatchId);
        Assert.True(svc.ItemsInBatch(del.BatchId).Single().Restored);
    }

    [SkippableFact]
    public async Task Restoring_one_item_leaves_the_rest_of_the_batch_untouched()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "uses the macOS Finder trash dev implementation");

        WritePng(Path.Combine(_photos, "a.png"), SKColors.Red);
        WritePng(Path.Combine(_photos, "b.png"), SKColors.Blue);
        using var db = Scan();
        var svc = new DeleteService(db, new MacOsTrashService());
        var pathA = Path.Combine(_photos, "a.png");
        var pathB = Path.Combine(_photos, "b.png");

        var del = await svc.RecycleAsync(Ids(db, "a.png", "b.png"));
        var items = svc.ItemsInBatch(del.BatchId);
        Assert.Equal(2, items.Count);
        var itemA = items.Single(i => i.OriginalPath == pathA);

        var ok = await svc.RestoreItemAsync(itemA.Id);
        Assert.True(ok);
        Assert.True(File.Exists(pathA), "the single restored item should be back on disk");
        Assert.False(File.Exists(pathB), "the sibling item should stay recycled");

        var batch = svc.Batches().Single(b => b.BatchId == del.BatchId);
        Assert.Equal(1, batch.Restored);
        Assert.Equal(2, batch.Total);

        // Restoring an already-restored (or nonexistent) row is a no-op, not a throw.
        Assert.False(await svc.RestoreItemAsync(itemA.Id));
        Assert.False(await svc.RestoreItemAsync(-1));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_work)) Directory.Delete(_work, true); } catch { }
    }

    // ---- ForgetItem / ForgetBatch: "remove from trash", the second irreversible action ----
    // Portable: these use FolderTrashService rather than the macOS Finder trash, so unlike the
    // cases above they run on every OS.

    [Fact]
    public async Task Forget_deletes_the_trashed_file_and_drops_its_history_entry()
    {
        WritePng(Path.Combine(_photos, "gone.png"), SKColors.Red);
        WritePng(Path.Combine(_photos, "stay.png"), SKColors.Green);
        using var db = Scan();
        var trashRoot = Path.Combine(_work, "trash");
        var svc = new DeleteService(db, new FolderTrashService(trashRoot));

        var del = await svc.RecycleAsync(Ids(db, "gone.png"));
        var item = Assert.Single(svc.ItemsInBatch(del.BatchId, 50));
        var trashed = Directory.GetFiles(trashRoot).Single();

        Assert.True(svc.ForgetItem(item.Id));

        Assert.False(File.Exists(trashed), "the trashed file must actually be reclaimed");
        Assert.Empty(svc.ItemsInBatch(del.BatchId, 50));
        Assert.Empty(svc.Batches());
    }

    /// <summary>
    /// The whole reason removal purges rather than only forgetting: undo_log.new_location is the
    /// only pointer to a trashed file, so dropping the row alone would strand the bytes forever —
    /// consuming storage, unrestorable, and invisible to every screen in the app.
    /// </summary>
    [Fact]
    public async Task Forget_leaves_nothing_stranded_in_the_trash()
    {
        WritePng(Path.Combine(_photos, "a.png"), SKColors.Red);
        WritePng(Path.Combine(_photos, "b.png"), SKColors.Green);
        using var db = Scan();
        var trashRoot = Path.Combine(_work, "trash");
        var svc = new DeleteService(db, new FolderTrashService(trashRoot));

        var del = await svc.RecycleAsync(Ids(db, "a.png", "b.png"));
        Assert.Equal(2, Directory.GetFiles(trashRoot).Length);

        Assert.Equal(2, svc.ForgetBatch(del.BatchId));

        Assert.Empty(Directory.GetFiles(trashRoot));
        Assert.Empty(svc.Batches());
    }

    /// <summary>
    /// A restored row points at the user's own file, back in their library. Removing that history
    /// entry must not delete it — "remove this entry" is not "delete the photo I already got back".
    /// </summary>
    [Fact]
    public async Task Forgetting_an_already_restored_entry_drops_the_record_but_keeps_the_photo()
    {
        var original = Path.Combine(_photos, "gone.png");
        WritePng(original, SKColors.Red);
        WritePng(Path.Combine(_photos, "stay.png"), SKColors.Green);
        using var db = Scan();
        var svc = new DeleteService(db, new FolderTrashService(Path.Combine(_work, "trash")));

        var del = await svc.RecycleAsync(Ids(db, "gone.png"));
        Assert.Equal(1, (await svc.RestoreAsync(del.BatchId)).Restored);
        Assert.True(File.Exists(original), "precondition: restored back to the library");

        var item = Assert.Single(svc.ItemsInBatch(del.BatchId, 50));
        Assert.True(svc.ForgetItem(item.Id));

        Assert.True(File.Exists(original), "the restored photo must survive forgetting its record");
        Assert.Empty(svc.Batches());
    }

    [Fact]
    public void Forgetting_something_that_does_not_exist_is_false_not_an_error()
    {
        WritePng(Path.Combine(_photos, "a.png"), SKColors.Red);
        WritePng(Path.Combine(_photos, "b.png"), SKColors.Green);
        using var db = Scan();
        var svc = new DeleteService(db, new FolderTrashService(Path.Combine(_work, "trash")));

        Assert.False(svc.ForgetItem(4242));
        Assert.Equal(0, svc.ForgetBatch("no-such-batch"));
    }
}
