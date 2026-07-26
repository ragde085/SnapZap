using System.Text.Json;

namespace SnapZap.App.Services;

/// <summary>
/// Owns the photo selection and persists it.
///
/// The selection lives here, in a singleton, rather than in the per-circuit <see cref="AppState"/>
/// because SnapZap is a single-user tool over a single catalog: two windows on the same library
/// are two views of one working set, not two independent ones. Holding a copy per circuit meant
/// each tab persisted its own full snapshot and the later write silently replaced the earlier —
/// one tab's triage work vanishing with no warning. With one owner, every window sees and edits
/// the same selection, and <see cref="Changed"/> keeps them in step.
///
/// Mutation is copy-on-write: readers hold a reference to an immutable snapshot and never need a
/// lock, which matters because <c>Contains</c> is called once per card per render while another
/// circuit may be editing. Writers swap the reference under a lock.
///
/// Persistence is to app-data rather than browser storage, so a session also survives an app
/// restart and doesn't depend on which browser opened the tab. Writes are debounced: selection
/// changes on every click and a library-sized selection is far too big to serialise that often.
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

    volatile HashSet<long> _selected = [];
    Timer? _timer;
    bool _disposed;

    /// <summary>Raised whenever the selection changes, including from another circuit. Handlers
    /// running in a Blazor component must marshal onto their own dispatcher.</summary>
    public event Action? Changed;

    public SessionStore(CatalogService catalog) : this(catalog, TimeSpan.FromSeconds(1)) { }

    internal SessionStore(CatalogService catalog, TimeSpan debounce)
    {
        _path = Path.Combine(catalog.AppDataDir, "session.json");
        _debounce = debounce;
        _selected = [.. LoadPersisted()];   // seed once per process, not once per circuit
    }

    /// <summary>The current selection. Treat as immutable — mutate via <see cref="Mutate"/>.</summary>
    public HashSet<long> Current => _selected;

    /// <summary>
    /// Apply a change to the selection. The callback receives a private copy which becomes the
    /// new snapshot, so concurrent readers are never exposed to a half-applied edit.
    /// </summary>
    public void Mutate(Action<HashSet<long>> change)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var next = new HashSet<long>(_selected);
            change(next);
            _selected = next;
            QueueSave(next);
        }
        Changed?.Invoke();
    }

    /// <summary>Ids persisted by an earlier run. Callers must still discard ids that no longer
    /// exist — the catalog may have changed while we were away.</summary>
    public IReadOnlyList<long> LoadPersisted()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(_path))?.SelectedIds ?? [];
        }
        catch { /* a corrupt session file must never block startup */ }
        return [];
    }

    /// <summary>Caller already holds <see cref="_gate"/>.</summary>
    void QueueSave(HashSet<long> ids)
    {
        _pending = [.. ids];
        _timer ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
    }

    long[]? _pending;   // null = nothing queued; [] is a legitimate "user cleared it"

    void Flush()
    {
        long[]? ids;
        lock (_gate) ids = _pending;
        if (ids is not null) Write(ids);
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
    /// Flushes any pending selection before shutting down. Without this, closing the app inside
    /// the debounce window — an entirely ordinary sequence, not a crash — would silently discard
    /// the user's last change even on a clean exit. A hard process kill can still lose it; that
    /// is the inherent cost of debouncing and is accepted.
    /// </summary>
    public void Dispose()
    {
        long[]? pending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            pending = _pending;
        }
        // Only write if a change was actually queued. Writing unconditionally meant that
        // simply opening and closing the app — never touching the selection — overwrote
        // session.json with an empty list and destroyed the saved triage.
        if (pending is not null) Write(pending);
    }
}
