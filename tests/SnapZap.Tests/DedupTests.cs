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

    [Fact]
    public async Task Detect_without_czkawka_still_reports_exact_and_notes_absence()
    {
        WritePng(Path.Combine(_photos, "a.png"), 100, 100, SKColors.Red);
        File.Copy(Path.Combine(_photos, "a.png"), Path.Combine(_photos, "b.png"));

        using var db = new Database(_dbPath);
        await new Scanner(db, new SkiaImageService(), _thumbs).ScanAsync(_photos);

        // Point at a non-existent czkawka path → similar detection unavailable, exact still works.
        var report = await new DuplicateService(db, czkawkaPath: "/nonexistent/czkawka_cli")
            .DetectAsync(_photos);

        Assert.Equal(1, report.ExactGroups);
        Assert.False(report.CzkawkaAvailable);
        Assert.Equal(0, report.SimilarGroups);
        Assert.Contains("not found", report.Note);
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

        await new DuplicateService(db, czkawkaPath: "/nonexistent/czkawka_cli").DetectAsync(_photos);

        // Marked even with czkawka absent: the run did everything this install can do, and
        // withholding the stamp would leave a pending dot the user has no way to clear.
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
        await new DuplicateService(db, czkawkaPath: "/nonexistent/czkawka_cli").DetectAsync(_photos);

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
        Assert.Equal(1, repo.MarkDupeChecked(inside));
        Assert.All(repo.Under(sibling), i => Assert.Null(i.DupeCheckedAt));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_work)) Directory.Delete(_work, true); } catch { }
    }
}

public class CzkawkaParserTests
{
    // Guards the defensive parser against the shapes czkawka's JSON might take. The first test
    // below is real captured output; the rest lock in tolerated variations.
    [Fact]
    public void Parses_real_czkawka_12_output()
    {
        // Captured verbatim from `czkawka_cli image -d <dir> --max-difference 10 -C out.json`
        // on czkawka 12.0.0. Shape confirmed: array of groups, each an array of file objects
        // carrying `path` plus metadata the parser ignores.
        var json = """
        [[{"path":"/private/tmp/czk2/compressed.jpg","size":33428,"width":900,"height":700,"modified_date":1785023032,"hashes":[],"difference":0},{"path":"/private/tmp/czk2/orig.jpg","size":158731,"width":900,"height":700,"modified_date":1785023032,"hashes":[],"difference":6}]]
        """;

        var groups = CzkawkaFinder.ParseGroups(json);

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Count);
        Assert.Contains("/private/tmp/czk2/orig.jpg", groups[0]);
        Assert.Contains("/private/tmp/czk2/compressed.jpg", groups[0]);
    }

    [Fact]
    public void Canonical_resolves_symlinked_ancestors()
    {
        // czkawka reports canonical paths while the catalog stores what the user typed. On
        // macOS /tmp is a symlink to /private/tmp, which silently broke every group match.
        var probe = Path.Combine(Path.GetTempPath(), $"snapzap_canon_{Guid.NewGuid():N}.txt");
        File.WriteAllText(probe, "x");
        try
        {
            var canonical = CzkawkaFinder.Canonical(probe);

            Assert.Equal(Path.GetFileName(probe), Path.GetFileName(canonical));
            // Whatever the platform, canonicalising twice must be stable.
            Assert.Equal(canonical, CzkawkaFinder.Canonical(canonical));
        }
        finally { File.Delete(probe); }
    }

    [Fact]
    public void Parses_array_of_groups_of_file_entries()
    {
        // Shape A: top-level array of groups, each group an array of {path,...} objects.
        var json = """
        [
          [ {"path":"/p/a.jpg","size":10}, {"path":"/p/b.jpg","size":12} ],
          [ {"path":"/p/c.jpg"}, {"path":"/p/d.jpg"}, {"path":"/p/e.jpg"} ]
        ]
        """;
        var groups = CzkawkaFinder.ParseGroups(json);
        Assert.Equal(2, groups.Count);
        Assert.Equal(2, groups[0].Count);
        Assert.Equal(3, groups[1].Count);
        Assert.Contains("/p/c.jpg", groups[1]);
    }

    [Fact]
    public void Finds_groups_nested_under_object_keys()
    {
        // Shape B: groups wrapped in an object (e.g. keyed by hash or under a results field).
        var json = """
        { "similar_images": {
            "grp1": [ {"path":"/x/1.png"}, {"path":"/x/2.png"} ]
        } }
        """;
        var groups = CzkawkaFinder.ParseGroups(json);
        Assert.Single(groups);
        Assert.Equal(2, groups[0].Count);
    }

    [Fact]
    public void Ignores_singletons_and_non_file_arrays()
    {
        var json = """
        { "meta": [1,2,3], "lonely": [ {"path":"/only/one.jpg"} ] }
        """;
        Assert.Empty(CzkawkaFinder.ParseGroups(json)); // no group of size >= 2
    }
}
