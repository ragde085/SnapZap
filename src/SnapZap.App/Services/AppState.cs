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
public sealed class AppState(CatalogService catalog, ITrashService trash, SessionStore session, DependencyChecker deps)
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

    /// <summary>Estimated time remaining for the current run, from a rolling recent-throughput
    /// window (see <see cref="EtaEstimator"/>). Null until enough progress has been observed.</summary>
    public TimeSpan? BusyEta { get; private set; }

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
    bool _includeSubfolders = true;

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

    /// <summary>
    /// Whether the chosen folder also brings in everything beneath it. On by default, which is
    /// how the folder facet has always behaved.
    /// </summary>
    /// <remarks>
    /// This is the capability the deleted "This folder" command used to provide: it matched
    /// <c>img.Folder == target</c> exactly while the facet matches by prefix, so for any folder
    /// with subfolders — the normal case — the two selected different sets, and after the
    /// command went nothing in the app could express "this directory only". Expressed as a
    /// filter rather than a selector it composes with every other facet and with every
    /// selection command, instead of being a second, parallel notion of folder scope.
    /// </remarks>
    public bool IncludeSubfolders
    {
        get => _includeSubfolders;
        set => SetFilter(ref _includeSubfolders, value);
    }

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

    /// <summary>
    /// Which band a photo falls in. Delegates the rule to <see cref="NsfwDecision"/> rather than
    /// comparing scores here: a tiled score can flag a photo whose whole-frame score is well
    /// under the threshold, and a band computed from the whole-frame number alone would disagree
    /// with the filter that selected it.
    /// </summary>
    public NsfwBand BandOf(ImageView img)
    {
        if (img.NsfwScore is null) return NsfwBand.Unchecked;
        if (NsfwDecision.IsExplicit(img.NsfwScore, img.NsfwTileMean)) return NsfwBand.Likely;
        return NsfwDecision.IsUnsure(img.NsfwScore, img.NsfwTileMean) ? NsfwBand.Unsure : NsfwBand.Clean;
    }

    /// <summary>
    /// The numbers behind a band, in one line, for the tooltip and the preview panel.
    /// </summary>
    /// <remarks>
    /// Shared so the two can't drift, and written out in full because the short version was
    /// actively misleading: a photo scored 0.40 whole-frame displayed "0.40 of 1.00 · flagged at
    /// 0.85", which reads as "this was nearly flagged" when 0.40 is what the model gives any
    /// photograph of a person. Naming both numbers and both cutoffs is the only version that
    /// explains a flag the whole-frame score alone would not account for.
    /// </remarks>
    public static string NsfwEvidence(ImageView img)
    {
        if (img.NsfwScore is not { } whole) return "Not scored yet.";

        var closer = img.NsfwTileMean;
        if (closer is null)
            return $"{whole:F2} of 1.00 · flagged at {NsfwDecision.WholeFlagThreshold:F2} and above";

        var flaggedCloser = closer >= NsfwDecision.TileMeanFlagThreshold;
        return $"{whole:F2} looking at the whole photo, {closer:F2} looking closer"
            + (flaggedCloser ? " — flagged on the closer look" : "")
            + $" · flagged at {NsfwDecision.WholeFlagThreshold:F2} overall"
            + $" or {NsfwDecision.TileMeanFlagThreshold:F2} closer";
    }

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
        InvalidateFiltered();
        Notify();
    }

    /// <summary>
    /// Drop everything derived from the visible set. One place, so a new derived cache cannot be
    /// added and then forgotten at one of the several sites that change what is visible.
    /// </summary>
    void InvalidateFiltered()
    {
        _filtered = null;
        _selectableCounts = null;
    }

    // ---- Sort ---------------------------------------------------------------
    SortKey _sort = SortKey.Scanned;
    bool _sortDescending;

    public SortKey Sort { get => _sort; set => SetSort(ref _sort, value); }
    public bool SortDescending { get => _sortDescending; set => SetSort(ref _sortDescending, value); }

    /// <summary>
    /// Re-sort without recounting. Order changes cannot change which photos match, so the
    /// selection counts are still correct — and they are not free: the blur slider is bound
    /// <c>@bind:event="oninput"</c>, so a single drag runs the filter path dozens of times, and
    /// rebuilding five tallies over the whole visible set on each one is exactly the per-render
    /// cost the count cache exists to avoid.
    /// </summary>
    void SetSort<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        _filtered = null;          // deliberately NOT _selectableCounts
        Notify();
    }

    public bool HasActiveFilter =>
        _nsfw != NsfwFilter.Any || _blurMax > 0 || _dupesOnly || _folder.Length > 0
        || _year.Length > 0 || !_includeSubfolders;

    public void ClearFilters()
    {
        _nsfw = NsfwFilter.Any;
        _blurMax = 0;
        _dupesOnly = false;
        _folder = "";
        _year = "";
        _includeSubfolders = true;
        InvalidateFiltered();
        Notify();
    }

    // ---- Facets (recomputed on load) ----------------------------------
    public IReadOnlyList<string> Folders { get; private set; } = [];
    public IReadOnlyList<int> Years { get; private set; } = [];

    /// <summary>Folder hierarchy with per-step completion, rebuilt on each load.</summary>
    public FolderNode? Tree { get; private set; }

    /// <summary>Everything <see cref="LoadAsync"/> computes off-thread: pure reads and LINQ
    /// passes over the catalogue, with no dependency on (or mutation of) other <see cref="AppState"/>
    /// fields, so it is safe to run on a background thread.</summary>
    sealed record LoadSnapshot(
        IReadOnlyList<ImageView> Images,
        Dictionary<NsfwBand, int> BandCounts,
        Dictionary<long, DupeInfo> DupeOf,
        IReadOnlyList<DupeGroup> DupeGroups,
        Dictionary<long, ImageView> ById,
        IReadOnlyList<string> Folders,
        IReadOnlyList<int> Years,
        int DupeExtraCount,
        long ReclaimableBytes,
        FolderNode? Tree);

    /// <summary>The cached thumbnail file's own last-write time, or 0 when there is none to stat
    /// — see <see cref="ImageView.ThumbUrl"/>.</summary>
    static long ThumbGenerationOf(ImageRecord r)
    {
        if (r.ThumbPath is null) return 0;
        try { return new FileInfo(r.ThumbPath).LastWriteTimeUtc.Ticks; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    LoadSnapshot BuildLoadSnapshot(string? scanRoot)
    {
        var records = catalog.Images.Under(scanRoot).ToList();
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
            // If every keeper in the group was recycled, every survivor still reads
            // IsKeeper=false — so they all counted as reclaimable extras, and "select all
            // extras" would arm a delete against every remaining copy of the photo. A group
            // always keeps at least one. More than one member may be flagged is_keeper (a burst
            // can have two frames worth keeping — see ToggleKeeper), so this is a set, not a
            // single id.
            var members = g.Members;
            var keeperIds = members.Where(m => m.IsKeeper).Select(m => m.ImageId).ToHashSet();
            if (keeperIds.Count == 0)
                keeperIds.Add(members.OrderBy(m => m.ImageId).First().ImageId);

            foreach (var m in members)
                dupeOf[m.ImageId] = new DupeInfo(g.Id, g.Kind, keeperIds.Contains(m.ImageId));
        }

        var images = records
            .Select(r => new ImageView(r, dupeOf.GetValueOrDefault(r.Id), ThumbGenerationOf(r)))
            .ToList();
        var bandCounts = images.GroupBy(BandOf).ToDictionary(g => g.Key, g => g.Count());
        var byId = images.ToDictionary(i => i.Id);

        var folders = images.Select(i => i.Folder).Distinct()
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        var years = images.Select(i => i.Year).Where(y => y is not null).Select(y => y!.Value)
            .Distinct().OrderByDescending(y => y).ToList();

        // Only non-keepers are reclaimable, and only in kinds a bulk action would actually take.
        // Counting burst frames here would advertise space that "Select duplicate extras" is
        // deliberately never going to free, and send the user hunting for the missing gigabytes.
        var extras = images
            .Where(i => dupeOf.TryGetValue(i.Id, out var d) && !d.IsKeeper && d.Kind.IsBulkSelectable())
            .ToList();

        // Built against the detectors that are on right now, not against whatever was on when the
        // rows were stamped — that difference is exactly what re-flags folders as pending.
        var tree = FolderTreeBuilder.Build(images, dupeOf, DedupSettings.Load(catalog.Db).CoveredKinds);

        return new LoadSnapshot(images, bandCounts, dupeOf, groups, byId, folders, years,
                                 extras.Count, extras.Sum(i => i.FileSize), tree);
    }

    // ---- Load --------------------------------------------------------------
    public async Task LoadAsync()
    {
        // Only the folder the session is scoped to. The catalogue keeps every folder ever
        // scanned as cache; loading all of it put photos from unrelated libraries in the grid
        // and, worse, inside the reach of "select everything shown" → Delete.
        // Recover the working folder across restarts: it is what export's mirror mode and the
        // post-restore re-scan need, and it is already recorded in the catalogue.
        ScannedFolder ??= catalog.ScanRoot;
        var scanRoot = catalog.ScanRoot;

        // The catalogue read, the group read and the dozen-odd whole-library LINQ passes all
        // happen here, off the circuit thread — previously they ran inline and returned
        // Task.CompletedTask despite the signature. Mutating AppState's own fields happens only
        // after this await, back on the circuit (Blazor Server's SynchronizationContext resumes
        // the continuation there), so subscribed components never observe a torn mix of old and
        // new state.
        var snapshot = await Task.Run(() => BuildLoadSnapshot(scanRoot));

        Images = snapshot.Images;
        _bandCounts = snapshot.BandCounts;
        DupeOf = snapshot.DupeOf;
        DupeGroups = snapshot.DupeGroups;
        _byId = snapshot.ById;
        Folders = snapshot.Folders;
        Years = snapshot.Years;
        DupeExtraCount = snapshot.DupeExtraCount;
        ReclaimableBytes = snapshot.ReclaimableBytes;
        Tree = snapshot.Tree;

        var live = Images.Select(i => i.Id).ToHashSet();

        // The store seeds itself from disk once per process, so a reload simply re-attaches to
        // the live selection. All that's needed here is dropping ids the catalog no longer has.
        if (Selected.Any(id => !live.Contains(id)))
            session.Mutate(sel => sel.RemoveWhere(id => !live.Contains(id)));
        if (_folder.Length > 0 &&
            !Folders.Any(f => f == _folder || f.StartsWith(_folder + "/", StringComparison.Ordinal)))
            _folder = "";
        if (_year.Length > 0 && !Years.Any(y => y.ToString() == _year)) _year = "";

        InvalidateFiltered();
        // LoadAsync both replaces Images and prunes/restores Selected, so the selection memo
        // must be invalidated and the (possibly pruned) selection persisted.
        NotifySelectionChanged();

        // One-shot, process-wide: surfaces the backup path a signature-recipe migration took
        // (AC 6a — "reported to the user", not just written to a log nobody reads). Checked on
        // every load rather than only the first one in this circuit, since it's the first *tab*
        // to load after the migration that should show it, and TakePendingRecipeMigrationNotice
        // itself guarantees only one of them wins the race.
        if (catalog.TakePendingRecipeMigrationNotice() is { } notice)
            ShowToast(notice);
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
        if (!InFolderScope(img)) return false;
        if (_year.Length > 0 && img.Year?.ToString() != _year) return false;
        return true;
    }

    /// <summary>
    /// Prefix match by default, guarded by the separator so "/a/b" never swallows "/a/bc".
    /// Selecting a parent folder therefore includes everything beneath it; exact matching for
    /// everyone used to mean a folder holding only subfolders matched nothing at all.
    /// IncludeSubfolders off narrows it back to the one directory — see the property.
    /// True (no restriction) when no folder is focused, i.e. "All folders".
    /// </summary>
    bool InFolderScope(ImageView img) =>
        _folder.Length == 0 ||
        (_includeSubfolders
            ? img.Folder == _folder || img.Folder.StartsWith(_folder + "/", StringComparison.Ordinal)
            : img.Folder == _folder);

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
            // The score the flag was decided on, not the whole-frame one: sorting by the latter
            // would bury a photo flagged on its tiles below every unflagged photo above it.
            SortKey.Nsfw => Nulls(items, i => i.NsfwDisplayScore, desc),
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

    /// <summary>A named set of photos the user can select in one action.</summary>
    public enum SelectionScope
    {
        AllShown,
        LikelyExplicit,
        NotSure,
        DuplicateExtras,
        DuplicateKeepers,
    }

    /// <summary>Allocated once: <see cref="CountBands"/> walks this per photo.</summary>
    static readonly SelectionScope[] AllScopes = Enum.GetValues<SelectionScope>();

    /// <summary>
    /// Whether one photo belongs to a named set. Reads <see cref="BandOf"/> and the resolved
    /// <see cref="ImageView.Dupe"/> rather than re-deriving either, so a selection command can
    /// never disagree with the badge on the card it selects.
    /// </summary>
    /// <remarks>
    /// <b>Safety-critical, in <see cref="SelectionScope.DuplicateExtras"/>.</b> The filter to
    /// <see cref="DupeKindExtensions.IsBulkSelectable"/> is what makes it possible to widen
    /// duplicate detection at all. Exact and Variant groups are copies of one photograph, so
    /// selecting the extras is exactly what the user asked for. A Burst group is five *different
    /// photographs* of one moment, and only one of them is the shot where the kid was smiling —
    /// sweeping those into a delete would make this a shredder rather than a triage tool. Burst
    /// members stay individually selectable; they are simply never selected wholesale.
    ///
    /// The rule lives on <see cref="DupeKindExtensions"/>, not inline here, so a fourth kind added
    /// later cannot become bulk-selectable by default just because nobody thought about it. It sits
    /// on the predicate rather than on the command so that the count on the button, the bytes
    /// beside it and what pressing it selects cannot drift apart — they all read this one method.
    ///
    /// <see cref="SelectionScope.DuplicateKeepers"/> is deliberately unfiltered: selecting keepers
    /// is how you export the survivors, and a burst's keeper is as much a survivor as any other.
    /// </remarks>
    bool InScope(SelectionScope scope, ImageView img) => scope switch
    {
        SelectionScope.AllShown => true,
        SelectionScope.LikelyExplicit => BandOf(img) == NsfwBand.Likely,
        SelectionScope.NotSure => BandOf(img) == NsfwBand.Unsure,
        SelectionScope.DuplicateExtras => img.Dupe is { IsKeeper: false } d && d.Kind.IsBulkSelectable(),
        SelectionScope.DuplicateKeepers => img.Dupe is { IsKeeper: true },
        _ => false,
    };

    /// <summary>
    /// Replace the selection with everything visible that matches.
    /// </summary>
    /// <remarks>
    /// Two rules, and both are load-bearing. Every predicate walks <see cref="Filtered"/>, so a
    /// command can never select a photo the grid is not showing — the same property catalogue
    /// scoping protects one layer up, and the reason it matters is that the selection is what
    /// Delete is aimed at. And every predicate <em>replaces</em> rather than adds, so the number
    /// on the button is the number you end up with. The two used to disagree:
    /// <c>SelectAllVisible</c> added and was filter-aware, <c>SelectDupeExtras</c> replaced and
    /// walked the whole scope, so with a filter on it armed Delete over photos off screen.
    /// </remarks>
    public void SelectBy(SelectionScope scope)
    {
        var matches = Filtered().Where(i => InScope(scope, i)).Select(i => i.Id).ToList();
        session.Mutate(sel =>
        {
            sel.Clear();
            foreach (var id in matches) sel.Add(id);
        });
        NotifySelectionChanged();
    }

    /// <summary>
    /// How many visible photos a command would select. Equal by construction to what pressing it
    /// does, because both read the same predicate over the same set.
    /// </summary>
    public int SelectableCount(SelectionScope scope) => Tally()[scope].Count;

    /// <summary>
    /// What those photos weigh — the number that turns "select 412 extras" into a decision.
    /// </summary>
    /// <remarks>
    /// Counted over the same filtered set as <see cref="SelectableCount"/> rather than reusing
    /// <see cref="ReclaimableBytes"/>, which is keyed on the whole scope. A button reading
    /// "Extras (3) — 14.2 GB reclaimable" because three of four hundred extras are on screen is
    /// the same count-and-action disagreement this whole selection layer exists to remove.
    /// </remarks>
    public string FormattedSelectableBytes(SelectionScope scope) => FormatBytes(Tally()[scope].Bytes);

    /// <summary>
    /// Whether the visible non-keepers include burst frames the Extras command will not take.
    /// </summary>
    /// <remarks>
    /// Purely for wording. The bulk-selectable rule is enforced in <see cref="InScope"/>; this only
    /// lets the UI say <em>why</em> a non-keeper on screen is not in the Extras count. Without it
    /// the honest zero-state is "no duplicates match the current filters", which is false — the
    /// duplicates are right there — and sends the user clearing filters that are not the cause.
    /// </remarks>
    public bool HasBurstOnlyExtras =>
        Filtered().Any(i => i.Dupe is { IsKeeper: false } d && !d.Kind.IsBulkSelectable());

    Dictionary<SelectionScope, (int Count, long Bytes)>? _selectableCounts;

    Dictionary<SelectionScope, (int Count, long Bytes)> Tally() => _selectableCounts ??= CountScopes();

    /// <summary>
    /// One pass with a tally, not one pass per scope. Cached rather than counted per call for the
    /// same reason as <see cref="_bandCounts"/>: six buttons read these on every render while a
    /// scan notifies once per file.
    /// </summary>
    Dictionary<SelectionScope, (int Count, long Bytes)> CountScopes()
    {
        var counts = AllScopes.ToDictionary(s => s, _ => (Count: 0, Bytes: 0L));
        foreach (var img in Filtered())
            foreach (var scope in AllScopes)
                if (InScope(scope, img))
                {
                    var (n, bytes) = counts[scope];
                    counts[scope] = (n + 1, bytes + img.FileSize);
                }
        return counts;
    }

    /// <summary>Kept as the name <c>PhotoGrid</c>'s [JSInvokable] wrapper reaches for ⌘A.</summary>
    public void SelectAllVisible() => SelectBy(SelectionScope.AllShown);

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

    /// <summary>
    /// Flip whether one member of a group survives an export. More than one member of a group
    /// may be a keeper at once — a burst might have two frames worth keeping — and a group
    /// always keeps at least one, so toggling off the last remaining keeper is a no-op.
    /// </summary>
    public async Task ToggleKeeperAsync(long groupId, long imageId)
    {
        new DupeRepository(catalog.Db).ToggleKeeper(groupId, imageId);
        await LoadAsync();
    }

    /// <summary>Flip selection within the visible set — useful after selecting the keepers.
    /// A relative operation on an existing selection rather than a predicate, so the
    /// replace rule in <see cref="SelectBy"/> deliberately does not apply.</summary>
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

    /// <summary>
    /// Expand a leading <c>~</c> to the user's home directory.
    /// </summary>
    /// <remarks>
    /// The folder box is a text field, so people type what they would type in a shell. Nothing
    /// downstream understands <c>~</c> — <see cref="Directory"/> treats it as a relative path —
    /// so <c>~/Photos</c> came back "Folder not found" for a folder that plainly exists, which
    /// reads as the app being broken rather than as the path needing a different spelling.
    ///
    /// Only a leading <c>~/</c> (or a bare <c>~</c>) is touched. A directory genuinely called
    /// <c>~snapshots</c> is a real thing on some backup tools and must keep working.
    /// </remarks>
    internal static string ExpandHome(string folder)
    {
        var f = folder.Trim();
        if (f == "~") return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!f.StartsWith("~/") && !f.StartsWith(@"~\")) return f;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), f[2..]);
    }

    public async Task ScanAsync(string folder)
    {
        if (Busy) return;
        folder = ExpandHome(folder);
        await RunAsync("Scanning", async (report, ct) =>
        {
            var progress = new Progress<ScanProgress>(p => report(p.Seen, p.Total, null));
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
            var report = await new DuplicateService(catalog.Db, catalog.Imaging, catalog.ThumbDir)
                .DetectAsync(folder, progress, ct);
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

        // Selection wins over the folder focused in the tree; with neither set ("All folders",
        // nothing selected) this falls through to the whole catalogue via ScanRoot. Captured now,
        // not read again once the run starts, so the scope can't drift if the user changes the
        // selection or folder while a previous scope's scan is still in flight.
        //
        // Deliberately InFolderScope only, not the full Filtered() (which also applies the year
        // filter etc.) — a year filter narrows what's *shown*, but scoring is additive metadata,
        // not a destructive/selective action, so "the folder" scores every year in it. If that
        // ever needs to match the grid's active filters exactly, swap this for Filtered().
        IReadOnlyList<long>? scope = Selected.Count > 0
            ? Selected.ToList()
            : _folder.Length > 0
                ? Images.Where(InFolderScope).Select(i => i.Id).ToList()
                : null;

        await RunAsync("Scoring NSFW", async (report, ct) =>
        {
            var progress = new Progress<NsfwProgress>(p => report(p.Done, p.Total, null));
            // Read once per run, not per photo: changing the setting mid-run would otherwise
            // leave half the folder scored one way and half the other, with nothing recording
            // which was which.
            var depth = deps.NsfwDepth;
            var result = await new NsfwScorer(catalog.Db, catalog.NsfwModelPath)
                .ScoreAllAsync(catalog.ScanRoot, progress, imageIds: scope, depth: depth, ct: ct);
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
        BusyEta = null;
        Status = "";
        _cts = new CancellationTokenSource();
        Notify();

        // Let that render reach the browser before the work begins. Notify only queues it, and
        // an operation whose first stretch is synchronous — NSFW loads a 328 MB model before
        // its first await — holds the circuit long enough that the buttons it just disabled
        // stayed clickable, so a second press queued a second run.
        await Task.Yield();

        var live = true;
        var eta = new EtaEstimator();
        void Report(int done, int total, string? detail)
        {
            if (!live) return;
            BusyDone = done;
            BusyTotal = total;
            BusyDetail = detail;
            BusyEta = eta.Estimate(Environment.TickCount64, done, total);
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
            // Total can still be 0 here — e.g. Stop lands while Scanner is enumerating the
            // folder, before file count is even known — so "of 0" would misreport, not just
            // look odd.
            Status = BusyTotal > 0
                ? $"{label} stopped after {BusyDone:N0} of {BusyTotal:N0}"
                : $"{label} stopped after {BusyDone:N0}";
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
            BusyEta = null;
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
