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
    public async Task Progress_reports_carry_the_real_total_not_zero()
    {
        // AppState.RunAsync's EtaEstimator treats total<=0 as "unknown" and never shows an ETA —
        // ScanProgress used to hardcode 0 regardless of how many files were actually found, so
        // Scan alone, of every long operation, could never show "X / Y · ~Ns left".
        WritePng(Path.Combine(_photos, "a.png"), 200, 120, SKColors.Red);
        WritePng(Path.Combine(_photos, "b.png"), 640, 480, SKColors.Green);
        WritePng(Path.Combine(_photos, "c.png"), 100, 100, SKColors.Blue);

        using var db = new Database(_dbPath);
        var scanner = new Scanner(db, new SkiaImageService(), _thumbs);

        // A hand-rolled IProgress<T> rather than System.Progress<T>: with no ambient
        // SynchronizationContext (the default on an xunit thread), Progress<T> posts callbacks
        // to the thread pool asynchronously, so reports could still be in flight when ScanAsync
        // returns. SyncProgress reports inline, so what's collected here is exactly what fired.
        var progress = new SyncProgress<ScanProgress>();

        await scanner.ScanAsync(_photos, progress);

        Assert.NotEmpty(progress.Values);
        Assert.All(progress.Values, r => Assert.Equal(3, r.Total));
    }

    [Fact]
    public async Task Scan_observes_cancellation_before_doing_any_analysis_work()
    {
        // Regression guard for the actual fix this session was about: the folder walk used to
        // run fully synchronously with no CancellationToken at all, so Stop could not touch it —
        // not "was slow to respond", but had literally no way to observe cancellation. This
        // doesn't need a huge directory or timing games: a token that's already cancelled before
        // ScanAsync is even called must still short-circuit cleanly, with nothing analyzed and
        // nothing written — which only holds if both the Task.Run wrapper and Enumerate's own
        // ct.ThrowIfCancellationRequested() are still wired through. Drop either one (e.g.
        // "simplify" Enumerate back to a plain synchronous call with no token) and this starts
        // scanning for real instead of throwing.
        WritePng(Path.Combine(_photos, "a.png"), 200, 120, SKColors.Red);
        WritePng(Path.Combine(_photos, "b.png"), 640, 480, SKColors.Green);

        using var db = new Database(_dbPath);
        var scanner = new Scanner(db, new SkiaImageService(), _thumbs);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scanner.ScanAsync(_photos, ct: cts.Token));

        Assert.Empty(new ImageRepository(db).All());
    }

    [Fact]
    public async Task Scanning_a_second_folder_leaves_the_first_folders_catalog_intact()
    {
        // Pruning removes rows whose file is gone. It must not treat "not in the folder I just
        // scanned" as "gone": scanning a second library used to delete the first one's rows
        // outright, losing every hash, score and keeper decision for photos still on disk.
        var other = Path.Combine(_work, "other");
        WritePng(Path.Combine(_photos, "a.png"), 200, 120, SKColors.Red);
        WritePng(Path.Combine(_photos, "b.png"), 640, 480, SKColors.Green);
        WritePng(Path.Combine(other, "c.png"), 320, 240, SKColors.Blue);

        using var db = new Database(_dbPath);
        var scanner = new Scanner(db, new SkiaImageService(), _thumbs);
        var repo = new ImageRepository(db);

        await scanner.ScanAsync(_photos);
        Assert.Equal(2, repo.Count());

        var second = await scanner.ScanAsync(other);
        Assert.Equal(0, second.Pruned);
        Assert.Equal(3, repo.Count());

        // Pruning still works within the folder actually scanned.
        File.Delete(Path.Combine(_photos, "b.png"));
        var third = await scanner.ScanAsync(_photos);
        Assert.Equal(1, third.Pruned);
        Assert.Equal(2, repo.Count());

        // ...and a row outside the scanned folder is dropped when its file is genuinely gone,
        // rather than lingering forever as a broken thumbnail just because the scan didn't
        // walk that folder. The rule is "the file is missing", not "it wasn't in this scan".
        File.Delete(Path.Combine(other, "c.png"));
        var fourth = await scanner.ScanAsync(_photos);
        Assert.Equal(1, fourth.Pruned);
        Assert.Equal(1, repo.Count());
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

/// <summary>Reports inline instead of via a captured (or thread-pool-fallback)
/// SynchronizationContext, so a test can assert against exactly what fired without a race
/// against System.Progress&lt;T&gt;'s async delivery.</summary>
sealed class SyncProgress<T> : IProgress<T>
{
    public List<T> Values { get; } = [];
    public void Report(T value) => Values.Add(value);
}
