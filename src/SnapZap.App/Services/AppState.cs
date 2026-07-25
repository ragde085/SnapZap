using SnapZap.Core;
using SnapZap.Core.Data;
using SnapZap.Core.Dedup;
using SnapZap.Core.Delete;
using SnapZap.Core.Nsfw;
using SnapZap.Core.Platform;
using SnapZap.Core.Scanning;
using ScanFailure = SnapZap.Core.Scanning.ScanFailure;

namespace SnapZap.App.Services;

/// <summary>A transient message with an optional one-click follow-up (e.g. undo a delete).</summary>
public sealed record Toast(string Message, string? ActionLabel = null, Func<Task>? Action = null);

/// <summary>
/// Scoped (one per circuit) view-model + operations for the whole app. Replaces the `state`
/// object and top-level functions in the old app.js. Components subscribe to <see cref="Changed"/>
/// and re-render; this class never touches the DOM/UI directly.
/// </summary>
public sealed class AppState(CatalogService catalog, ITrashService trash)
{
    public event Action? Changed;
    public void Notify() => Changed?.Invoke();

    // ---- Data ----------------------------------------------------------
    public IReadOnlyList<ImageView> Images { get; private set; } = [];
    public Dictionary<long, DupeInfo> DupeOf { get; private set; } = [];
    public HashSet<long> Selected { get; } = [];
    public long? LastClickedId { get; private set; }

    public string? ScannedFolder { get; private set; }
    public string Status { get; private set; } = "";

    // ---- Busy / progress -------------------------------------------------
    // Centralised here so every operation reports the same way and the
    // Progress<T>-vs-completion race is guarded in exactly one place.
    public bool Busy { get; private set; }
    public string? BusyLabel { get; private set; }
    public int BusyDone { get; private set; }
    public int BusyTotal { get; private set; }

    /// <summary>Null while the total is still unknown — the bar renders indeterminate.</summary>
    public double? BusyFraction =>
        BusyTotal > 0 ? Math.Clamp((double)BusyDone / BusyTotal, 0, 1) : null;

    // ---- Toast -----------------------------------------------------------
    public Toast? CurrentToast { get; private set; }
    int _toastSeq;

    public void ShowToast(string message, string? actionLabel = null, Func<Task>? action = null)
    {
        CurrentToast = new Toast(message, actionLabel, action);
        var seq = ++_toastSeq;
        Notify();
        _ = AutoDismiss(seq);
    }

    async Task AutoDismiss(int seq)
    {
        // Long enough to actually reach for Undo after a destructive action.
        await Task.Delay(TimeSpan.FromSeconds(14));
        if (_toastSeq == seq && CurrentToast is not null)
        {
            CurrentToast = null;
            Notify();
        }
    }

    public void DismissToast()
    {
        _toastSeq++;
        CurrentToast = null;
        Notify();
    }

    // ---- Filters -----------------------------------------------------------
    // Setters invalidate the memoised filter result and notify, so bindings stay
    // a plain @bind with no @bind:after bookkeeping at every call site.
    double _nsfwMin;
    int _blurMax;
    bool _dupesOnly;
    string _folder = "";
    string _year = "";

    public double NsfwMin { get => _nsfwMin; set => SetFilter(ref _nsfwMin, value); }
    public int BlurMax { get => _blurMax; set => SetFilter(ref _blurMax, value); }
    public bool DupesOnly { get => _dupesOnly; set => SetFilter(ref _dupesOnly, value); }
    public string Folder { get => _folder; set => SetFilter(ref _folder, value ?? ""); }
    public string Year { get => _year; set => SetFilter(ref _year, value ?? ""); }

    void SetFilter<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        _filtered = null;
        Notify();
    }

    public bool HasActiveFilter =>
        _nsfwMin > 0 || _blurMax > 0 || _dupesOnly || _folder.Length > 0 || _year.Length > 0;

    public void ClearFilters()
    {
        _nsfwMin = 0;
        _blurMax = 0;
        _dupesOnly = false;
        _folder = "";
        _year = "";
        _filtered = null;
        Notify();
    }

    // ---- Facets (recomputed on load) ----------------------------------
    public IReadOnlyList<string> Folders { get; private set; } = [];
    public IReadOnlyList<int> Years { get; private set; } = [];

    /// <summary>Folder hierarchy with per-step completion, rebuilt on each load.</summary>
    public FolderNode? Tree { get; private set; }

    // ---- Load --------------------------------------------------------------
    public Task LoadAsync()
    {
        var groups = new DupeRepository(catalog.Db).Groups();
        var dupeOf = new Dictionary<long, DupeInfo>();
        foreach (var g in groups)
            foreach (var m in g.Members)
                dupeOf[m.ImageId] = new DupeInfo(g.Id, g.Kind, m.IsKeeper);

        Images = catalog.Images.All()
            .Select(r => new ImageView(r, dupeOf.GetValueOrDefault(r.Id)))
            .ToList();
        DupeOf = dupeOf;

        Folders = Images.Select(i => i.Folder).Distinct()
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        Years = Images.Select(i => i.Year).Where(y => y is not null).Select(y => y!.Value)
            .Distinct().OrderByDescending(y => y).ToList();

        Tree = FolderTreeBuilder.Build(Images, DupeOf);

        // Drop selections and filter values that no longer refer to anything. The folder filter
        // is a prefix, so it stays valid as long as some folder still sits underneath it.
        var live = Images.Select(i => i.Id).ToHashSet();
        Selected.RemoveWhere(id => !live.Contains(id));
        if (_folder.Length > 0 &&
            !Folders.Any(f => f == _folder || f.StartsWith(_folder + "/", StringComparison.Ordinal)))
            _folder = "";
        if (_year.Length > 0 && !Years.Any(y => y.ToString() == _year)) _year = "";

        _filtered = null;
        Notify();
        return Task.CompletedTask;
    }

    // ---- Filtering -----------------------------------------------------
    List<ImageView>? _filtered;

    public bool Matches(ImageView img)
    {
        if (_nsfwMin > 0 && !(img.NsfwScore is { } n && n >= _nsfwMin)) return false;
        // Blurrier-than filter: lower blur score = blurrier, so show scores <= threshold.
        if (_blurMax > 0 && !(img.BlurScore is { } b && b <= _blurMax)) return false;
        if (_dupesOnly && !DupeOf.ContainsKey(img.Id)) return false;
        // Prefix match, guarded by the separator so "/a/b" never swallows "/a/bc". Selecting a
        // parent folder therefore includes everything beneath it; exact matching used to mean
        // a folder holding only subfolders matched nothing at all.
        if (_folder.Length > 0 &&
            !(img.Folder == _folder || img.Folder.StartsWith(_folder + "/", StringComparison.Ordinal)))
            return false;
        if (_year.Length > 0 && img.Year?.ToString() != _year) return false;
        return true;
    }

    /// <summary>The visible set, in grid order. Memoised — this runs on every render of
    /// the grid, the summary and the selection ops, over the whole library.</summary>
    public List<ImageView> Filtered() => _filtered ??= Images.Where(Matches).ToList();

    // ---- Selection -------------------------------------------------------
    public void Toggle(long id)
    {
        if (!Selected.Remove(id)) Selected.Add(id);
        LastClickedId = id;
        Notify();
    }

    public void SelectRange(long fromId, long toId, IReadOnlyList<long> visibleOrder)
    {
        var a = IndexOf(visibleOrder, fromId);
        var b = IndexOf(visibleOrder, toId);
        if (a < 0 || b < 0) { Toggle(toId); return; }
        var (lo, hi) = a < b ? (a, b) : (b, a);
        for (var i = lo; i <= hi; i++) Selected.Add(visibleOrder[i]);
        LastClickedId = toId;
        Notify();
    }

    static int IndexOf(IReadOnlyList<long> list, long id)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i] == id) return i;
        return -1;
    }

    public void SelectAllVisible()
    {
        foreach (var img in Filtered()) Selected.Add(img.Id);
        Notify();
    }

    public void SelectVisibleFolder()
    {
        var visible = Filtered();
        var target = _folder.Length > 0 ? _folder : visible.Count > 0 ? visible[0].Folder : "";
        foreach (var img in visible)
            if (img.Folder == target) Selected.Add(img.Id);
        Notify();
    }

    /// <summary>Select every non-keeper across all duplicate groups (not just the visible set) —
    /// the one-click "delete extras, keep best".</summary>
    public void SelectDupeExtras()
    {
        foreach (var (id, d) in DupeOf)
            if (!d.IsKeeper) Selected.Add(id);
        Notify();
    }

    /// <summary>Flip selection within the visible set — useful after selecting the keepers.</summary>
    public void InvertVisibleSelection()
    {
        foreach (var img in Filtered())
        {
            if (!Selected.Remove(img.Id)) Selected.Add(img.Id);
        }
        Notify();
    }

    public void ClearSelection()
    {
        Selected.Clear();
        LastClickedId = null;
        Notify();
    }

    // ---- Operations --------------------------------------------------------

    /// <summary>Files the last scan could not take, grouped so the user knows where to look.</summary>
    public IReadOnlyList<ScanFailure> LastFailures { get; private set; } = [];

    public void ClearFailures()
    {
        LastFailures = [];
        Notify();
    }

    public async Task ScanAsync(string folder)
    {
        if (Busy) return;
        await RunAsync("Scanning", async report =>
        {
            var progress = new Progress<ScanProgress>(p => report(p.Seen, 0, null));
            try
            {
                var result = await catalog.ScanAsync(folder, progress, CancellationToken.None);
                ScannedFolder = folder;
                LastFailures = result.Failures;
                await LoadAsync();

                var msg = $"Scanned {result.Analyzed} new, {result.Cached} cached";
                if (result.Failed > 0)
                {
                    var folders = result.Failures.Select(f => f.Folder).Distinct().Count();
                    msg += $" · {result.Failed} skipped"
                         + (folders > 1 ? $" across {folders} folders" : "");
                }
                return msg;
            }
            catch (DirectoryNotFoundException)
            {
                return $"Folder not found: {folder}";
            }
        });
    }

    public async Task DedupAsync(string folder)
    {
        if (Busy) return;
        await RunAsync("Finding duplicates", async _ =>
        {
            var report = await new DuplicateService(catalog.Db).DetectAsync(folder);
            await LoadAsync();
            return report.CzkawkaAvailable
                ? $"{report.ExactGroups} exact, {report.SimilarGroups} similar groups"
                : $"{report.ExactGroups} exact groups (similar-image tool not installed)";
        });
    }

    public async Task NsfwAsync()
    {
        if (Busy) return;
        await RunAsync("Scoring NSFW", async report =>
        {
            var progress = new Progress<NsfwProgress>(p => report(p.Done, p.Total, null));
            var result = await new NsfwScorer(catalog.Db, catalog.NsfwModelPath)
                .ScoreAllAsync(progress);
            await LoadAsync();
            return result.ModelAvailable
                ? $"Scored {result.Scored}" + (result.Failed > 0 ? $", {result.Failed} failed" : "")
                : "NSFW model not installed — scoring skipped";
        });
    }

    /// <summary>
    /// Runs one long operation with consistent busy/progress/status handling. The reporter is
    /// ignored once the body returns, so a late Progress&lt;T&gt; post cannot overwrite the
    /// final status message.
    /// </summary>
    async Task RunAsync(string label, Func<Action<int, int, string?>, Task<string>> body)
    {
        Busy = true;
        BusyLabel = label;
        BusyDone = 0;
        BusyTotal = 0;
        Status = "";
        Notify();

        var live = true;
        void Report(int done, int total, string? _)
        {
            if (!live) return;
            BusyDone = done;
            BusyTotal = total;
            Notify();
        }

        try
        {
            Status = await body(Report);
        }
        catch (Exception ex)
        {
            Status = $"Failed: {ex.Message}";
        }
        finally
        {
            live = false;
            Busy = false;
            BusyLabel = null;
            Notify();
        }
    }

    public async Task DeleteAsync(IReadOnlyList<long> imageIds)
    {
        if (Busy || imageIds.Count == 0) return;

        var result = await new DeleteService(catalog.Db, trash).RecycleAsync(imageIds);
        Selected.ExceptWith(imageIds);
        await LoadAsync();

        Status = $"Recycled {result.Recycled}" + (result.Failed > 0 ? $", {result.Failed} failed" : "");

        // The common case is "undo what I just did" — offer it inline rather than
        // making the user open the history dialog and find the batch.
        var n = result.Recycled;
        ShowToast(
            $"Recycled {n} photo{(n == 1 ? "" : "s")} to the Recycle Bin.",
            "Undo",
            () => RestoreAndReloadAsync(result.BatchId));
    }

    public IReadOnlyList<UndoBatch> Batches() => new DeleteService(catalog.Db, trash).Batches();

    /// <summary>Restore a batch, then reconcile the catalog so restored files reappear.</summary>
    public async Task<RestoreResult> RestoreAndReloadAsync(string batchId)
    {
        var result = await new DeleteService(catalog.Db, trash).RestoreAsync(batchId);
        Status = $"Restored {result.Restored}" + (result.Missing > 0 ? $", {result.Missing} missing" : "");

        // Restored files are back on disk but were removed from the catalog on delete —
        // re-scan so they reappear in the grid with fresh signals.
        if (ScannedFolder is { } folder && Directory.Exists(folder))
        {
            var scanned = await catalog.ScanAsync(folder, null, CancellationToken.None);
            _ = scanned;
        }
        await LoadAsync();
        return result;
    }
}
