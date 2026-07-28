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
}
