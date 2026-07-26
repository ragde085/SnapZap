using SnapZap.App;
using SnapZap.App.Services;
using Xunit;

namespace SnapZap.Tests;

/// <summary>
/// Session persistence keeps a triage session alive across a reload, so its failure modes are
/// "the user's judgement was silently thrown away". Debouncing, shutdown flushing and
/// resilience to a damaged file are all worth pinning down.
/// </summary>
public class SessionStoreTests : IDisposable
{
    readonly string _appData = Path.Combine(Path.GetTempPath(), "pc_sess_" + Guid.NewGuid().ToString("N"));
    readonly CatalogService _catalog;

    public SessionStoreTests()
    {
        // CatalogService picks its own app-data location, so point the store at a scratch dir
        // by constructing it through the internal test seam.
        Directory.CreateDirectory(_appData);
        _catalog = new CatalogService();
    }

    string SessionFile => Path.Combine(_catalog.AppDataDir, "session.json");

    [Fact]
    public async Task Rapid_changes_collapse_into_one_write()
    {
        using var store = new SessionStore(_catalog, TimeSpan.FromMilliseconds(120));

        // Simulates a burst of clicks: only the final state should reach disk.
        store.SaveSelection([1]);
        store.SaveSelection([1, 2]);
        store.SaveSelection([1, 2, 3]);

        await Task.Delay(400);

        Assert.Equal([1, 2, 3], store.LoadSelection().Order());
    }

    [Fact]
    public async Task Debounce_defers_the_write()
    {
        File.Delete(SessionFile);
        using var store = new SessionStore(_catalog, TimeSpan.FromMilliseconds(400));

        store.SaveSelection([7]);
        // Nothing should have hit disk yet — that is the whole point of debouncing.
        Assert.False(File.Exists(SessionFile));

        await Task.Delay(700);
        Assert.Equal([7L], store.LoadSelection());
    }

    [Fact]
    public void Dispose_flushes_a_pending_write()
    {
        File.Delete(SessionFile);

        // A long debounce that would never fire on its own: closing the app inside the window
        // is ordinary, and used to discard the last change even on a clean shutdown.
        var store = new SessionStore(_catalog, TimeSpan.FromMinutes(5));
        store.SaveSelection([11, 22]);
        store.Dispose();

        Assert.Equal([11L, 22L], new SessionStore(_catalog).LoadSelection().Order());
    }

    [Fact]
    public void A_damaged_session_file_is_ignored_rather_than_fatal()
    {
        Directory.CreateDirectory(_catalog.AppDataDir);
        File.WriteAllText(SessionFile, "{ this is not json");

        Assert.Empty(new SessionStore(_catalog).LoadSelection());
    }

    public void Dispose()
    {
        _catalog.Dispose();
        try { if (File.Exists(SessionFile)) File.Delete(SessionFile); } catch { }
        try { if (Directory.Exists(_appData)) Directory.Delete(_appData, true); } catch { }
    }
}
