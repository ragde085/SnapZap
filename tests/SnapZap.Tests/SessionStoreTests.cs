using SnapZap.App;
using SnapZap.App.Services;
using Xunit;

namespace SnapZap.Tests;

/// <summary>
/// The session store owns the selection and persists it, so its failure modes are "the user's
/// judgement was silently thrown away". Sharing across windows, debouncing, shutdown flushing
/// and resilience to a damaged file are all worth pinning down.
/// </summary>
public class SessionStoreTests : IDisposable
{
    readonly CatalogService _catalog = new();

    string SessionFile => Path.Combine(_catalog.AppDataDir, "session.json");

    void ClearFile() { try { File.Delete(SessionFile); } catch { } }

    [Fact]
    public async Task Rapid_changes_collapse_into_one_write()
    {
        ClearFile();
        using var store = new SessionStore(_catalog, TimeSpan.FromMilliseconds(120));

        // Simulates a burst of clicks: only the final state should reach disk.
        store.Mutate(s => s.Add(1));
        store.Mutate(s => s.Add(2));
        store.Mutate(s => s.Add(3));

        await Task.Delay(400);

        Assert.Equal([1L, 2L, 3L], store.LoadPersisted().Order());
    }

    [Fact]
    public async Task Debounce_defers_the_write()
    {
        ClearFile();
        using var store = new SessionStore(_catalog, TimeSpan.FromMilliseconds(400));

        store.Mutate(s => s.Add(7));
        // Nothing should have hit disk yet — that is the whole point of debouncing.
        Assert.False(File.Exists(SessionFile));

        await Task.Delay(700);
        Assert.Equal([7L], store.LoadPersisted());
    }

    [Fact]
    public void Dispose_flushes_a_pending_write()
    {
        ClearFile();

        // A long debounce that would never fire on its own: closing the app inside the window
        // is ordinary, and used to discard the last change even on a clean shutdown.
        var store = new SessionStore(_catalog, TimeSpan.FromMinutes(5));
        store.Mutate(s => { s.Add(11); s.Add(22); });
        store.Dispose();

        Assert.Equal([11L, 22L], new SessionStore(_catalog).LoadPersisted().Order());
    }

    [Fact]
    public void Selection_is_seeded_from_the_previous_run()
    {
        ClearFile();
        var first = new SessionStore(_catalog, TimeSpan.FromMilliseconds(50));
        first.Mutate(s => s.Add(42));
        first.Dispose();

        // A reload gets a fresh store; the selection must survive it.
        using var second = new SessionStore(_catalog);
        Assert.Contains(42L, second.Current);
    }

    [Fact]
    public void Every_window_shares_one_selection()
    {
        // The bug this replaced: each circuit held its own copy and the later write silently
        // replaced the earlier, losing one window's triage work without warning.
        ClearFile();
        using var store = new SessionStore(_catalog, TimeSpan.FromMilliseconds(50));

        var notified = 0;
        store.Changed += () => notified++;

        store.Mutate(s => s.Add(1));   // "window A"
        store.Mutate(s => s.Add(2));   // "window B"

        Assert.Equal([1L, 2L], store.Current.Order());   // additive, not last-write-wins
        Assert.Equal(2, notified);                        // both windows told to re-render
    }

    [Fact]
    public void Readers_are_not_disturbed_by_a_concurrent_edit()
    {
        // Copy-on-write: a snapshot taken before a change must not observe it, which is what
        // lets cards call Contains() during a render while another window edits.
        ClearFile();
        using var store = new SessionStore(_catalog, TimeSpan.FromMilliseconds(50));
        store.Mutate(s => s.Add(1));

        var snapshot = store.Current;
        store.Mutate(s => s.Add(2));

        Assert.Single(snapshot);
        Assert.Equal(2, store.Current.Count);
    }

    [Fact]
    public void A_damaged_session_file_is_ignored_rather_than_fatal()
    {
        Directory.CreateDirectory(_catalog.AppDataDir);
        File.WriteAllText(SessionFile, "{ this is not json");

        Assert.Empty(new SessionStore(_catalog).LoadPersisted());
    }

    public void Dispose()
    {
        _catalog.Dispose();
        ClearFile();
    }
}
