using SnapZap.Core;
using SnapZap.Core.Data;
using Xunit;

namespace SnapZap.Tests;

/// <summary>
/// <see cref="DupeRepository.ToggleKeeper"/> replaced the old clear-then-set-one "SetKeeper" so a
/// burst with two frames worth keeping doesn't force everything else in the group to export.
/// The one invariant that must survive the change: a group can never be toggled down to zero
/// keepers (see AppStateTests' recycled-keeper coverage for the other half of that guarantee).
/// </summary>
public class DupeRepositoryTests : IDisposable
{
    readonly string _work = Path.Combine(Path.GetTempPath(), "pc_dupe_repo_" + Guid.NewGuid().ToString("N"));
    readonly Database _db;
    readonly ImageRepository _images;
    readonly DupeRepository _dupes;

    public DupeRepositoryTests()
    {
        Directory.CreateDirectory(_work);
        _db = new Database(Path.Combine(_work, "catalog.db"));
        _images = new ImageRepository(_db);
        _dupes = new DupeRepository(_db);
    }

    long AddImage(string name) => _images.Upsert(new ImageRecord
    {
        Path = Path.Combine(_work, name),
        ContentHash = "hash-" + name,
        FileSize = 1,
        Mtime = 1,
    });

    bool IsKeeper(long groupId, long imageId) =>
        _dupes.Groups().Single(g => g.Id == groupId).Members.Single(m => m.ImageId == imageId).IsKeeper;

    [Fact]
    public void Toggling_a_second_member_on_keeps_both_without_clearing_the_first()
    {
        var a = AddImage("a.jpg");
        var b = AddImage("b.jpg");
        var c = AddImage("c.jpg");
        var groupId = _dupes.AddGroup(DupeKind.Burst, null, [(a, true), (b, false), (c, false)]);

        _dupes.ToggleKeeper(groupId, b);

        Assert.True(IsKeeper(groupId, a));
        Assert.True(IsKeeper(groupId, b));
        Assert.False(IsKeeper(groupId, c));
    }

    [Fact]
    public void Toggling_a_keeper_off_is_allowed_while_another_keeper_remains()
    {
        var a = AddImage("a.jpg");
        var b = AddImage("b.jpg");
        var groupId = _dupes.AddGroup(DupeKind.Burst, null, [(a, true), (b, true)]);

        _dupes.ToggleKeeper(groupId, a);

        Assert.False(IsKeeper(groupId, a));
        Assert.True(IsKeeper(groupId, b));
    }

    [Fact]
    public void Toggling_off_the_last_remaining_keeper_is_refused()
    {
        var a = AddImage("a.jpg");
        var b = AddImage("b.jpg");
        var groupId = _dupes.AddGroup(DupeKind.Exact, null, [(a, true), (b, false)]);

        _dupes.ToggleKeeper(groupId, a);   // a is the only keeper — must stay a keeper

        Assert.True(IsKeeper(groupId, a));
        Assert.False(IsKeeper(groupId, b));
    }

    [Fact]
    public void Toggling_an_image_that_is_not_a_member_of_the_group_does_nothing()
    {
        var a = AddImage("a.jpg");
        var outsider = AddImage("outsider.jpg");
        var groupId = _dupes.AddGroup(DupeKind.Exact, null, [(a, true)]);

        _dupes.ToggleKeeper(groupId, outsider);   // no exception, no effect

        Assert.True(IsKeeper(groupId, a));
        Assert.Single(_dupes.Groups().Single(g => g.Id == groupId).Members);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_work, recursive: true); } catch { }
    }
}
