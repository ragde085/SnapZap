using SnapZap.App;
using SnapZap.App.Services;
using SnapZap.Core;
using SnapZap.Core.Platform;
using Xunit;

namespace SnapZap.Tests;

/// <summary>
/// The catalogue deliberately keeps every folder ever scanned, because that cache is what makes
/// re-scanning a large library near-instant. The scope is what stops the cache leaking into the
/// view — and it is a safety property, not a tidiness one: what the grid shows is exactly what
/// "select everything shown" then Export or Delete is aimed at. An unscoped load put photos from
/// libraries the user scanned weeks ago, and cannot see on screen, inside that reach.
/// </summary>
public class CatalogScopeTests : IDisposable
{
    readonly string _work = Path.Combine(Path.GetTempPath(), "pc_scope_" + Guid.NewGuid().ToString("N"));
    readonly string _libA;
    readonly string _libB;
    readonly CatalogService _catalog;
    readonly SessionStore _session;
    readonly AppState _state;

    public CatalogScopeTests()
    {
        Directory.CreateDirectory(_work);
        _libA = Path.Combine(_work, "libA");
        _libB = Path.Combine(_work, "libB");
        Directory.CreateDirectory(Path.Combine(_libA, "sub"));
        Directory.CreateDirectory(_libB);

        _catalog = new CatalogService(_work);
        _session = new SessionStore(_catalog, TimeSpan.FromMilliseconds(50));
        _state = new AppState(_catalog, new MacOsTrashService(), _session, new DependencyChecker(_catalog));
    }

    long Add(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, name);
        return _catalog.Images.Upsert(new ImageRecord
        {
            Path = path, FileSize = 1000, Mtime = 0, ContentHash = "h-" + Guid.NewGuid(), AnalyzedAt = 1,
        });
    }

    [Fact]
    public async Task Scanning_a_folder_scopes_the_view_to_it()
    {
        Add(_libA, "a1.jpg");
        Add(_libA, "a2.jpg");
        Add(_libB, "b1.jpg");

        await _catalog.ScanAsync(_libA, null, CancellationToken.None);
        await _state.LoadAsync();

        Assert.All(_state.Images, i => Assert.StartsWith(_libA, i.Path, StringComparison.Ordinal));
        Assert.DoesNotContain(_state.Images, i => i.Path.StartsWith(_libB, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Switching_folders_replaces_the_view_rather_than_adding_to_it()
    {
        Add(_libA, "a1.jpg");
        Add(_libB, "b1.jpg");
        Add(_libB, "b2.jpg");

        await _catalog.ScanAsync(_libA, null, CancellationToken.None);
        await _state.LoadAsync();
        var afterA = _state.Images.Count;

        await _catalog.ScanAsync(_libB, null, CancellationToken.None);
        await _state.LoadAsync();

        Assert.Equal(1, afterA);
        Assert.Equal(2, _state.Images.Count);
        Assert.All(_state.Images, i => Assert.StartsWith(_libB, i.Path, StringComparison.Ordinal));
    }

    /// <summary>The point of scoping: the destructive actions can only ever reach what is shown.</summary>
    [Fact]
    public async Task Select_everything_shown_cannot_reach_another_folder()
    {
        Add(_libA, "a1.jpg");
        var strayId = Add(_libB, "b1.jpg");

        await _catalog.ScanAsync(_libA, null, CancellationToken.None);
        await _state.LoadAsync();
        _state.SelectAllVisible();

        Assert.DoesNotContain(strayId, _state.Selected);
        Assert.Single(_state.Selected);
    }

    /// <summary>Scoping is a view concern; the other folder stays cached so returning is instant.</summary>
    [Fact]
    public async Task The_other_folder_stays_in_the_catalogue_as_cache()
    {
        Add(_libA, "a1.jpg");
        Add(_libB, "b1.jpg");

        await _catalog.ScanAsync(_libA, null, CancellationToken.None);
        await _state.LoadAsync();

        Assert.Single(_state.Images);
        Assert.Equal(2, _catalog.Images.TotalCount());
    }

    /// <summary>
    /// A catalogue written before scoping existed has no recorded root. It must keep showing
    /// what it always showed rather than coming up empty, and the next scan settles the scope.
    /// </summary>
    [Fact]
    public async Task A_catalogue_with_no_recorded_root_still_loads()
    {
        Add(_libA, "a1.jpg");
        Add(_libB, "b1.jpg");

        Assert.Null(_catalog.ScanRoot);
        await _state.LoadAsync();

        Assert.Equal(2, _state.Images.Count);
    }

    /// <summary>A sibling whose name merely starts with the root's must not be swept in.</summary>
    [Fact]
    public async Task Scope_does_not_swallow_a_sibling_with_a_shared_prefix()
    {
        var decoy = _libA + "-old";
        Directory.CreateDirectory(decoy);
        Add(_libA, "a1.jpg");
        Add(decoy, "old.jpg");

        await _catalog.ScanAsync(_libA, null, CancellationToken.None);
        await _state.LoadAsync();

        Assert.Single(_state.Images);
        Assert.DoesNotContain(_state.Images, i => i.Path.StartsWith(decoy, StringComparison.Ordinal));
    }

    /// <summary>
    /// Scoping the view but not the work is worse than not scoping: the screen says four photos
    /// while the app quietly grinds through thousands in folders the user cannot see. Exact
    /// dedup and NSFW scoring share <c>PathScope</c>, so this covers the predicate both use.
    /// </summary>
    [Fact]
    public async Task Duplicate_detection_ignores_identical_photos_outside_the_scope()
    {
        // The same bytes in two libraries. Only one library is open.
        File.WriteAllText(Path.Combine(_libA, "a.jpg"), "same");
        File.WriteAllText(Path.Combine(_libB, "b.jpg"), "same");
        foreach (var (dir, name) in new[] { (_libA, "a.jpg"), (_libB, "b.jpg") })
            _catalog.Images.Upsert(new ImageRecord
            {
                Path = Path.Combine(dir, name), FileSize = 4, Mtime = 0,
                ContentHash = "identical", AnalyzedAt = 1,
            });

        var groups = new SnapZap.Core.Dedup.ExactDuplicateFinder(_catalog.Db).FindAndStore(_libA);

        // One copy each side is not a duplicate anyone can act on from inside libA.
        Assert.Equal(0, groups);
    }

    [Fact]
    public async Task Duplicate_detection_still_finds_copies_inside_the_scope()
    {
        foreach (var name in new[] { "one.jpg", "one-copy.jpg" })
            _catalog.Images.Upsert(new ImageRecord
            {
                Path = Path.Combine(_libA, name), FileSize = 4, Mtime = 0,
                ContentHash = "identical", AnalyzedAt = 1,
            });

        Assert.Equal(1, new SnapZap.Core.Dedup.ExactDuplicateFinder(_catalog.Db).FindAndStore(_libA));
        await Task.CompletedTask;
    }

    /// <summary>
    /// Re-running detection in one folder used to clear every group in the catalogue first, so
    /// a dedup pass over folder B silently discarded results the user had already reviewed in
    /// folder A — with no message and no way back except another scan.
    /// </summary>
    [Fact]
    public void Re_detecting_in_one_folder_keeps_another_folders_groups()
    {
        foreach (var name in new[] { "a1.jpg", "a2.jpg" })
            _catalog.Images.Upsert(new ImageRecord
            {
                Path = Path.Combine(_libA, name), FileSize = 4, Mtime = 0, ContentHash = "ha", AnalyzedAt = 1,
            });
        foreach (var name in new[] { "b1.jpg", "b2.jpg" })
            _catalog.Images.Upsert(new ImageRecord
            {
                Path = Path.Combine(_libB, name), FileSize = 4, Mtime = 0, ContentHash = "hb", AnalyzedAt = 1,
            });

        var finder = new SnapZap.Core.Dedup.ExactDuplicateFinder(_catalog.Db);
        Assert.Equal(1, finder.FindAndStore(_libA));
        Assert.Equal(1, finder.FindAndStore(_libB));

        // Both survive: detecting in B cleared only B's groups.
        var all = new SnapZap.Core.Data.DupeRepository(_catalog.Db).Groups().ToList();
        Assert.Equal(2, all.Count);
    }

    /// <summary>
    /// The case the whole scoped-clear exists to get right, and the one that is easiest to get
    /// wrong: a group with a foot in each library. Deleting it because one member is in scope
    /// cascades its members away, so the out-of-scope copy silently loses its duplicate status
    /// — and a scoped rebuild can never restore the pairing, because it filters both sides of
    /// its own hash match to the same root.
    /// </summary>
    [Fact]
    public void A_group_straddling_two_folders_survives_a_scoped_rebuild()
    {
        var inA = _catalog.Images.Upsert(new ImageRecord
        {
            Path = Path.Combine(_libA, "a.jpg"), FileSize = 4, Mtime = 0,
            ContentHash = "shared", AnalyzedAt = 1,
        });
        var inB = _catalog.Images.Upsert(new ImageRecord
        {
            Path = Path.Combine(_libB, "b.jpg"), FileSize = 4, Mtime = 0,
            ContentHash = "shared", AnalyzedAt = 1,
        });

        var finder = new SnapZap.Core.Dedup.ExactDuplicateFinder(_catalog.Db);
        Assert.Equal(1, finder.FindAndStore(null));          // unscoped: the pair is a group

        finder.FindAndStore(_libA);                          // now work inside libA only

        var members = new SnapZap.Core.Data.DupeRepository(_catalog.Db).Groups()
            .SelectMany(g => g.Members).Select(m => m.ImageId).ToList();
        Assert.Contains(inA, members);
        Assert.Contains(inB, members);
    }

    /// <summary>A root the user typed with a trailing separator is the same folder.</summary>
    [Fact]
    public async Task A_root_with_a_trailing_separator_scopes_the_same_way()
    {
        Add(_libA, "a1.jpg");
        Add(_libB, "b1.jpg");

        await _catalog.ScanAsync(_libA + Path.DirectorySeparatorChar, null, CancellationToken.None);
        await _state.LoadAsync();

        Assert.Single(_state.Images);
        Assert.All(_state.Images, i => Assert.StartsWith(_libA, i.Path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Forget_empties_the_catalogue_but_keeps_the_undo_log()
    {
        Add(_libA, "a1.jpg");
        await _catalog.ScanAsync(_libA, null, CancellationToken.None);
        await _state.LoadAsync();
        Assert.NotEmpty(_state.Images);

        // A recycled file's restore record is keyed by path, not by image id, so emptying the
        // catalogue must not take it with them — otherwise "forget the analysis" quietly
        // destroys the only in-app route back for anything already in the Recycle Bin.
        lock (_catalog.Db.WriteLock)
        {
            using var cmd = _catalog.Db.Writer.CreateCommand();
            cmd.CommandText = """
                INSERT INTO undo_log(batch_id, op, original_path, new_location, ts_utc)
                VALUES('batch-1', 'recycle', '/somewhere/gone.jpg', '/Trash/gone.jpg', 0)
                """;
            cmd.ExecuteNonQuery();
        }

        await _state.ForgetEverythingAsync();

        Assert.Empty(_state.Images);
        Assert.Equal(0, _catalog.Images.TotalCount());
        Assert.Null(_catalog.ScanRoot);

        using (var c = _catalog.Db.OpenRead())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM undo_log";
            Assert.Equal(1, Convert.ToInt32(cmd.ExecuteScalar()));
        }
    }

    public void Dispose()
    {
        _session.Dispose();
        _catalog.Dispose();
        try { Directory.Delete(_work, true); } catch { /* temp dir */ }
    }
}
