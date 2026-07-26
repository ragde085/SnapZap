using System.Text.Json;

namespace SnapZap.App.Services;

/// <summary>
/// Persists the parts of a triage session worth surviving a page reload — currently the
/// selection, which can represent a great deal of human judgement.
///
/// Written to app-data rather than browser storage so it also survives an app restart and
/// doesn't depend on which browser opened the tab. Writes are debounced: selection changes on
/// every click, and a library-sized selection is far too big to serialise that often.
///
/// ⚠ Known limitation — this is a singleton while <see cref="AppState"/> is per-circuit, so
/// with two tabs open on the same instance the saved selection is last-write-wins: each tab
/// persists its own full snapshot and the later one replaces the earlier outright, with no
/// merge and no warning. Acceptable for a single-user desktop tool that expects one window;
/// supporting multiple windows properly would mean scoping the store per circuit or merging
/// on a per-tab timestamp.
/// </summary>
public sealed class SessionStore : IDisposable
{
    sealed class Snapshot
    {
        public long[] SelectedIds { get; set; } = [];
    }

    readonly string _path;
    readonly Lock _gate = new();
    readonly TimeSpan _debounce;

    Timer? _timer;
    long[] _pending = [];
    bool _disposed;

    public SessionStore(CatalogService catalog) : this(catalog, TimeSpan.FromSeconds(1)) { }

    internal SessionStore(CatalogService catalog, TimeSpan debounce)
    {
        _path = Path.Combine(catalog.AppDataDir, "session.json");
        _debounce = debounce;
    }

    /// <summary>Ids selected when the session was last saved. Callers must still discard ids
    /// that no longer exist — the catalog may have changed while we were away.</summary>
    public IReadOnlyList<long> LoadSelection()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(_path))?.SelectedIds ?? [];
        }
        catch { /* a corrupt session file must never block startup */ }
        return [];
    }

    /// <summary>Queue a save. Rapid successive calls collapse into one write.</summary>
    public void SaveSelection(IEnumerable<long> ids)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _pending = ids.ToArray();
            _timer ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    void Flush()
    {
        long[] ids;
        lock (_gate) ids = _pending;
        Write(ids);
    }

    void Write(long[] ids)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // Write via a temp file so an interrupted write can't leave an unreadable session.
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Snapshot { SelectedIds = ids }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch { /* session persistence is best-effort and must never surface as an error */ }
    }

    /// <summary>
    /// Flushes any pending selection before shutting down. Without this, closing the app within
    /// the debounce window — an entirely ordinary sequence, not a crash — would silently discard
    /// the user's last selection change even on a clean exit. A hard process kill can still lose
    /// it; that is the inherent cost of debouncing and is accepted.
    /// </summary>
    public void Dispose()
    {
        long[] pending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            pending = _pending;
        }
        Write(pending);
    }
}
