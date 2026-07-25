using SnapZap.Core.Data;
using SnapZap.Core.Imaging;
using SnapZap.Core.Scanning;
using SkiaSharp;
using Xunit;

namespace SnapZap.Tests;

public class ScannerTests : IDisposable
{
    readonly string _work = Path.Combine(Path.GetTempPath(), "pc_scan_" + Guid.NewGuid().ToString("N"));
    readonly string _photos;
    readonly string _thumbs;
    readonly string _dbPath;

    public ScannerTests()
    {
        _photos = Path.Combine(_work, "photos");
        _thumbs = Path.Combine(_work, "thumbs");
        _dbPath = Path.Combine(_work, "catalog.db");
        Directory.CreateDirectory(_photos);
    }

    static void WritePng(string path, int w, int h, SKColor color)
    {
        using var bmp = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bmp)) canvas.Clear(color);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }

    [Fact]
    public async Task Scans_images_generates_thumbnails_and_caches_on_rescan()
    {
        // 5 images across a nested folder; one non-image decoy that must be ignored.
        WritePng(Path.Combine(_photos, "a.png"), 200, 120, SKColors.Red);
        WritePng(Path.Combine(_photos, "b.png"), 640, 480, SKColors.Green);
        WritePng(Path.Combine(_photos, "sub", "c.png"), 100, 100, SKColors.Blue);
        WritePng(Path.Combine(_photos, "sub", "d.jpg"), 50, 50, SKColors.Yellow);
        WritePng(Path.Combine(_photos, "sub", "e.png"), 300, 300, SKColors.Purple);
        File.WriteAllText(Path.Combine(_photos, "notes.txt"), "not an image");

        using var db = new Database(_dbPath);
        var imaging = new SkiaImageService();
        var scanner = new Scanner(db, imaging, _thumbs);

        var first = await scanner.ScanAsync(_photos);
        Assert.Equal(5, first.Total);       // decoy .txt not enumerated
        Assert.Equal(5, first.Analyzed);
        Assert.Equal(0, first.Cached);

        // Every analyzed image has a thumbnail on disk and dimensions in the DB.
        var repo = new ImageRepository(db);
        var rows = repo.All().ToList();
        Assert.Equal(5, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.True(File.Exists(r.ThumbPath), $"thumb missing for {r.Path}");
            Assert.True(r.Width > 0 && r.Height > 0);
            Assert.False(string.IsNullOrEmpty(r.ContentHash));
        });

        // Rescan with nothing changed → all cache hits, zero re-analysis.
        var second = await scanner.ScanAsync(_photos);
        Assert.Equal(5, second.Cached);
        Assert.Equal(0, second.Analyzed);

        // Touch one file's content → exactly one re-analysis, four cached.
        await Task.Delay(1100); // ensure mtime (unix-seconds) advances
        WritePng(Path.Combine(_photos, "a.png"), 201, 121, SKColors.Orange);
        var third = await scanner.ScanAsync(_photos);
        Assert.Equal(1, third.Analyzed);
        Assert.Equal(4, third.Cached);

        // Delete a file → pruned from the catalog on next scan.
        File.Delete(Path.Combine(_photos, "b.png"));
        var fourth = await scanner.ScanAsync(_photos);
        Assert.Equal(1, fourth.Pruned);
        Assert.Equal(4, repo.Count());
    }

    [Fact]
    public async Task Exact_duplicates_do_not_fail_on_concurrent_thumbnail_write()
    {
        // Regression: identical files share a content hash → same thumbnail path. Concurrent
        // writers must not collide. Many copies of one image maximizes the race window.
        WritePng(Path.Combine(_photos, "orig.png"), 512, 512, SKColors.Teal);
        for (int i = 0; i < 20; i++)
            File.Copy(Path.Combine(_photos, "orig.png"), Path.Combine(_photos, $"dup{i}.png"));

        using var db = new Database(_dbPath);
        var scanner = new Scanner(db, new SkiaImageService(), _thumbs);
        var result = await scanner.ScanAsync(_photos);

        Assert.Equal(21, result.Total);
        Assert.Equal(0, result.Failed);          // no thumbnail-write collisions
        Assert.Equal(21, result.Analyzed);

        // All 21 rows share one content hash and one existing thumbnail.
        var rows = new ImageRepository(db).All().ToList();
        Assert.Single(rows.Select(r => r.ContentHash).Distinct());
        Assert.All(rows, r => Assert.True(File.Exists(r.ThumbPath)));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_work)) Directory.Delete(_work, true); } catch { }
    }
}
