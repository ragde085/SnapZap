using SnapZap.App;
using SnapZap.App.Resources;
using SnapZap.App.Services;
using SnapZap.Core.Data;
using SnapZap.Core;
using SnapZap.Core.Platform;
using Xunit;

namespace SnapZap.Tests;

/// <summary>
/// AppState decides what the grid shows and what the destructive buttons are aimed at, so its
/// failure modes are "the user acts on a picture of the library that isn't true any more".
/// </summary>
public class AppStateTests : IDisposable
{
    readonly string _work = Path.Combine(Path.GetTempPath(), "pc_state_" + Guid.NewGuid().ToString("N"));
    readonly CatalogService _catalog;
    readonly SessionStore _session;
    readonly AppState _state;

    public AppStateTests()
    {
        Directory.CreateDirectory(_work);
        _catalog = new CatalogService(_work);
        _session = new SessionStore(_catalog, TimeSpan.FromMilliseconds(50));
        _state = new AppState(_catalog, new MacOsTrashService(), _session, new DependencyChecker(_catalog),
            new TestStringLocalizer<AppStateResources>());
    }

    long AddImage(string name)
    {
        var path = Path.Combine(_work, name);
        File.WriteAllText(path, name);
        return _catalog.Images.Upsert(new ImageRecord
        {
            Path = path,
            FileSize = 1000,
            Mtime = 0,
            ContentHash = "hash-" + name,
            AnalyzedAt = 1,
        });
    }

    [Fact]
    public async Task A_group_stops_counting_as_duplicates_once_only_one_copy_survives()
    {
        // Groups are recorded when detected and the members are recycled afterwards. A group
        // with one surviving copy is a duplicate of nothing: offering to "review 1 duplicate
        // group" sends the user into a screen with a single photo and no decision to make.
        var keep = AddImage("keep.png");
        var extra = AddImage("extra.png");
        new DupeRepository(_catalog.Db).AddGroup(DupeKind.Exact, null, [(keep, true), (extra, false)]);

        await _state.LoadAsync();
        Assert.Single(_state.DupeGroups);
        Assert.Equal(1, _state.DupeExtraCount);

        // The extra is recycled; the group collapses to one member.
        new DeleteRow(_catalog).Remove(extra);

        await _state.LoadAsync();
        Assert.Empty(_state.DupeGroups);
        Assert.Equal(0, _state.DupeExtraCount);
        Assert.Null(_state.Images.Single().Dupe);   // and the survivor loses its duplicate badge
    }

    [Fact]
    public async Task A_group_whose_keeper_was_deleted_still_keeps_one_copy()
    {
        // The keeper flag is stored per member. If the keeper is recycled, every survivor still
        // reads IsKeeper=false — so all of them counted as reclaimable "extras", and the
        // one-click "select all extras" would arm a delete against every remaining copy of the
        // photo. Something must always survive a group.
        var keeper = AddImage("keeper.png");
        var a = AddImage("a.png");
        var b = AddImage("b.png");
        new DupeRepository(_catalog.Db)
            .AddGroup(DupeKind.Exact, null, [(keeper, true), (a, false), (b, false)]);

        new DeleteRow(_catalog).Remove(keeper);

        await _state.LoadAsync();

        Assert.Single(_state.DupeGroups);
        Assert.Equal(1, _state.DupeExtraCount);                       // not 2
        Assert.Single(_state.DupeOf.Values, d => d.IsKeeper);    // exactly one survivor kept

        // And the button that acts on this can never select the whole group.
        _state.SelectBy(AppState.SelectionScope.DuplicateExtras);
        Assert.Single(_state.Selected);
    }

    [Fact]
    public async Task Toggling_a_second_member_keeps_both_and_only_the_third_counts_as_an_extra()
    {
        // Two copies can both be worth keeping (e.g. one for a shared album, one for the
        // original folder) — toggling a second keeper on must not clear the first (the old
        // SetKeeper did exactly that), and the extras count/selection must follow: only the
        // member nobody chose to keep is reclaimable.
        var keeper = AddImage("keeper.png");
        var alsoWorthKeeping = AddImage("also.png");
        var extra = AddImage("extra.png");
        var groupId = new DupeRepository(_catalog.Db)
            .AddGroup(DupeKind.Exact, null, [(keeper, true), (alsoWorthKeeping, false), (extra, false)]);

        await _state.LoadAsync();
        await _state.ToggleKeeperAsync(groupId, alsoWorthKeeping);

        Assert.True(_state.DupeOf[keeper].IsKeeper);
        Assert.True(_state.DupeOf[alsoWorthKeeping].IsKeeper);
        Assert.False(_state.DupeOf[extra].IsKeeper);
        Assert.Equal(1, _state.DupeExtraCount);

        _state.SelectBy(AppState.SelectionScope.DuplicateExtras);
        Assert.Single(_state.Selected);
        Assert.Contains(extra, _state.Selected);

        _state.SelectBy(AppState.SelectionScope.DuplicateKeepers);
        Assert.Equal(2, _state.Selected.Count);
    }

    /// <summary>Removes a catalog row the way a completed recycle does.</summary>
    sealed class DeleteRow(CatalogService catalog)
    {
        public void Remove(long id)
        {
            lock (catalog.Db.WriteLock)
            {
                using var cmd = catalog.Db.Writer.CreateCommand();
                cmd.CommandText = "DELETE FROM images WHERE id=$id";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
        }
    }

    public void Dispose()
    {
        _session.Dispose();
        _catalog.Dispose();
        try { Directory.Delete(_work, recursive: true); } catch { }
    }
}
