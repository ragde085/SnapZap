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

/// <summary>What the grid is ordered by. <see cref="Scanned"/> is the catalog's own order,
/// which is how the grid behaved before sorting existed.</summary>
public enum SortKey { Scanned, Captured, Name, Size, Blur, Nsfw }

/// <summary>
/// Scoped (one per circuit) view-model + operations for the whole app. Replaces the `state`
/// object and top-level functions in the old app.js. Components subscribe to <see cref="Changed"/>
/// and re-render; this class never touches the DOM/UI directly.
/// </summary>
public sealed class AppState(CatalogService catalog, ITrashService trash, SessionStore session)
{
    public event Action? Changed;

    public void Notify() => Changed?.Invoke();

    /// <summary>
    /// Notify *and* record that the selection moved. Kept separate from <see cref="Notify"/>
    /// because Notify also fires for filters, toasts and — critically — every progress tick of
    /// a scan. Doing this work there meant one full re-serialisation of the selection and one
    /// O(n) memo invalidation per file scanned.
    /// </summary>
    void NotifySelectionChanged()
    {
        _selectedBytes = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Rebuild the folder tree after the detector settings changed.
    /// </summary>
    /// <remarks>
    /// The tree counts a row as checked only when the run that stamped it covered every currently
    /// enabled detector, so switching one on has to re-derive it. Without this the checkbox in
    /// Setup and the pending dots in the tree disagree until the next scan — and the dots are the
    /// only thing telling the user there is now work outstanding.
    /// </remarks>
    public void NotifyDedupSettingsChanged()
    {
        Tree = FolderTreeBuilder.Build(Images, DupeOf, DedupSettings.Load(catalog.Db).CoveredKinds);
        Changed?.Invoke();
    }

    /// <summary>Adopt a selection change made by another window and re-render.</summary>
    public void RefreshSelection()
    {
        _selectedBytes = null;
        Changed?.Invoke();
    }

    // ---- Data ----------------------------------------------------------
    public IReadOnlyList<ImageView> Images { get; private set; } = [];
    public Dictionary<long, DupeInfo> DupeOf { get; private set; } = [];

    /// <summary>Duplicate groups with their members, for the review flow.</summary>
    public IReadOnlyList<DupeGroup> DupeGroups { get; private set; } = [];

    /// <summary>Bytes held by duplicate extras — what removing them would give back. This is
    /// the number that makes the whole exercise feel worth doing, so it is computed on load
    /// rather than left for the user to infer from a count.</summary>
    public long ReclaimableBytes { get; private set; }

    public int DupeExtraCount { get; private set; }

    public string FormattedReclaimable => FormatBytes(ReclaimableBytes);

    /// <summary>Bytes held by the current selection — what a delete would actually free.</summary>
    public long SelectedBytes => _selectedBytes ??=
        Images.Where(i => Selected.Contains(i.Id)).Sum(i => i.FileSize);

    public string FormattedSelectedBytes => FormatBytes(SelectedBytes);

    long? _selectedBytes;

    /// <summary>Id lookup built once per load. Rebuilding it per call made the duplicate
    /// review scan the whole library a dozen times on every render.</summary>
    Dictionary<long, ImageView> _byId = [];

    /// <summary>Human-readable size, e.g. "14.2 GB".</summary>
    public static string FormatBytes(long b) => b switch
    {
        >= 1_000_000_000 => $"{b / 1e9:F1} GB",
        >= 1_000_000 => $"{b / 1e6:F0} MB",
        >= 1_000 => $"{b / 1e3:F0} KB",
        _ => $"{b} bytes",
    };
    /// <summary>The shared selection. Owned by <see cref="SessionStore"/> so every window on
    /// this library sees the same working set; read-only from here.</summary>
    public HashSet<long> Selected => session.Current;
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

    /// <summary>
    /// What the current step is doing right now, when it can say — the file being hashed, the line
    /// czkawka just printed. Null when the operation has nothing more specific than its label.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="BusyLabel"/>, which names the whole operation and must stay stable
    /// while this changes underneath it.
    /// </remarks>
    public string? BusyDetail { get; private set; }

    CancellationTokenSource? _cts;

    /// <summary>True while an operation is running that hasn't already been asked to stop.</summary>
    public bool CanCancel => Busy && _cts is { IsCancellationRequested: false };

    /// <summary>Ask the running operation to stop. Work already committed is kept.</summary>
    public void Cancel()
    {
        if (_cts is { IsCancellationRequested: false })
        {
            _cts.Cancel();
            BusyLabel = "Stopping";
            Notify();
        }
    }

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
    NsfwFilter _nsfw = NsfwFilter.Any;
    int _blurMax;
    bool _dupesOnly;
    string _folder = "";
    string _year = "";

    /// <summary>
    /// Which explicit-content band the grid is limited to. Named states rather than a 0–1
    /// slider: the model's output is bimodal, so a hundred stops between 0 and 1 offered
    /// ninety-eight positions that select either everything or nothing, and "NSFW at least
    /// any" was not a sentence.
    /// </summary>
    public NsfwFilter Nsfw { get => _nsfw; set => SetFilter(ref _nsfw, value); }
    public int BlurMax { get => _blurMax; set => SetFilter(ref _blurMax, value); }
    public bool DupesOnly { get => _dupesOnly; set => SetFilter(ref _dupesOnly, value); }
    public string Folder { get => _folder; set => SetFilter(ref _folder, value ?? ""); }
    public string Year { get => _year; set => SetFilter(ref _year, value ?? ""); }

    // ---- Preview ------------------------------------------------------------
    bool _previewDetails;

    /// <summary>
    /// Whether the preview's details panel is open. Off by default so a photo opens at the
    /// largest size the window allows; kept here rather than in the modal so the choice
    /// survives closing and reopening — someone reading EXIF on one photo is reading it on
    /// the next, and re-opening the panel for every shot would be its own chore.
    /// </summary>
    public bool PreviewDetails
    {
        get => _previewDetails;
        set { if (_previewDetails == value) return; _previewDetails = value; Notify(); }
    }

    /// <summary>
    /// How explicit the model thinks a photo is, as something a person can act on.
    /// </summary>
    /// <remarks>
    /// The raw score is a softmax output, not a calibrated probability, and DESIGN §8 records
    /// that no NSFW-labelled fixtures exist — nothing has ever validated the model's judgement,
    /// only that SnapZap reproduces it. A bare "0.62" invites arithmetic the number cannot
    /// support ("twice as bad as 0.31"); a band invites the only sound response, which is to
    /// look at the ones it flagged.
    /// </remarks>
    public enum NsfwBand
    {
        /// <summary>Never scored. Emphatically not the same as "clean".</summary>
        Unchecked,
        Clean,
        Unsure,
        Likely,
    }

    /// <summary>The score at or above which a photo counts as explicit. Follows the filter once
    /// the user picks a stricter one, so the badge and the filter can never disagree.</summary>
    public double NsfwThreshold => ImageView.NsfwFlagThreshold;

    public NsfwBand BandOf(ImageView img) => img.NsfwScore switch
    {
        null => NsfwBand.Unchecked,
        var n when n >= NsfwThreshold => NsfwBand.Likely,
        var n when n >= ImageView.NsfwUnsureThreshold => NsfwBand.Unsure,
        _ => NsfwBand.Clean,
    };

    /// <summary>Plain-language name for a band; the primary reading everywhere a score shows.</summary>
    public static string BandLabel(NsfwBand band) => band switch
    {
        NsfwBand.Likely => "Likely explicit",
        NsfwBand.Unsure => "Not sure",
        NsfwBand.Clean => "Looks clean",
        _ => "Not checked",
    };

    /// <summary>
    /// The blur score at or below which a photo is badged as soft.
    /// </summary>
    /// <remarks>
    /// Follows the filter slider once the user has moved it. The badge and the filter were two
    /// unrelated numbers — a fixed 100 for the badge against a 0–300 slider — so filtering to
    /// "blurrier than 150" produced a grid of photos with no soft-focus badge on any of them,
    /// each one apparently contradicting the filter that selected it.
    /// </remarks>
    public double SoftThreshold => _blurMax > 0 ? _blurMax : ImageView.BlurFlagThreshold;

    /// <summary>Whether this photo reads as soft at the threshold currently in force.</summary>
    public bool IsSoft(ImageView img) => img.BlurScore is { } b && b <= SoftThreshold;

    bool MatchesNsfw(NsfwBand band) => _nsfw switch
    {
        NsfwFilter.Likely => band == NsfwBand.Likely,
        NsfwFilter.Review => band is NsfwBand.Likely or NsfwBand.Unsure,
        NsfwFilter.Clean => band == NsfwBand.Clean,
        NsfwFilter.Unchecked => band == NsfwBand.Unchecked,
        _ => true,
    };

    /// <summary>How many photos currently sit in each band, for the filter's own labels — a
    /// choice that selects nothing should say so before it is chosen.</summary>
    /// <remarks>
    /// Cached per load, not counted per call. The summary reads it on every render and the
    /// filter popover six times, while a scan notifies once per file — so counting live made a
    /// 40k-photo scan do 40k passes over 40k photos on the circuit's own thread.
    /// </remarks>
    public int CountInBand(NsfwBand band) => _bandCounts.GetValueOrDefault(band);

    Dictionary<NsfwBand, int> _bandCounts = [];

    void SetFilter<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        _filtered = null;
        Notify();
    }

    // ---- Sort ---------------------------------------------------------------
    SortKey _sort = SortKey.Scanned;
    bool _sortDescending;

    public SortKey Sort { get => _sort; set => SetFilter(ref _sort, value); }
    public bool SortDescending { get => _sortDescending; set => SetFilter(ref _sortDescending, value); }

    public bool HasActiveFilter =>
        _nsfw != NsfwFilter.Any || _blurMax > 0 || _dupesOnly || _folder.Length > 0 || _year.Length > 0;

    public void ClearFilters()
    {
        _nsfw = NsfwFilter.Any;
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
        // Only the folder the session is scoped to. The catalogue keeps every folder ever
        // scanned as cache; loading all of it put photos from unrelated libraries in the grid
        // and, worse, inside the reach of "select everything shown" → Delete.
        // Recover the working folder across restarts: it is what export's mirror mode and the
        // post-restore re-scan need, and it is already recorded in the catalogue.
        ScannedFolder ??= catalog.ScanRoot;

        var records = catalog.Images.Under(catalog.ScanRoot).ToList();
        var present = records.Select(r => r.Id).ToHashSet();

        // Groups are stored as they were detected, but members get recycled afterwards. A group
        // with one surviving copy is no longer a duplicate of anything: it must not be counted
        // in "5 duplicate groups", stepped through in the review, or badged on the card.
        var groups = new DupeRepository(catalog.Db).Groups()
            .Select(g => g with { Members = g.Members.Where(m => present.Contains(m.ImageId)).ToList() })
            .Where(g => g.Members.Count >= 2)
            .ToList();

        var dupeOf = new Dictionary<long, DupeInfo>();
        foreach (var g in groups)
        {
            // If the keeper itself was recycled, every survivor still reads IsKeeper=false —
            // so they all counted as reclaimable extras, and "select all extras" would arm a
            // delete against every remaining copy of the photo. A group always keeps one.
            var members = g.Members;
            var keeperId = members.FirstOrDefault(m => m.IsKeeper)?.ImageId
                        ?? members.OrderBy(m => m.ImageId).First().ImageId;

            foreach (var m in members)
                dupeOf[m.ImageId] = new DupeInfo(g.Id, g.Kind, m.ImageId == keeperId);
        }

        Images = records.Select(r => new ImageView(r, dupeOf.GetValueOrDefault(r.Id))).ToList();
        _bandCounts = Images.GroupBy(BandOf).ToDictionary(g => g.Key, g => g.Count());
        DupeOf = dupeOf;
        DupeGroups = groups;
        _byId = Images.ToDictionary(i => i.Id);

        Folders = Images.Select(i => i.Folder).Distinct()
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        Years = Images.Select(i => i.Year).Where(y => y is not null).Select(y => y!.Value)
            .Distinct().OrderByDescending(y => y).ToList();

        // Only non-keepers are reclaimable, and only in kinds a bulk action would actually take.
        // Counting burst frames here would advertise space that "Select duplicate extras" is
        // deliberately never going to free, and send the user hunting for the missing gigabytes.
        var extras = Images
            .Where(i => dupeOf.TryGetValue(i.Id, out var d) && !d.IsKeeper && d.Kind.IsBulkSelectable())
            .ToList();
        DupeExtraCount = extras.Count;
        ReclaimableBytes = extras.Sum(i => i.FileSize);

        // Built against the detectors that are on right now, not against whatever was on when the
        // rows were stamped — that difference is exactly what re-flags folders as pending.
        Tree = FolderTreeBuilder.Build(Images, DupeOf, DedupSettings.Load(catalog.Db).CoveredKinds);

        var live = Images.Select(i => i.Id).ToHashSet();

        // The store seeds itself from disk once per process, so a reload simply re-attaches to
        // the live selection. All that's needed here is dropping ids the catalog no longer has.
        if (Selected.Any(id => !live.Contains(id)))
            session.Mutate(sel => sel.RemoveWhere(id => !live.Contains(id)));
        if (_folder.Length > 0 &&
            !Folders.Any(f => f == _folder || f.StartsWith(_folder + "/", StringComparison.Ordinal)))
            _folder = "";
        if (_year.Length > 0 && !Years.Any(y => y.ToString() == _year)) _year = "";

        _filtered = null;
        // LoadAsync both replaces Images and prunes/restores Selected, so the selection memo
        // must be invalidated and the (possibly pruned) selection persisted.
        NotifySelectionChanged();
        return Task.CompletedTask;
    }

    // ---- Filtering -----------------------------------------------------
    List<ImageView>? _filtered;

    public bool Matches(ImageView img)
    {
        // Matches on the band, not the raw number, so what the filter selects is exactly what
        // the badge and the preview call it. Unchecked is its own state: filtering for "looks
        // clean" used to quietly include every photo nobody had scored yet.
        if (_nsfw != NsfwFilter.Any && !MatchesNsfw(BandOf(img))) return false;
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
    public List<ImageView> Filtered() => _filtered ??= ApplySort(Images.Where(Matches)).ToList();

    /// <summary>
    /// Photos missing the value being sorted on always sort last, whichever direction is
    /// chosen — an undated photo is not "earliest", it's unknown, and burying it under a
    /// date sort would be misleading.
    /// </summary>
    IEnumerable<ImageView> ApplySort(IEnumerable<ImageView> items)
    {
        var desc = _sortDescending;

        return _sort switch
        {
            SortKey.Scanned => desc ? items.OrderByDescending(i => i.Id) : items.OrderBy(i => i.Id),
            SortKey.Name => desc
                ? items.OrderByDescending(i => i.FileName, StringComparer.OrdinalIgnoreCase)
                : items.OrderBy(i => i.FileName, StringComparer.OrdinalIgnoreCase),
            SortKey.Size => desc ? items.OrderByDescending(i => i.FileSize) : items.OrderBy(i => i.FileSize),
            SortKey.Captured => Nulls(items, i => i.ExifTaken, desc),
            SortKey.Blur => Nulls(items, i => i.BlurScore, desc),
            SortKey.Nsfw => Nulls(items, i => i.NsfwScore, desc),
            _ => items,
        };

        static IOrderedEnumerable<ImageView> Nulls<T>(
            IEnumerable<ImageView> src, Func<ImageView, T?> key, bool desc) where T : struct
        {
            var known = src.OrderBy(i => key(i) is null);   // false (has value) sorts first
            return desc
                ? known.ThenByDescending(i => key(i))
                : known.ThenBy(i => key(i));
        }
    }

    // ---- Selection -------------------------------------------------------
    public void Toggle(long id)
    {
        session.Mutate(sel => { if (!sel.Remove(id)) sel.Add(id); });
        LastClickedId = id;
        NotifySelectionChanged();
    }

    public void SelectRange(long fromId, long toId, IReadOnlyList<long> visibleOrder)
    {
        var a = IndexOf(visibleOrder, fromId);
        var b = IndexOf(visibleOrder, toId);
        if (a < 0 || b < 0) { Toggle(toId); return; }
        var (lo, hi) = a < b ? (a, b) : (b, a);
        session.Mutate(sel => { for (var i = lo; i <= hi; i++) sel.Add(visibleOrder[i]); });
        LastClickedId = toId;
        NotifySelectionChanged();
    }

    static int IndexOf(IReadOnlyList<long> list, long id)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i] == id) return i;
        return -1;
    }

    public void SelectAllVisible()
    {
        var visibleNow = Filtered();
        session.Mutate(sel => { foreach (var img in visibleNow) sel.Add(img.Id); });
        NotifySelectionChanged();
    }

    /// <summary>
    /// Resolve a group's members to the loaded view models, in a stable order.
    ///
    /// Deliberately not keeper-first: the review compares copies side by side, and re-sorting
    /// on every pick would make the card you just chose jump position under the cursor.
    /// Sorting by path keeps the layout still so only the badge moves.
    /// </summary>
    public IReadOnlyList<ImageView> MembersOf(DupeGroup group) =>
        group.Members
            .Where(m => _byId.ContainsKey(m.ImageId))
            .Select(m => _byId[m.ImageId])
            .OrderBy(i => i.Path, StringComparer.Ordinal)
            .ToList();

    /// <summary>Override which member of a group survives an export.</summary>
    public async Task SetKeeperAsync(long groupId, long imageId)
    {
        new DupeRepository(catalog.Db).SetKeeper(groupId, imageId);
        await LoadAsync();
    }

    /// <summary>Select exactly the non-keepers across all duplicate groups — the one-click
    /// "delete extras, keep best". Replaces the selection rather than adding to it, so the
    /// count on the button is the count you end up with.</summary>
    /// <summary>
    /// Select every non-keeper in every group whose kind is safe to sweep in bulk.
    /// </summary>
    /// <remarks>
    /// <b>Safety-critical.</b> The filter to <see cref="DupeKindExtensions.IsBulkSelectable"/> is
    /// what makes it possible to widen duplicate detection at all. Exact and Variant groups are
    /// copies of one photograph, so selecting the extras is exactly what the user asked for. A
    /// Burst group is five *different photographs* of one moment, and only one of them is the shot
    /// where the kid was smiling — sweeping those into a delete would make this a shredder rather
    /// than a triage tool. Burst members stay individually selectable; they are simply never
    /// selected wholesale.
    ///
    /// The rule lives on <see cref="DupeKindExtensions"/>, not inline here, so a fourth kind added
    /// later cannot become bulk-selectable by default just because nobody thought about it.
    /// </remarks>
    public void SelectDupeExtras()
    {
        var dupes = DupeOf;
        session.Mutate(sel =>
        {
            sel.Clear();
            foreach (var (id, d) in dupes)
                if (!d.IsKeeper && d.Kind.IsBulkSelectable()) sel.Add(id);
        });
        NotifySelectionChanged();
    }

    /// <summary>Flip selection within the visible set — useful after selecting the keepers.</summary>
    public void InvertVisibleSelection()
    {
        var visibleNow = Filtered();
        session.Mutate(sel =>
        {
            foreach (var img in visibleNow)
                if (!sel.Remove(img.Id)) sel.Add(img.Id);
        });
        NotifySelectionChanged();
    }

    public void ClearSelection()
    {
        session.Mutate(sel => sel.Clear());
        LastClickedId = null;
        NotifySelectionChanged();
    }

    // ---- Operations --------------------------------------------------------

    /// <summary>Files the last scan could not take, grouped so the user knows where to look.</summary>
    public IReadOnlyList<ScanFailure> LastFailures { get; private set; } = [];

    /// <summary>Formats the last scan recognised but cannot decode, counted by format.</summary>
    public IReadOnlyDictionary<string, int> LastUnsupported { get; private set; }
        = new Dictionary<string, int>();

    public int LastUnsupportedTotal => LastUnsupported.Values.Sum();

    public void ClearFailures()
    {
        LastFailures = [];
        LastUnsupported = new Dictionary<string, int>();
        Notify();
    }

    /// <summary>
    /// Empty the catalogue: every photo's hash, scores, EXIF, thumbnail and duplicate grouping,
    /// for every folder ever scanned. The photos themselves are not touched, and the undo log
    /// survives so anything already in the Recycle Bin can still be restored from History.
    /// </summary>
    public async Task ForgetEverythingAsync()
    {
        if (Busy) return;

        // Through RunAsync like every other operation: it rewrites the whole database file, so
        // it needs the same busy state, the same render-before-work yield and the same error
        // containment. Doing it inline reintroduced exactly the frozen-but-still-clickable
        // window that the NSFW model load had just been fixed for.
        await RunAsync("Emptying the catalogue", async (_, _) =>
        {
            await Task.Run(catalog.Forget);
            ScannedFolder = null;
            LastFailures = [];
            LastUnsupported = new Dictionary<string, int>();
            ClearFilters();
            session.Mutate(sel => sel.Clear());
            await LoadAsync();
            return "Catalogue emptied. Scan a folder to start again.";
        });
    }

    public async Task ScanAsync(string folder)
    {
        if (Busy) return;
        await RunAsync("Scanning", async (report, ct) =>
        {
            var progress = new Progress<ScanProgress>(p => report(p.Seen, 0, null));
            try
            {
                var result = await catalog.ScanAsync(folder, progress, ct);
                ScannedFolder = folder;
                LastFailures = result.Failures;
                LastUnsupported = result.UnsupportedByFormat;
                await LoadAsync();

                var msg = $"Scanned {result.Analyzed} new, {result.Cached} cached";
                if (result.Failed > 0)
                {
                    var folders = result.Failures.Select(f => f.Folder).Distinct().Count();
                    msg += $" · {result.Failed} skipped"
                         + (folders > 1 ? $" across {folders} folders" : "");
                }
                if (result.UnsupportedTotal > 0)
                    msg += $" · {result.UnsupportedTotal:N0} in formats SnapZap can't read yet";

                // Pruning drops catalog rows for files that have left the folder. That is the
                // right behaviour, but it also drops their scores and keeper decisions, so it
                // has to be said out loud rather than shown as a silently shorter grid.
                if (result.Pruned > 0)
                    msg += $" · {result.Pruned:N0} no longer in the folder, removed from the catalogue";

                return msg;
            }
            catch (DirectoryNotFoundException)
            {
                return $"Folder not found: {folder}";
            }
        });
    }

    /// <summary>Detect duplicates in the folder currently on screen.</summary>
    public async Task DedupAsync()
    {
        if (Busy || ScannedFolder is not { } folder) return;
        await RunAsync("Finding duplicates", async (reportProgress, ct) =>
        {
            var progress = new Progress<DedupProgress>(p => reportProgress(p.Done, p.Total, p.Detail));
            var report = await new DuplicateService(catalog.Db).DetectAsync(folder, progress, ct);
            await LoadAsync();

            // Counted from the loaded, scoped groups rather than the detectors' own tallies,
            // so the sentence agrees with the grid behind it.
            var counts = new List<string>();
            foreach (var kind in (ReadOnlySpan<DupeKind>)[DupeKind.Exact, DupeKind.Variant, DupeKind.Burst])
            {
                var n = DupeGroups.Count(g => g.Kind == kind);
                if (n > 0) counts.Add($"{n:N0} {kind.Label().ToLowerInvariant()}");
            }

            var headline = counts.Count == 0 ? "No duplicates found" : string.Join(", ", counts) + " groups";
            // The note carries the partial-result warnings — unhashed photos, a truncated
            // comparison, detectors switched off. Appending it rather than replacing the count is
            // the point: "0 groups" and "0 groups, but half the library has no signature" have to
            // look different.
            return report.Note is null ? headline : $"{headline} · {report.Note}";
        });
    }

    public async Task NsfwAsync()
    {
        if (Busy) return;
        await RunAsync("Scoring NSFW", async (report, ct) =>
        {
            var progress = new Progress<NsfwProgress>(p => report(p.Done, p.Total, null));
            var result = await new NsfwScorer(catalog.Db, catalog.NsfwModelPath)
                .ScoreAllAsync(catalog.ScanRoot, progress, ct: ct);
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
    async Task RunAsync(string label, Func<Action<int, int, string?>, CancellationToken, Task<string>> body)
    {
        Busy = true;
        BusyLabel = label;
        BusyDone = 0;
        BusyTotal = 0;
        BusyDetail = null;
        Status = "";
        _cts = new CancellationTokenSource();
        Notify();

        // Let that render reach the browser before the work begins. Notify only queues it, and
        // an operation whose first stretch is synchronous — NSFW loads a 328 MB model before
        // its first await — holds the circuit long enough that the buttons it just disabled
        // stayed clickable, so a second press queued a second run.
        await Task.Yield();

        var live = true;
        void Report(int done, int total, string? detail)
        {
            if (!live) return;
            BusyDone = done;
            BusyTotal = total;
            BusyDetail = detail;
            Notify();
        }

        try
        {
            Status = await body(Report, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Everything analysed before the stop is already committed, and the tier-1 cache
            // means re-running skips it — so a cancelled scan is progress, not lost work.
            await LoadAsync();
            Status = $"{label} stopped after {BusyDone:N0} of {BusyTotal:N0}";
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
            BusyDetail = null;
            _cts?.Dispose();
            _cts = null;
            Notify();
        }
    }

    public async Task DeleteAsync(IReadOnlyList<long> imageIds)
    {
        if (Busy || imageIds.Count == 0) return;

        // Goes through the shared busy path like every other long operation: recycling
        // thousands of photos used to show nothing at all and left the button double-clickable.
        Busy = true;
        BusyLabel = "Moving to the Recycle Bin";
        BusyDone = 0;
        BusyTotal = imageIds.Count;
        Notify();

        DeleteResult result;
        try
        {
            result = await new DeleteService(catalog.Db, trash).RecycleAsync(imageIds);
        }
        finally
        {
            Busy = false;
            BusyLabel = null;
        }

        session.Mutate(sel => sel.ExceptWith(imageIds));
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

/// <summary>The explicit-content states the grid can be limited to.</summary>
public enum NsfwFilter
{
    Any,
    /// <summary>Only what the model called explicit.</summary>
    Likely,
    /// <summary>Explicit plus the ones it was unsure about — the review queue.</summary>
    Review,
    Clean,
    /// <summary>Never scored. Its own state, because "not asked" is not "safe".</summary>
    Unchecked,
}
