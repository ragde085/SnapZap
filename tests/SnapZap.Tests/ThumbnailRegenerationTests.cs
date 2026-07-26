using SkiaSharp;
using SnapZap.App.Services;
using SnapZap.Core;
using SnapZap.Core.Data;
using SnapZap.Core.Dedup;
using SnapZap.Core.Imaging;
using SnapZap.Core.Scanning;
using Xunit;

namespace SnapZap.Tests;

/// <summary>
/// Task 16 / AC 13: a thumbnail cached before Phase D's orientation fix must be regenerated, and
/// the regeneration must be visible to the browser despite Task 2's one-year immutable cache
/// header. Covers both halves — the on-disk pixels changing, and the served URL changing.
/// </summary>
public class ThumbnailRegenerationTests : IDisposable
{
    readonly string _work = Path.Combine(Path.GetTempPath(), "pc_thumbregen_" + Guid.NewGuid().ToString("N"));
    readonly string _photos, _thumbs, _dbPath;

    public ThumbnailRegenerationTests()
    {
        _photos = Path.Combine(_work, "photos");
        _thumbs = Path.Combine(_work, "thumbs");
        _dbPath = Path.Combine(_work, "catalog.db");
        Directory.CreateDirectory(_photos);
    }

    static SKBitmap Scene(int w, int h)
    {
        var bmp = new SKBitmap(w, h);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(new SKColor(30, 30, 40));
        using var paint = new SKPaint { IsAntialias = true };
        paint.Color = new SKColor(220, 200, 120);
        canvas.DrawRect(new SKRect(w * 0.05f, h * 0.05f, w * 0.45f, h * 0.30f), paint);
        return bmp;
    }

    static byte[] EncodeJpeg(SKBitmap bmp)
    {
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 100);
        return data.ToArray();
    }

    /// <summary>Rotate 90 CW, independent of SkiaImageService's own orientation code — this
    /// simulates "what the sensor stored" for EXIF Orientation=6, exactly as
    /// <c>OrientationTests.Rotate90Cw</c> does.</summary>
    static SKBitmap Rotate90Cw(SKBitmap src)
    {
        var dst = new SKBitmap(src.Height, src.Width);
        using var c = new SKCanvas(dst);
        c.Translate(src.Height, 0);
        c.RotateDegrees(90);
        c.DrawBitmap(src, 0, 0);
        return dst;
    }

    static byte[] InjectOrientation6(byte[] jpeg)
    {
        byte[] tiff =
        [
            0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00,
            0x01, 0x00,
            0x12, 0x01,
            0x03, 0x00,
            0x01, 0x00, 0x00, 0x00,
            0x06, 0x00, 0x00, 0x00, // Orientation = 6 (RightTop / rotate 90 CW to display)
            0x00, 0x00, 0x00, 0x00,
        ];
        byte[] exifHeader = "Exif\0\0"u8.ToArray();
        int segLen = 2 + exifHeader.Length + tiff.Length;
        var app1 = new byte[2 + segLen];
        app1[0] = 0xFF; app1[1] = 0xE1;
        app1[2] = (byte)(segLen >> 8); app1[3] = (byte)segLen;
        Buffer.BlockCopy(exifHeader, 0, app1, 4, exifHeader.Length);
        Buffer.BlockCopy(tiff, 0, app1, 4 + exifHeader.Length, tiff.Length);

        var result = new byte[2 + app1.Length + (jpeg.Length - 2)];
        result[0] = 0xFF; result[1] = 0xD8;
        Buffer.BlockCopy(app1, 0, result, 2, app1.Length);
        Buffer.BlockCopy(jpeg, 2, result, 2 + app1.Length, jpeg.Length - 2);
        return result;
    }

    [Fact]
    public async Task Stale_sideways_thumbnail_is_overwritten_by_the_backfill_pass()
    {
        // A 300x200 upright scene, stored as the camera would for EXIF Orientation=6: rotated 90
        // CW so displaying it requires undoing that rotation.
        using var upright = Scene(300, 200);
        using var stored = Rotate90Cw(upright);
        var photoPath = Path.Combine(_photos, "portrait.jpg");
        File.WriteAllBytes(photoPath, InjectOrientation6(EncodeJpeg(stored)));

        var imaging = new SkiaImageService();
        using var db = new Database(_dbPath);
        await new Scanner(db, imaging, _thumbs).ScanAsync(_photos);

        var repo = new ImageRepository(db);
        var row = Assert.Single(repo.All());
        // Task 14 already normalises orientation at scan time, so the row and its thumbnail are
        // already correct (300x200-ish, not the stored 200x300) — confirming the fixture and the
        // scan path agree before simulating the "written before Phase D" state below.
        Assert.True(row.Width > row.Height, "scan-time orientation should already be upright");

        var thumbPath = Path.Combine(_thumbs, row.ContentHash[..2], row.ContentHash + ".jpg");
        Assert.True(File.Exists(thumbPath));

        // Simulate a thumbnail cached BEFORE Phase D landed: overwrite it with the sideways
        // (as-stored) pixels. Content-addressed by hash, so nothing about a normal re-scan would
        // ever touch this file again on its own.
        File.WriteAllBytes(thumbPath, EncodeJpeg(stored));
        using (var staleCheck = SKBitmap.Decode(thumbPath))
            Assert.True(staleCheck!.Width < staleCheck.Height, "fixture setup: thumbnail should start sideways");

        // A URL taken before the backfill (mtime-keyed — see ThumbUrl_changes_when_the_thumbnail_file_is_rewritten
        // below for the isolated version of this assertion).
        var urlBefore = new ImageView(row, null, ThumbGenerationFor(row)).ThumbUrl;

        // Force a recipe bump so the row needs backfilling, then run detection with thumbDir wired
        // — the same call AppState.DedupAsync makes.
        db.EnsurePhashRecipeForTest(PerceptualHash.PhashRecipeVersion + 1);
        await new DuplicateService(db, imaging, _thumbs).DetectAsync(_photos);

        using var regenerated = SKBitmap.Decode(thumbPath);
        Assert.True(regenerated!.Width > regenerated.Height,
            "thumbnail should be regenerated upright after the backfill pass");

        // The URL itself must change — a static, compile-time recipe-version constant would leave
        // a browser that fetched urlBefore earlier this same session stuck behind the one-year
        // immutable cache header, since the app version (and the constant) never changed.
        var urlAfter = new ImageView(row, null, ThumbGenerationFor(row)).ThumbUrl;
        Assert.NotEqual(urlBefore, urlAfter);
    }

    static long ThumbGenerationFor(ImageRecord r) => new FileInfo(r.ThumbPath!).LastWriteTimeUtc.Ticks;

    [Fact]
    public void ThumbUrl_changes_when_the_thumbnail_file_is_rewritten()
    {
        // Task 16 (in-session cache-busting): the query param must be keyed on something that
        // changes exactly when the thumbnail FILE is rewritten, not a single compile-time constant
        // shared by the whole app version — the lazy backfill (DuplicateService) can regenerate a
        // thumbnail mid-session, at the same app version, the moment "Find duplicates" next runs.
        var record = new ImageRecord { Path = "/x/y.jpg", ContentHash = "abc123" };
        var urlAtGeneration1 = new ImageView(record, null, ThumbGeneration: 1).ThumbUrl;
        var urlAtGeneration2 = new ImageView(record, null, ThumbGeneration: 2).ThumbUrl;

        Assert.Equal("/api/thumb/abc123?v=1", urlAtGeneration1);
        Assert.NotEqual(urlAtGeneration1, urlAtGeneration2);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_work)) Directory.Delete(_work, true); } catch { }
    }
}
