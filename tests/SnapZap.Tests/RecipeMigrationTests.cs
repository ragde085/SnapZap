using SkiaSharp;
using SnapZap.App;
using SnapZap.Core;
using SnapZap.Core.Data;
using SnapZap.Core.Dedup;
using SnapZap.Core.Imaging;
using SnapZap.Core.Scanning;
using Xunit;

namespace SnapZap.Tests;

/// <summary>
/// Task 13's eager-invalidate / lazy-backfill split (AC 6a, 6b, 6c) — the pre-mortem's #1 and #3
/// highest-risk items. <see cref="Database.EnsurePhashRecipeForTest"/> drives the same production
/// code path <see cref="Database"/>'s constructor uses, parameterised by an arbitrary target
/// version, so a sequence of recipe bumps can be exercised without redefining
/// <see cref="PerceptualHash.PhashRecipeVersion"/> itself.
/// </summary>
public class RecipeMigrationTests : IDisposable
{
    readonly string _work = Path.Combine(Path.GetTempPath(), "pc_recipe_" + Guid.NewGuid().ToString("N"));
    readonly string _photos, _thumbs, _dbPath;

    public RecipeMigrationTests()
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

    async Task<Database> ScanFixture(int count)
    {
        for (int i = 0; i < count; i++)
            WritePng(Path.Combine(_photos, $"p{i}.png"), 200 + i, 150 + i, new SKColor((byte)(i * 20), 100, 200));

        var db = new Database(_dbPath);
        await new Scanner(db, new SkiaImageService(), _thumbs).ScanAsync(_photos);
        return db;
    }

    [Fact]
    public async Task Recipe_bump_invalidates_catalogue_wide_exactly_once_and_backs_up_first()
    {
        using var db = await ScanFixture(5);
        var repo = new ImageRepository(db);

        // Real scan output: every row has a signature and (after detection) a checked stamp.
        await new DuplicateService(db).DetectAsync(_photos);
        Assert.All(repo.All(), r => Assert.NotNull(r.Phash));
        Assert.All(repo.All(), r => Assert.NotNull(r.DupeCheckedAt));

        var targetVersion = PerceptualHash.PhashRecipeVersion + 1;
        var migration = db.EnsurePhashRecipeForTest(targetVersion);

        Assert.True(migration.Migrated);
        Assert.Equal(5, migration.RowsInvalidated);
        Assert.NotNull(migration.BackupPath);
        Assert.True(File.Exists(migration.BackupPath), "backup must exist at the reported path");

        // phash and dupe-checked state cleared catalogue-wide — AC 6a.
        var rows = repo.All().ToList();
        Assert.All(rows, r => Assert.Null(r.Phash));
        Assert.All(rows, r => Assert.Null(r.DupeCheckedAt));
        Assert.All(rows, r => Assert.Equal(DupeKinds.None, r.DupeCheckedKinds));

        // A second "open" at the same target version performs no migration work.
        var again = db.EnsurePhashRecipeForTest(targetVersion);
        Assert.False(again.Migrated);
        Assert.Equal(0, again.RowsInvalidated);
        Assert.Null(again.BackupPath);
    }

    [Fact]
    public async Task Fresh_empty_catalogue_takes_no_backup_on_its_first_recipe_write()
    {
        // An empty catalogue has nothing to protect — EnsurePhashRecipe already ran once for the
        // real PerceptualHash.PhashRecipeVersion in the Database constructor; bumping past that
        // now (with zero rows) must not manufacture a backup file for data that never existed.
        using var db = new Database(_dbPath);
        var migration = db.EnsurePhashRecipeForTest(PerceptualHash.PhashRecipeVersion + 1);

        Assert.True(migration.Migrated);
        Assert.Equal(0, migration.RowsInvalidated);
        Assert.Null(migration.BackupPath);
    }

    [Fact]
    public async Task Two_bumps_without_detection_between_them_are_backfilled_in_one_pass()
    {
        using var db = await ScanFixture(6);
        var repo = new ImageRepository(db);
        Assert.All(repo.All(), r => Assert.NotNull(r.Phash)); // scan itself computed signatures

        // Phase C's bump, then Phase D's — no detection run in between, exactly the scenario a
        // user upgrading through both releases without clicking "Find duplicates" hits.
        db.EnsurePhashRecipeForTest(PerceptualHash.PhashRecipeVersion + 1);
        db.EnsurePhashRecipeForTest(PerceptualHash.PhashRecipeVersion + 2);

        // Neither bump itself decoded anything — invalidation is pure SQL (AC 6a) — so every row
        // is still queued.
        Assert.Equal(6, repo.NeedingPhash(_photos).Count);

        // One detection run backfills everything queued by BOTH bumps in a single pass — the
        // property the eager-invalidate/lazy-backfill split exists to buy (AC 6b). If the
        // architecture instead backfilled per-bump, this single call could not clear a
        // two-bumps' worth of queue on its own.
        await new DuplicateService(db).DetectAsync(_photos);
        Assert.Empty(repo.NeedingPhash(_photos));
        Assert.All(repo.All(), r => Assert.NotNull(r.Phash));
    }

    [Fact]
    public async Task Cancelled_backfill_leaves_the_catalogue_consistent_and_a_later_run_resumes()
    {
        using var db = await ScanFixture(15);
        var repo = new ImageRepository(db);
        db.EnsurePhashRecipeForTest(PerceptualHash.PhashRecipeVersion + 1);
        Assert.Equal(15, repo.NeedingPhash(_photos).Count);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(1));
        try
        {
            await new DuplicateService(db).DetectAsync(_photos, ct: cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected on the common path; if the backfill finished before the token fired, that
            // is also fine — the consistency and resume assertions below hold either way.
        }

        // Consistent, whatever fraction completed: a row's phash is either a full signature or
        // still absent — never a partial/corrupt blob (AC 6c).
        Assert.All(repo.All(), r => Assert.True(r.Phash is null || r.Phash.Length == PerceptualHash.ByteLength));

        // A subsequent, uncancelled run resumes from wherever it left off rather than restarting —
        // NeedingPhash already gives this for free by selecting exactly the rows still NULL.
        await new DuplicateService(db).DetectAsync(_photos);
        Assert.Empty(repo.NeedingPhash(_photos));
    }

    [Fact]
    public void Migration_notice_is_surfaced_exactly_once()
    {
        // AC 6a requires the backup path be "reported to the user" — CatalogService is the seam
        // that turns Database.RecipeMigration into something AppState can show as a toast.
        var appData = Path.Combine(_work, "appdata_notice");
        Directory.CreateDirectory(appData);
        var dbPath = Path.Combine(appData, "catalog.db");

        // Seed a catalogue that looks like it was written under an older recipe.
        using (var seedDb = new Database(dbPath))
        {
            new ImageRepository(seedDb).Upsert(new ImageRecord
            { Path = "/a.jpg", ContentHash = "h1", FileSize = 1, Mtime = 1 });
            seedDb.EnsurePhashRecipeForTest(PerceptualHash.PhashRecipeVersion - 1);
        }

        // Reopening via CatalogService re-runs EnsurePhashRecipe against the real, current
        // version, which now differs from what was just stored — a real migration, not a test hook.
        using var catalog = new CatalogService(appData);

        var first = catalog.TakePendingRecipeMigrationNotice();
        Assert.NotNull(first);
        Assert.Contains("backup", first, StringComparison.OrdinalIgnoreCase);

        Assert.Null(catalog.TakePendingRecipeMigrationNotice());
    }

    [Fact]
    public void No_migration_notice_when_the_catalogue_is_already_current()
    {
        var appData = Path.Combine(_work, "appdata_no_notice");
        using var catalog = new CatalogService(appData); // fresh catalogue: constructor's own bump is the only "migration"
        Assert.Null(catalog.TakePendingRecipeMigrationNotice()); // ...and it invalidated zero rows, so nothing to report
    }

    [Fact]
    public void Only_the_most_recent_recipe_backup_is_kept()
    {
        // Finding from tech-lead review: every future PhashRecipeVersion bump used to leave another
        // full VACUUM INTO copy of the catalogue on disk forever. Only the most recent backup is
        // ever useful for a revert, so earlier ones must be pruned as each new one is taken.
        var dbPath = Path.Combine(_work, "prune_catalog.db");
        using var db = new Database(dbPath);
        new ImageRepository(db).Upsert(new ImageRecord
        { Path = "/a.jpg", ContentHash = "h1", FileSize = 1, Mtime = 1 });

        db.EnsurePhashRecipeForTest(101);
        db.EnsurePhashRecipeForTest(102);
        db.EnsurePhashRecipeForTest(103);

        var backups = Directory.GetFiles(_work, "prune_catalog.db.bak-recipe-*");
        Assert.Single(backups);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_work)) Directory.Delete(_work, true); } catch { }
    }
}
