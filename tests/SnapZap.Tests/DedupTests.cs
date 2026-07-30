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
        new DedupSettings { VariantEnabled = false }.Save(db);

        var report = await new DuplicateService(db).DetectAsync(_photos);

        Assert.Equal(1, report.ExactGroups);
        Assert.Equal(0, report.VariantGroups);
        Assert.Equal(0, report.BurstGroups);
        Assert.Contains("same-shot matching is turned off", report.Note);
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
        new DedupSettings { VariantEnabled = true }.Save(db);

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

        new DedupSettings { VariantEnabled = false }.Save(db);
        await new DuplicateService(db).DetectAsync(_photos);

        // Exact and Burst always run — neither has a toggle — so a "visual matching off" run still
        // covers both. Only Variant is absent from the mask.
        var row = new ImageRepository(db).Under(_photos).Single();
        Assert.Equal(DupeKinds.Exact | DupeKinds.Burst, row.DupeCheckedKinds);

        // With variant detection switched on, the stored mask no longer covers what is enabled,
        // which is what makes the folder tree show the work as outstanding again.
        var richer = new DedupSettings { VariantEnabled = true };
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
    /// <b>Regression: detecting duplicates in one folder must not strip burst protection from
    /// another.</b>
    /// </summary>
    /// <remarks>
    /// One catalogue outlives every folder ever scanned and detection always runs against a single
    /// root, so an unscoped clear of <c>images.burst_adjacent</c> wipes the protection every other
    /// folder earned — silently re-opening the superset-Variant hole the flag exists to close
    /// (docs/ROADMAP.md, defect B). Scan A, then B, and A's burst frames become bulk-selectable
    /// again with nothing on screen to say so.
    ///
    /// <b>Both folders hold a real two-frame burst, and that is load-bearing.</b> An earlier
    /// version of this test put ordinary PNGs in B and passed with the bug still present:
    /// <c>BurstFinder</c> returns before it writes anything when a folder has fewer than two
    /// photos carrying an EXIF capture time, so the clear never ran and the test proved nothing.
    /// The committed <c>fixtures/burst</c> frames exist because SkiaSharp cannot write EXIF, so a
    /// burst cannot be synthesised in-process.
    /// </remarks>
    [Fact]
    public async Task Detecting_in_one_folder_keeps_another_folders_burst_protection()
    {
        var a = Path.Combine(_photos, "A");
        var b = Path.Combine(_photos, "B");
        CopyBurst(a);
        CopyBurst(b);

        using var db = new Database(_dbPath);
        await new Scanner(db, new SkiaImageService(), _thumbs).ScanAsync(_photos);

        var repo = new ImageRepository(db);
        await new DuplicateService(db).DetectAsync(a);

        var protectedInA = repo.BurstAdjacentIds();
        Assert.NotEmpty(protectedInA);   // precondition: A really did earn protection

        // The regression: detection over a different folder used to clear the whole column.
        await new DuplicateService(db).DetectAsync(b);

        var afterB = repo.BurstAdjacentIds();
        Assert.All(protectedInA, id =>
            Assert.True(afterB.Contains(id), $"image {id} in folder A lost its burst protection"));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    //  DedupSettings.BurstEnabled — the switch, and the consequence of using it
    // ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Turning burst detection off makes burst frames bulk-selectable. That is the whole point
    /// of the setting, and this test exists so nobody can change it by accident.</b>
    /// </summary>
    /// <remarks>
    /// Read together with <c>NsfwBandTests.An_ordinary_photo_of_a_person_is_not_flagged</c>: both
    /// pin a deliberate safety position rather than an implementation detail. This one asserts the
    /// dangerous direction on purpose, because the danger is the feature — a user who wants burst
    /// frames treated as copies is asking for exactly this. If this test starts failing, the
    /// question is not "how do I make it pass", it is "did someone mean to change what this setting
    /// does".
    ///
    /// Note what it also proves: the frames do not become <i>ungrouped</i> when burst is off. They
    /// come back as something bulk-selectable, because pixel distance cannot separate a burst frame
    /// from a re-encode. That is why the old default-off version of this setting was inert — opting
    /// out did not protect anything, it just moved the frames into a group that could be swept.
    /// </remarks>
    [Fact]
    public async Task Disabling_burst_detection_makes_burst_frames_bulk_selectable()
    {
        CopyBurst(_photos);

        using var db = new Database(_dbPath);
        await new Scanner(db, new SkiaImageService(), _thumbs).ScanAsync(_photos);
        var repo = new ImageRepository(db);

        // On (the default): the frames are grouped as a burst and held back.
        new DedupSettings().Save(db);
        await new DuplicateService(db).DetectAsync(_photos);

        var onGroups = new DupeRepository(db).Groups();
        Assert.Contains(onGroups, g => g.Kind == DupeKind.Burst);
        var onAssign = DupeAssignmentResolver.Resolve(onGroups, repo.BurstAdjacentIds());
        Assert.DoesNotContain(onAssign.Values, a => a.IsBulkSelectableExtra);

        // Off: no burst group survives, the protection column is cleared for this root, and the
        // frames are now offered to a bulk delete.
        new DedupSettings { BurstEnabled = false }.Save(db);
        await new DuplicateService(db).DetectAsync(_photos);

        var offGroups = new DupeRepository(db).Groups();
        Assert.DoesNotContain(offGroups, g => g.Kind == DupeKind.Burst);
        Assert.Empty(repo.BurstAdjacentIds());

        var offAssign = DupeAssignmentResolver.Resolve(offGroups, repo.BurstAdjacentIds());
        Assert.Contains(offAssign.Values, a => a.IsBulkSelectableExtra);
    }

    /// <summary>
    /// Switching burst detection back on restores the protection, without a re-scan.
    /// </summary>
    /// <remarks>
    /// The reversibility is the argument for allowing the switch at all, so it is pinned. Capture
    /// time is read during the scan regardless of this setting, which is why re-detecting is enough
    /// — if this ever needed a re-scan, the setting would be a one-way door and should not exist.
    /// </remarks>
    [Fact]
    public async Task Re_enabling_burst_detection_restores_protection_without_a_rescan()
    {
        CopyBurst(_photos);

        using var db = new Database(_dbPath);
        await new Scanner(db, new SkiaImageService(), _thumbs).ScanAsync(_photos);
        var repo = new ImageRepository(db);

        new DedupSettings { BurstEnabled = false }.Save(db);
        await new DuplicateService(db).DetectAsync(_photos);
        Assert.Empty(repo.BurstAdjacentIds());

        new DedupSettings { BurstEnabled = true }.Save(db);
        await new DuplicateService(db).DetectAsync(_photos);   // no second ScanAsync

        Assert.NotEmpty(repo.BurstAdjacentIds());
        Assert.Contains(new DupeRepository(db).Groups(), g => g.Kind == DupeKind.Burst);
    }

    /// <summary>
    /// Detecting with burst off in one folder must not clear another folder's protection.
    /// </summary>
    /// <remarks>
    /// The disabled branch clears <c>burst_adjacent</c>, which is a new unscoped-write hazard of
    /// exactly the kind <see cref="Detecting_in_one_folder_keeps_another_folders_burst_protection"/>
    /// covers for the enabled path. It routes through <c>BurstFinder.ClearProtection</c> so the
    /// scoping lives in one place; this proves the routing is real rather than intended.
    /// </remarks>
    [Fact]
    public async Task Disabling_burst_in_one_folder_keeps_another_folders_protection()
    {
        var a = Path.Combine(_photos, "A");
        var b = Path.Combine(_photos, "B");
        CopyBurst(a);
        CopyBurst(b);

        using var db = new Database(_dbPath);
        await new Scanner(db, new SkiaImageService(), _thumbs).ScanAsync(_photos);
        var repo = new ImageRepository(db);

        new DedupSettings().Save(db);
        await new DuplicateService(db).DetectAsync(a);
        var protectedInA = repo.BurstAdjacentIds();
        Assert.NotEmpty(protectedInA);

        // Now turn it off and detect only in B. A is untouched by that decision.
        new DedupSettings { BurstEnabled = false }.Save(db);
        await new DuplicateService(db).DetectAsync(b);

        var afterB = repo.BurstAdjacentIds();
        Assert.All(protectedInA, id =>
            Assert.True(afterB.Contains(id), $"image {id} in folder A lost its burst protection"));
    }

    /// <summary>
    /// A folder checked with burst off must read as still owing burst work once it is switched on.
    /// </summary>
    /// <remarks>
    /// <c>CoveredKinds</c> feeds <c>images.dupe_checked_kinds</c>, which is how the folder tree
    /// decides what is still pending. Recording Burst unconditionally would make re-enabling the
    /// protection look like a no-op on every folder already scanned.
    /// </remarks>
    [Fact]
    public void Covered_kinds_omits_burst_when_it_is_disabled()
    {
        Assert.True(new DedupSettings().CoveredKinds.HasFlag(DupeKinds.Burst));
        Assert.False(new DedupSettings { BurstEnabled = false }.CoveredKinds.HasFlag(DupeKinds.Burst));

        // Exact is not negotiable on either setting.
        Assert.True(new DedupSettings { BurstEnabled = false, VariantEnabled = false }
            .CoveredKinds.HasFlag(DupeKinds.Exact));
    }

    /// <summary>The switch round-trips through the catalogue's meta table like every other setting.</summary>
    [Fact]
    public void Burst_enabled_round_trips_through_meta()
    {
        using var db = new Database(_dbPath);
        Assert.True(DedupSettings.Load(db).BurstEnabled);            // default is on

        new DedupSettings { BurstEnabled = false }.Save(db);
        Assert.False(DedupSettings.Load(db).BurstEnabled);

        new DedupSettings { BurstEnabled = true }.Save(db);
        Assert.True(DedupSettings.Load(db).BurstEnabled);
    }

    /// <summary>Copies the committed two-frame EXIF burst into <paramref name="folder"/>.</summary>
    static void CopyBurst(string folder)
    {
        Directory.CreateDirectory(folder);
        var src = Path.Combine(AppContext.BaseDirectory, "fixtures", "burst");
        foreach (var f in Directory.GetFiles(src, "*.jpg"))
            File.Copy(f, Path.Combine(folder, Path.GetFileName(f)), overwrite: true);
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

    /// <summary>
    /// The keeper must be the same photo every run, on any machine.
    /// </summary>
    /// <remarks>
    /// Rows are inserted as the parallel scan completes them, so a fresh catalogue over the same
    /// folder assigns different ids each run. When the members tie on every earlier key — same
    /// pixels, same bytes, which is exactly what re-encodes of one shot look like — the final key
    /// is the one that decides, and an Id tie-break silently inherited the scan's completion
    /// order. Observed live: the same four-file group kept a different photo on 2 of 5 runs.
    ///
    /// Five fresh catalogues over one unchanged folder, and the answer has to be identical.
    /// </remarks>
    [Fact]
    public async Task Keeper_is_the_same_photo_across_independent_scans_of_one_folder()
    {
        // Four files that tie on pixels and bytes: identical content, distinct paths.
        WritePng(Path.Combine(_photos, "frame_a.png"), 200, 200, SKColors.Teal);
        foreach (var n in new[] { "frame_b.png", "frame_c.png", "frame_d.png" })
            File.Copy(Path.Combine(_photos, "frame_a.png"), Path.Combine(_photos, n));

        var keepers = new List<string>();
        for (var run = 0; run < 5; run++)
        {
            var dbPath = Path.Combine(_work, $"run{run}.db");
            using var db = new Database(dbPath);
            await new Scanner(db, new SkiaImageService(), Path.Combine(_work, $"t{run}")).ScanAsync(_photos);

            new ExactDuplicateFinder(db).FindAndStore();

            var repo = new ImageRepository(db);
            var group = new DupeRepository(db).Groups().Single();
            var keeperId = group.Members.Single(m => m.IsKeeper).ImageId;
            keepers.Add(Path.GetFileName(repo.ByIds([keeperId]).Single().Path));
        }

        Assert.Single(keepers.Distinct());
    }

    // ---- Reconciliation ------------------------------------------------------

    /// <summary>Write a group directly, to set up an overlap without depending on thresholds.</summary>
    long Group(Database db, DupeKind kind, params long[] ids) =>
        new DupeRepository(db).AddGroup(kind, "test", ids.Select((id, i) => (id, i == 0)).ToList());

    async Task<(Database Db, long[] Ids)> TwoPhotos()
    {
        WritePng(Path.Combine(_photos, "a.png"), 100, 100, SKColors.Red);
        WritePng(Path.Combine(_photos, "b.png"), 100, 100, SKColors.Blue);
        var db = new Database(_dbPath);
        await new Scanner(db, new SkiaImageService(), _thumbs).ScanAsync(_photos);
        return (db, new ImageRepository(db).Under(_photos).Select(i => i.Id).OrderBy(i => i).ToArray());
    }

    /// <summary>
    /// The defect this exists to fix. Byte-identical copies satisfy every detector's threshold, so
    /// the same photos were written as an Exact group and again as a Variant group. Measured on a
    /// 38,668-photo library: 19 groups over 25 distinct photos, and the review flow showed the same
    /// pair as group 1 and again as group 12.
    /// </summary>
    [Fact]
    public async Task A_variant_group_restating_an_exact_group_is_dropped()
    {
        var (db, ids) = await TwoPhotos();
        using var _ = db;
        Group(db, DupeKind.Exact, ids[0], ids[1]);
        Group(db, DupeKind.Variant, ids[0], ids[1]);

        Assert.Equal(1, GroupReconciler.Reconcile(db, null));

        var left = Assert.Single(new DupeRepository(db).Groups());
        Assert.Equal(DupeKind.Exact, left.Kind);
    }

    /// <summary>
    /// <b>Safety-critical, and the non-obvious direction.</b> When Burst and Variant claim the same
    /// photos, Burst survives — so they stay out of bulk selection. Pixel distance cannot separate
    /// the two (measured on real files: two different burst frames 9 bits apart, one photograph and
    /// its 50% resize 16 apart), so the detector that consulted the capture clock is the one to
    /// believe, and believing it errs toward review rather than toward Delete.
    /// </summary>
    [Fact]
    public async Task A_variant_group_restating_a_burst_group_is_dropped_not_the_other_way_round()
    {
        var (db, ids) = await TwoPhotos();
        using var _ = db;
        Group(db, DupeKind.Variant, ids[0], ids[1]);
        Group(db, DupeKind.Burst, ids[0], ids[1]);

        Assert.Equal(1, GroupReconciler.Reconcile(db, null));

        var left = Assert.Single(new DupeRepository(db).Groups());
        Assert.Equal(DupeKind.Burst, left.Kind);
    }

    /// <summary>
    /// A copy inherits the original's EXIF, so byte-identical files look exactly like frames taken
    /// at one instant and were being pulled into Burst groups — and then withheld from bulk
    /// selection. The tool refused to reclaim the most certain duplicates it can find; reclaimable
    /// fell from 372 KB to 74 KB on the fixture library for this reason alone. Exact outranks the
    /// clock, because for copies the clock says nothing.
    /// </summary>
    [Fact]
    public async Task Exact_outranks_burst_so_identical_copies_stay_selectable()
    {
        var (db, ids) = await TwoPhotos();
        using var _ = db;
        Group(db, DupeKind.Exact, ids[0], ids[1]);
        Group(db, DupeKind.Burst, ids[0], ids[1]);

        Assert.Equal(1, GroupReconciler.Reconcile(db, null));

        var left = Assert.Single(new DupeRepository(db).Groups());
        Assert.Equal(DupeKind.Exact, left.Kind);
    }

    /// <summary>
    /// Only exact cover is dropped. Two groups that merely overlap are two different claims about
    /// two different sets; collapsing them would silently merge photographs the detectors kept
    /// apart, which is the failure the complete-linkage grouper exists to avoid.
    /// </summary>
    [Fact]
    public async Task Partially_overlapping_groups_both_survive()
    {
        WritePng(Path.Combine(_photos, "a.png"), 100, 100, SKColors.Red);
        WritePng(Path.Combine(_photos, "b.png"), 100, 100, SKColors.Blue);
        WritePng(Path.Combine(_photos, "c.png"), 100, 100, SKColors.Green);
        using var db = new Database(_dbPath);
        await new Scanner(db, new SkiaImageService(), _thumbs).ScanAsync(_photos);
        var ids = new ImageRepository(db).Under(_photos).Select(i => i.Id).OrderBy(i => i).ToArray();

        Group(db, DupeKind.Exact, ids[0], ids[1]);
        Group(db, DupeKind.Variant, ids[1], ids[2]);

        Assert.Equal(0, GroupReconciler.Reconcile(db, null));
        Assert.Equal(2, new DupeRepository(db).Groups().Count);
    }

    /// <summary>
    /// A stronger group covering only *part* of a weaker one leaves the weaker one alone: the
    /// Variant group is still saying something about the third photo that nothing else says.
    /// </summary>
    [Fact]
    public async Task A_weaker_group_reaching_beyond_the_stronger_one_survives()
    {
        WritePng(Path.Combine(_photos, "a.png"), 100, 100, SKColors.Red);
        WritePng(Path.Combine(_photos, "b.png"), 100, 100, SKColors.Blue);
        WritePng(Path.Combine(_photos, "c.png"), 100, 100, SKColors.Green);
        using var db = new Database(_dbPath);
        await new Scanner(db, new SkiaImageService(), _thumbs).ScanAsync(_photos);
        var ids = new ImageRepository(db).Under(_photos).Select(i => i.Id).OrderBy(i => i).ToArray();

        Group(db, DupeKind.Exact, ids[0], ids[1]);
        Group(db, DupeKind.Variant, ids[0], ids[1], ids[2]);

        Assert.Equal(0, GroupReconciler.Reconcile(db, null));
        Assert.Equal(2, new DupeRepository(db).Groups().Count);
    }

    /// <summary>
    /// The status line is counted back off the catalogue, not summed from the detectors, so it
    /// describes the groups the review flow will actually step through. Reporting detector totals
    /// is what produced "7 identical, 12 same shot groups" for a library with 12 findings.
    /// </summary>
    [Fact]
    public async Task The_report_counts_what_survived_reconciliation()
    {
        WritePng(Path.Combine(_photos, "a.png"), 100, 100, SKColors.Red);
        File.Copy(Path.Combine(_photos, "a.png"), Path.Combine(_photos, "b.png"));

        using var db = new Database(_dbPath);
        await new Scanner(db, new SkiaImageService(), _thumbs).ScanAsync(_photos);

        var report = await new DuplicateService(db).DetectAsync(_photos);

        // Identical files: one Exact group, and no Variant restatement of it left standing.
        Assert.Equal(1, report.ExactGroups);
        Assert.Equal(0, report.VariantGroups);
        Assert.Equal(report.ExactGroups + report.VariantGroups + report.BurstGroups,
                     new DupeRepository(db).Groups().Count(g => g.Members.Count >= 2));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_work)) Directory.Delete(_work, true); } catch { }
    }
}
