using SnapZap.Core;
using SnapZap.Core.Data;
using SnapZap.Core.Dedup;
using SnapZap.Core.Imaging;
using SnapZap.Core.Scanning;
using SkiaSharp;
using Xunit;

namespace SnapZap.Tests;

public class ExactDedupTests : IDisposable
{
    readonly string _work = Path.Combine(Path.GetTempPath(), "pc_dedup_" + Guid.NewGuid().ToString("N"));
    readonly string _photos, _thumbs, _dbPath;

    public ExactDedupTests()
    {
        _photos = Path.Combine(_work, "photos");
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

    /// <summary>A smaller copy of an existing image: same picture, different bytes and pixels.</summary>
    static void Downscale(string sourcePath, string destPath, int w, int h)
    {
        using var src = SKBitmap.Decode(sourcePath);
        using var small = src.Resize(new SKImageInfo(w, h), SKSamplingOptions.Default)!;
        using var img = SKImage.FromBitmap(small);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.Create(destPath);
        data.SaveTo(fs);
    }

    [Fact]
    public async Task Exact_finder_groups_identical_files_and_picks_highest_res_keeper()
    {
        // Group 1: three byte-identical copies of a small image.
        WritePng(Path.Combine(_photos, "small.png"), 100, 100, SKColors.Red);
        Directory.CreateDirectory(Path.Combine(_photos, "sub"));
        File.Copy(Path.Combine(_photos, "small.png"), Path.Combine(_photos, "small_copy1.png"));
        File.Copy(Path.Combine(_photos, "small.png"), Path.Combine(_photos, "sub", "small_copy2.png"));
        // A distinct, unique image (must NOT be grouped).
        WritePng(Path.Combine(_photos, "unique.png"), 800, 600, SKColors.Blue);

        using var db = new Database(_dbPath);
        await new Scanner(db, new SkiaImageService(), _thumbs).ScanAsync(_photos);

        var groups = new ExactDuplicateFinder(db).FindAndStore();
        Assert.Equal(1, groups); // exactly one duplicate group

        var stored = new DupeRepository(db).Groups(DupeKind.Exact);
        Assert.Single(stored);
        Assert.Equal(3, stored[0].Members.Count);
        Assert.Equal(1, stored[0].Members.Count(m => m.IsKeeper)); // exactly one keeper
    }

    /// <summary>
    /// Exact detection has no toggle by design, so turning the visual detectors off must still
    /// find identical files — and must say out loud that only part of the job ran. "0 groups" and
    /// "0 groups, and we only compared checksums" have to look different to the user.
    /// </summary>
    [Fact]
    public async Task With_visual_detectors_off_exact_still_runs_and_the_note_says_so()
    {
        WritePng(Path.Combine(_photos, "a.png"), 100, 100, SKColors.Red);
        File.Copy(Path.Combine(_photos, "a.png"), Path.Combine(_photos, "b.png"));

        using var db = new Database(_dbPath);
        await new Scanner(db, new SkiaImageService(), _thumbs).ScanAsync(_photos);
        new DedupSettings { VariantEnabled = false, BurstEnabled = false }.Save(db);

        var report = await new DuplicateService(db).DetectAsync(_photos);

        Assert.Equal(1, report.ExactGroups);
        Assert.Equal(0, report.VariantGroups);
        Assert.Equal(0, report.BurstGroups);
        Assert.Contains("visual matching is turned off", report.Note);
    }

    /// <summary>
    /// The pass czkawka used to run, now in-process: a downscaled copy is not byte-identical, so
    /// only the variant detector can pair it with its original.
    /// </summary>
    [Fact]
    public async Task Variant_detection_pairs_a_downscaled_copy_and_keeps_the_larger()
    {
        var original = Path.Combine(_photos, "big.png");
        WritePng(original, 480, 360, SKColors.Teal);
        Downscale(original, Path.Combine(_photos, "small.png"), 160, 120);

        using var db = new Database(_dbPath);
        await new Scanner(db, new SkiaImageService(), _thumbs).ScanAsync(_photos);
        new DedupSettings { VariantEnabled = true, BurstEnabled = false }.Save(db);

        var report = await new DuplicateService(db).DetectAsync(_photos);

        Assert.Equal(1, report.VariantGroups);
        var group = Assert.Single(new DupeRepository(db).Groups(DupeKind.Variant));
        Assert.Equal(2, group.Members.Count);

        // Keeper is the higher-resolution copy, never the shrunken one.
        var keeperId = group.Members.Single(m => m.IsKeeper).ImageId;
        var keeper = new ImageRepository(db).Under(_photos).Single(i => i.Id == keeperId);
        Assert.EndsWith("big.png", keeper.Path);
    }

    /// <summary>
    /// A disabled detector has to clear its own groups. Leaving them on screen after switching it
    /// off shows results nothing is maintaining, and nothing tells the user they are stale.
    /// </summary>
    [Fact]
    public async Task Disabling_a_detector_clears_the_groups_it_had_written()
    {
        var original = Path.Combine(_photos, "big.png");
        WritePng(original, 480, 360, SKColors.Teal);
        Downscale(original, Path.Combine(_photos, "small.png"), 160, 120);

        using var db = new Database(_dbPath);
        await new Scanner(db, new SkiaImageService(), _thumbs).ScanAsync(_photos);

        new DedupSettings { VariantEnabled = true }.Save(db);
        await new DuplicateService(db).DetectAsync(_photos);
        Assert.Single(new DupeRepository(db).Groups(DupeKind.Variant));

        new DedupSettings { VariantEnabled = false }.Save(db);
        await new DuplicateService(db).DetectAsync(_photos);
        Assert.Empty(new DupeRepository(db).Groups(DupeKind.Variant));
    }

    /// <summary>
    /// The coverage mask, not just the timestamp. Enabling a detector after a run has to leave the
    /// folder reading as pending, or the tree's dot is decorative.
    /// </summary>
    [Fact]
    public async Task Coverage_records_which_detectors_actually_ran()
    {
        WritePng(Path.Combine(_photos, "a.png"), 100, 100, SKColors.Red);

        using var db = new Database(_dbPath);
        await new Scanner(db, new SkiaImageService(), _thumbs).ScanAsync(_photos);

        new DedupSettings { VariantEnabled = false, BurstEnabled = false }.Save(db);
        await new DuplicateService(db).DetectAsync(_photos);

        var row = new ImageRepository(db).Under(_photos).Single();
        Assert.Equal(DupeKinds.Exact, row.DupeCheckedKinds);

        // With variant detection switched on, the stored mask no longer covers what is enabled,
        // which is what makes the folder tree show the work as outstanding again.
        var richer = new DedupSettings { VariantEnabled = true, BurstEnabled = false };
        Assert.NotEqual(richer.CoveredKinds, row.DupeCheckedKinds & richer.CoveredKinds);
    }

    /// <summary>
    /// The folder tree's whole ability to say "checked, and clean" rests on this stamp existing
    /// after a run and being absent before one. Without it a folder with no duplicates and a
    /// folder nobody has looked at are indistinguishable.
    /// </summary>
    [Fact]
    public async Task Completed_detection_marks_the_scanned_rows_as_checked()
    {
        WritePng(Path.Combine(_photos, "a.png"), 100, 100, SKColors.Red);
        WritePng(Path.Combine(_photos, "b.png"), 120, 120, SKColors.Blue);

        using var db = new Database(_dbPath);
        await new Scanner(db, new SkiaImageService(), _thumbs).ScanAsync(_photos);

        var repo = new ImageRepository(db);
        Assert.All(repo.Under(_photos), i => Assert.Null(i.DupeCheckedAt));

        await new DuplicateService(db).DetectAsync(_photos);

        Assert.All(repo.Under(_photos), i => Assert.NotNull(i.DupeCheckedAt));
    }

    /// <summary>
    /// Re-analysing a file whose bytes moved invalidates the verdict computed against the old
    /// ones. Carrying the stamp across an Upsert would report the folder as checked while the
    /// photos that actually changed silently were not.
    /// </summary>
    [Fact]
    public async Task Rescanning_a_changed_file_clears_its_checked_stamp()
    {
        var target = Path.Combine(_photos, "a.png");
        WritePng(target, 100, 100, SKColors.Red);

        using var db = new Database(_dbPath);
        var scanner = new Scanner(db, new SkiaImageService(), _thumbs);
        await scanner.ScanAsync(_photos);
        await new DuplicateService(db).DetectAsync(_photos);

        var repo = new ImageRepository(db);
        Assert.NotNull(repo.Under(_photos).Single().DupeCheckedAt);

        // Different content and a different size, so the tier-1 probe misses and the row is
        // re-analysed rather than reused from cache.
        WritePng(target, 640, 480, SKColors.Green);
        File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddMinutes(1));
        await scanner.ScanAsync(_photos);

        Assert.Null(repo.Under(_photos).Single().DupeCheckedAt);
    }

    /// <summary>
    /// PathScope moved from a substr() equality to a half-open range so the predicate could use
    /// the index on images.path. The rewrite has to select exactly the same rows — including for
    /// a sibling folder whose name extends the scoped one ("pics" vs "pics_old"), which is where
    /// a careless prefix comparison leaks.
    /// </summary>
    [Fact]
    public async Task Scoped_queries_exclude_sibling_folders_with_the_scope_as_a_name_prefix()
    {
        var inside = Path.Combine(_photos, "pics");
        var sibling = Path.Combine(_photos, "pics_old");
        WritePng(Path.Combine(inside, "a.png"), 100, 100, SKColors.Red);
        WritePng(Path.Combine(sibling, "b.png"), 100, 100, SKColors.Blue);

        using var db = new Database(_dbPath);
        await new Scanner(db, new SkiaImageService(), _thumbs).ScanAsync(_photos);

        var repo = new ImageRepository(db);
        Assert.Equal(2, repo.Under(_photos).Count());

        var scoped = repo.Under(inside).ToList();
        Assert.Single(scoped);
        Assert.EndsWith("a.png", scoped[0].Path);

        // And the write path scopes identically to the read path.
        Assert.Equal(1, repo.MarkDupeChecked(inside, DupeKinds.Exact));
        Assert.All(repo.Under(sibling), i => Assert.Null(i.DupeCheckedAt));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_work)) Directory.Delete(_work, true); } catch { }
    }
}
