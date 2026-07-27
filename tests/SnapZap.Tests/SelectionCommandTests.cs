using SnapZap.App;
using SnapZap.App.Services;
using SnapZap.Core;
using SnapZap.Core.Data;
using SnapZap.Core.Platform;
using Xunit;
using Scope = SnapZap.App.Services.AppState.SelectionScope;

namespace SnapZap.Tests;

/// <summary>
/// The selection commands decide what Export and Delete are aimed at, so their failure mode is
/// not a wrong number on a button — it is a delete over photos the user cannot see. Two rules
/// carry that weight and every test here is one of them: a command acts on the visible set, and
/// the number on the button is the number you end up with.
/// </summary>
public class SelectionCommandTests : IDisposable
{
    readonly string _work = Path.Combine(Path.GetTempPath(), "pc_sel_" + Guid.NewGuid().ToString("N"));
    readonly CatalogService _catalog;
    readonly SessionStore _session;
    readonly AppState _state;

    public SelectionCommandTests()
    {
        Directory.CreateDirectory(_work);
        _catalog = new CatalogService(_work);
        _session = new SessionStore(_catalog, TimeSpan.FromMilliseconds(50));
        _state = new AppState(_catalog, new MacOsTrashService(), _session, new DependencyChecker(_catalog));
    }

    /// <summary>One photo, with only the signals these tests actually steer on.</summary>
    long Add(string name, double? nsfw = null, long? taken = null, string? folder = null, long size = 1000)
    {
        var dir = folder is null ? _work : Path.Combine(_work, folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, name);
        return _catalog.Images.Upsert(new ImageRecord
        {
            Path = path,
            FileSize = size,
            Mtime = 0,
            ContentHash = "h-" + name,
            AnalyzedAt = 1,
            NsfwScore = nsfw,
            ExifTaken = taken,
        });
    }

    void Group(params (long Id, bool Keeper)[] members) => Group(DupeKind.Exact, members);

    void Group(DupeKind kind, params (long Id, bool Keeper)[] members) =>
        new DupeRepository(_catalog.Db).AddGroup(kind, null, members.ToList());

    /// <summary>Unix seconds inside the given year, for the year facet.</summary>
    static long In(int year) => new DateTimeOffset(year, 6, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    // ---- Membership: each predicate selects exactly its own set --------------

    [Fact]
    public async Task Flagged_and_not_sure_select_their_own_band_and_nothing_else()
    {
        // Either side of both boundaries, plus the unscored case, reusing the values
        // NsfwBandTests already pins.
        var clean = Add("clean.jpg", 0.44);
        var unsureLow = Add("unsure-low.jpg", 0.45);
        var unsureHigh = Add("unsure-high.jpg", 0.84);
        var likely = Add("likely.jpg", 0.85);
        var unchecked_ = Add("unchecked.jpg");

        await _state.LoadAsync();

        _state.SelectBy(Scope.LikelyExplicit);
        Assert.Equal([likely], _state.Selected.Order());

        _state.SelectBy(Scope.NotSure);
        Assert.Equal([unsureLow, unsureHigh], _state.Selected.Order());

        // Neither band ever reaches "looks clean" or "not checked" — the conflation the bands
        // were introduced to remove.
        Assert.DoesNotContain(clean, _state.Selected);
        Assert.DoesNotContain(unchecked_, _state.Selected);
    }

    [Fact]
    public async Task Extras_and_keepers_partition_the_duplicate_members()
    {
        var keepA = Add("keepA.jpg");
        var extraA1 = Add("extraA1.jpg");
        var extraA2 = Add("extraA2.jpg");
        var keepB = Add("keepB.jpg");
        var extraB = Add("extraB.jpg");
        var loner = Add("loner.jpg");
        Group((keepA, true), (extraA1, false), (extraA2, false));
        Group((keepB, true), (extraB, false));

        await _state.LoadAsync();

        _state.SelectBy(Scope.DuplicateExtras);
        var extras = _state.Selected.ToHashSet();
        _state.SelectBy(Scope.DuplicateKeepers);
        var keepers = _state.Selected.ToHashSet();

        Assert.Equal([extraA1, extraA2, extraB], extras.Order());
        Assert.Equal([keepA, keepB], keepers.Order());          // exactly one per group
        Assert.Empty(extras.Intersect(keepers));                 // disjoint
        Assert.Equal(5, extras.Count + keepers.Count);           // and together, every member
        Assert.DoesNotContain(loner, extras);
        Assert.DoesNotContain(loner, keepers);
    }

    // ---- The filter rule -----------------------------------------------------

    /// <summary>
    /// The defect this whole change exists to fix. The old SelectDupeExtras walked the DupeOf
    /// dictionary — scope-limited but not filter-limited — so with a band filter on it selected
    /// extras that were not on screen, and the selection bar then offered Delete over them.
    /// Verified red against the pre-fix code: it selected the hidden extra as well.
    /// </summary>
    [Fact]
    public async Task Extras_never_reaches_past_the_active_filter()
    {
        var keepA = Add("keepA.jpg", 0.9);
        var extraA = Add("extraA.jpg", 0.9);
        var keepB = Add("keepB.jpg", 0.01);
        var hiddenExtra = Add("extraB.jpg", 0.01);
        Group((keepA, true), (extraA, false));
        Group((keepB, true), (hiddenExtra, false));

        await _state.LoadAsync();
        _state.Nsfw = NsfwFilter.Likely;        // only the explicit pair is on screen

        _state.SelectBy(Scope.DuplicateExtras);

        Assert.Equal([extraA], _state.Selected.Order());
        Assert.DoesNotContain(hiddenExtra, _state.Selected);
    }

    // ---- The folder box ------------------------------------------------------

    /// <summary>
    /// The folder box is a text field, so people type shell paths into it. Nothing downstream
    /// understands <c>~</c>, so <c>~/Photos</c> reported "Folder not found" for a folder that
    /// plainly exists — which reads as the app being broken, not as a spelling problem.
    /// </summary>
    [Theory]
    [InlineData("~")]
    [InlineData("~/Pictures")]
    [InlineData("~/Pictures/2024 holiday")]
    public void Tilde_paths_resolve_under_the_home_directory(string typed)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expanded = AppState.ExpandHome(typed);

        Assert.StartsWith(home, expanded);
        Assert.DoesNotContain('~', expanded);
    }

    /// <summary>
    /// Only a leading <c>~/</c> is a home reference. Folders really are called things like
    /// <c>~snapshots</c>, and an absolute path must survive untouched.
    /// </summary>
    [Theory]
    [InlineData("/Volumes/Archive/Photos")]
    [InlineData("~snapshots")]
    [InlineData("/tmp/a~b")]
    [InlineData("relative/path")]
    public void Non_home_paths_are_left_alone(string typed) =>
        Assert.Equal(typed, AppState.ExpandHome(typed));

    [Fact]
    public void Surrounding_whitespace_is_trimmed_off_a_pasted_path() =>
        Assert.Equal("/Volumes/Archive", AppState.ExpandHome("  /Volumes/Archive  "));

    // ---- The bulk-selectable rule --------------------------------------------

    /// <summary>
    /// The invariant that lets duplicate detection widen at all. A Burst group is five *different
    /// photographs* of one moment, so sweeping its non-keepers into a selection that Delete is
    /// aimed at would make this a shredder rather than a triage tool. Exact and Variant groups are
    /// copies of one photograph and stay bulk-selectable.
    /// </summary>
    [Fact]
    public async Task Extras_never_selects_a_burst_frame_in_bulk()
    {
        var keepExact = Add("keep-exact.jpg");
        var extraExact = Add("extra-exact.jpg");
        var keepVariant = Add("keep-variant.jpg");
        var extraVariant = Add("extra-variant.jpg");
        var keepBurst = Add("keep-burst.jpg");
        var burstFrame = Add("burst-frame.jpg");
        Group(DupeKind.Exact, (keepExact, true), (extraExact, false));
        Group(DupeKind.Variant, (keepVariant, true), (extraVariant, false));
        Group(DupeKind.Burst, (keepBurst, true), (burstFrame, false));

        await _state.LoadAsync();

        _state.SelectBy(Scope.DuplicateExtras);

        Assert.Equal([extraExact, extraVariant], _state.Selected.Order());
        Assert.DoesNotContain(burstFrame, _state.Selected);
    }

    /// <summary>
    /// The count and the bytes beside it read the same predicate as the command, so the rule
    /// cannot hold for what the button does while the label still advertises the burst frames.
    /// That disagreement is the one this selection layer exists to remove.
    /// </summary>
    [Fact]
    public async Task Burst_frames_are_absent_from_the_extras_count_and_bytes_too()
    {
        var keepExact = Add("keep-exact.jpg", size: 100);
        var extraExact = Add("extra-exact.jpg", size: 2000);
        var keepBurst = Add("keep-burst.jpg", size: 100);
        var burstFrame = Add("burst-frame.jpg", size: 5000);
        Group(DupeKind.Exact, (keepExact, true), (extraExact, false));
        Group(DupeKind.Burst, (keepBurst, true), (burstFrame, false));

        await _state.LoadAsync();

        Assert.Equal(1, _state.SelectableCount(Scope.DuplicateExtras));
        Assert.Equal(AppState.FormatBytes(2000), _state.FormattedSelectableBytes(Scope.DuplicateExtras));

        // The library-wide figure the Extras button is *not* allowed to borrow excludes them on
        // the same rule, so neither number sends the user hunting for the missing gigabytes.
        Assert.Equal(2000, _state.ReclaimableBytes);
    }

    /// <summary>
    /// Burst members stay individually selectable — the rule withholds them from bulk actions,
    /// it does not put them out of the user's reach. Their keeper is a survivor like any other,
    /// so selecting keepers to export deliberately does not filter by kind.
    /// </summary>
    [Fact]
    public async Task Burst_members_remain_reachable_by_hand_and_as_keepers()
    {
        var keepBurst = Add("keep-burst.jpg");
        var burstFrame = Add("burst-frame.jpg");
        Group(DupeKind.Burst, (keepBurst, true), (burstFrame, false));

        await _state.LoadAsync();

        _state.Toggle(burstFrame);
        Assert.Equal([burstFrame], _state.Selected.Order());

        _state.SelectBy(Scope.DuplicateKeepers);
        Assert.Equal([keepBurst], _state.Selected.Order());
    }

    /// <summary>
    /// Distinguishes "no duplicates here" from "duplicates here, none of them copies" — the two
    /// states a zero on the Extras button can mean once bursts exist, and the second one is the
    /// state whose obvious message is false.
    /// </summary>
    [Fact]
    public async Task Burst_only_extras_are_distinguishable_from_having_no_extras_at_all()
    {
        // Two folders so a filter can take the burst off screen without touching the exact pair.
        var keepBurst = Add("keep-burst.jpg", folder: "burst");
        var burstFrame = Add("burst-frame.jpg", folder: "burst");
        var keepExact = Add("keep-exact.jpg", folder: "copies");
        var extraExact = Add("extra-exact.jpg", folder: "copies");
        Group(DupeKind.Burst, (keepBurst, true), (burstFrame, false));
        Group(DupeKind.Exact, (keepExact, true), (extraExact, false));

        await _state.LoadAsync();

        // The burst frame is on screen and is a non-keeper the Extras command will not take.
        Assert.True(_state.HasBurstOnlyExtras);
        Assert.Equal(1, _state.SelectableCount(Scope.DuplicateExtras));

        // Filtered to the copies: every visible non-keeper is now selectable, so the UI must fall
        // back to its ordinary wording rather than blaming bursts that are no longer shown.
        _state.Folder = Path.Combine(_work, "copies");
        Assert.False(_state.HasBurstOnlyExtras);
        Assert.Equal(1, _state.SelectableCount(Scope.DuplicateExtras));

        // Filtered to the burst: a zero on the button that is emphatically not "no duplicates".
        _state.Folder = Path.Combine(_work, "burst");
        Assert.True(_state.HasBurstOnlyExtras);
        Assert.Equal(0, _state.SelectableCount(Scope.DuplicateExtras));
        Assert.NotEmpty(_state.DupeGroups);
    }

    [Fact]
    public async Task A_library_of_only_bulk_selectable_extras_reports_no_burst_leftovers()
    {
        var keepExact = Add("keep-exact.jpg");
        var extraExact = Add("extra-exact.jpg");
        Group(DupeKind.Exact, (keepExact, true), (extraExact, false));

        await _state.LoadAsync();

        Assert.False(_state.HasBurstOnlyExtras);
        Assert.Equal(1, _state.SelectableCount(Scope.DuplicateExtras));
    }

    [Theory]
    [InlineData(Scope.AllShown)]
    [InlineData(Scope.LikelyExplicit)]
    [InlineData(Scope.NotSure)]
    [InlineData(Scope.DuplicateExtras)]
    [InlineData(Scope.DuplicateKeepers)]
    public async Task No_command_can_select_a_photo_the_grid_is_not_showing(Scope scope)
    {
        var keep2020 = Add("k20.jpg", 0.9, In(2020));
        var extra2020 = Add("e20.jpg", 0.5, In(2020));
        var keep2021 = Add("k21.jpg", 0.9, In(2021));
        var extra2021 = Add("e21.jpg", 0.5, In(2021));
        Group((keep2020, true), (extra2020, false));
        Group((keep2021, true), (extra2021, false));

        await _state.LoadAsync();
        _state.Year = "2020";

        _state.SelectBy(scope);

        var visible = _state.Filtered().Select(i => i.Id).ToHashSet();
        Assert.All(_state.Selected, id => Assert.Contains(id, visible));
    }

    // ---- The replace rule ----------------------------------------------------

    [Theory]
    [InlineData(Scope.AllShown)]
    [InlineData(Scope.LikelyExplicit)]
    [InlineData(Scope.NotSure)]
    [InlineData(Scope.DuplicateExtras)]
    [InlineData(Scope.DuplicateKeepers)]
    public async Task The_count_on_the_button_is_the_count_you_end_up_with(Scope scope)
    {
        var keep = Add("keep.jpg", 0.9);
        var extra = Add("extra.jpg", 0.5);
        Add("clean.jpg", 0.01);
        Add("unscored.jpg");
        Group((keep, true), (extra, false));

        await _state.LoadAsync();

        // Something already picked by hand, and deliberately outside most of the predicates.
        _state.Toggle(Add("byhand.jpg"));
        await _state.LoadAsync();

        var promised = _state.SelectableCount(scope);
        _state.SelectBy(scope);

        Assert.Equal(promised, _state.Selected.Count);
    }

    [Fact]
    public async Task A_predicate_replaces_the_hand_made_selection_rather_than_adding_to_it()
    {
        var flagged = Add("flagged.jpg", 0.9);
        var byHand = Add("byhand.jpg", 0.01);

        await _state.LoadAsync();
        _state.Toggle(byHand);
        Assert.Contains(byHand, _state.Selected);

        _state.SelectBy(Scope.LikelyExplicit);

        Assert.Equal([flagged], _state.Selected.Order());
        Assert.DoesNotContain(byHand, _state.Selected);
    }

    /// <summary>
    /// ⌘A routes through PhotoGrid's [JSInvokable] wrapper into this method, and Task 3 changed
    /// what it does — it replaces now instead of adding. The shortcut must still select the
    /// visible set.
    /// </summary>
    [Fact]
    public async Task Select_all_visible_still_selects_everything_shown_after_becoming_a_replace()
    {
        var a = Add("a.jpg");
        var b = Add("b.jpg");

        await _state.LoadAsync();
        _state.Toggle(a);

        _state.SelectAllVisible();

        Assert.Equal([a, b], _state.Selected.Order());
    }

    /// <summary>Invert, Toggle and SelectRange are relative operations, not predicates, so the
    /// replace rule deliberately does not reach them.</summary>
    [Fact]
    public async Task Invert_still_flips_within_the_visible_set()
    {
        var a = Add("a.jpg");
        var b = Add("b.jpg");

        await _state.LoadAsync();
        _state.Toggle(a);

        _state.InvertVisibleSelection();

        Assert.Equal([b], _state.Selected.Order());
    }

    // ---- The count cache -----------------------------------------------------

    [Fact]
    public async Task Counts_follow_a_filter_change_rather_than_serving_a_stale_number()
    {
        Add("k20.jpg", 0.9, In(2020));
        Add("k21.jpg", 0.9, In(2021));

        await _state.LoadAsync();
        Assert.Equal(2, _state.SelectableCount(Scope.LikelyExplicit));   // reads, and caches

        _state.Year = "2020";

        Assert.Equal(1, _state.SelectableCount(Scope.LikelyExplicit));
    }

    /// <summary>
    /// Sorting changes order, not membership, so the counts cannot have moved — and rebuilding
    /// them on the sort path would put five tallies over the whole visible set behind the blur
    /// slider, which fires the filter path dozens of times per drag.
    /// </summary>
    [Fact]
    public async Task A_sort_change_leaves_the_counts_alone()
    {
        Add("a.jpg", 0.9);
        Add("b.jpg", 0.9);

        await _state.LoadAsync();
        Assert.Equal(2, _state.SelectableCount(Scope.LikelyExplicit));

        _state.Sort = SortKey.Name;
        _state.SortDescending = true;

        Assert.Equal(2, _state.SelectableCount(Scope.LikelyExplicit));
        Assert.Equal("b.jpg", _state.Filtered()[0].FileName);   // the sort did take effect
    }

    [Fact]
    public async Task Counts_are_rebuilt_after_a_load_replaces_the_library()
    {
        Add("a.jpg", 0.9);
        await _state.LoadAsync();
        Assert.Equal(1, _state.SelectableCount(Scope.LikelyExplicit));

        Add("b.jpg", 0.9);
        await _state.LoadAsync();

        Assert.Equal(2, _state.SelectableCount(Scope.LikelyExplicit));
    }

    // ---- Include subfolders (the restored "This folder" capability) -----------

    /// <summary>
    /// The folder facet matches by prefix so a parent brings in everything beneath it, which is
    /// what the tree exists to do. The deleted "This folder" command matched exactly, and after
    /// it went nothing could express "this directory only". A leaf folder passes either way —
    /// which is exactly how the two came to be believed equivalent — so this uses a parent with
    /// a child.
    /// </summary>
    [Fact]
    public async Task Include_subfolders_off_narrows_a_parent_folder_to_its_own_directory()
    {
        var atParent = Add("parent.jpg", folder: "lib");
        var inChild = Add("child.jpg", folder: Path.Combine("lib", "sub"));

        await _state.LoadAsync();
        _state.Folder = ImageView.FolderOf(Path.Combine(_work, "lib", "parent.jpg"));

        // On (the default): the child is in view, and All shown takes it.
        Assert.True(_state.IncludeSubfolders);
        Assert.Contains(inChild, _state.Filtered().Select(i => i.Id));
        _state.SelectBy(Scope.AllShown);
        Assert.Equal([atParent, inChild], _state.Selected.Order());

        // Off: exact match, so only the chosen directory — what "This folder" used to do.
        _state.IncludeSubfolders = false;
        Assert.DoesNotContain(inChild, _state.Filtered().Select(i => i.Id));
        _state.SelectBy(Scope.AllShown);
        Assert.Equal([atParent], _state.Selected.Order());
    }

    [Fact]
    public async Task Include_subfolders_counts_as_an_active_filter_only_while_it_is_off()
    {
        Add("a.jpg", folder: "lib");
        await _state.LoadAsync();

        Assert.False(_state.HasActiveFilter);           // on is the default, and defaults filter nothing

        _state.IncludeSubfolders = false;
        Assert.True(_state.HasActiveFilter);

        _state.ClearFilters();
        Assert.True(_state.IncludeSubfolders);          // and clearing puts it back
        Assert.False(_state.HasActiveFilter);
    }

    // ---- Bytes ---------------------------------------------------------------

    /// <summary>
    /// The reclaimable figure on the Extras button has to describe the extras the button will
    /// actually select. Reusing the library-wide ReclaimableBytes would have put "14.2 GB
    /// reclaimable" on a button that selects three photos.
    /// </summary>
    [Fact]
    public async Task Reclaimable_bytes_describe_the_visible_extras_not_the_whole_library()
    {
        var keepA = Add("keepA.jpg", 0.9, size: 100);
        var extraA = Add("extraA.jpg", 0.9, size: 2000);
        var keepB = Add("keepB.jpg", 0.01, size: 100);
        var extraB = Add("extraB.jpg", 0.01, size: 5000);
        Group((keepA, true), (extraA, false));
        Group((keepB, true), (extraB, false));

        await _state.LoadAsync();
        _state.Nsfw = NsfwFilter.Likely;

        Assert.Equal(7000, _state.ReclaimableBytes);    // the library's own total is unchanged
        Assert.Equal(AppState.FormatBytes(2000), _state.FormattedSelectableBytes(Scope.DuplicateExtras));
    }

    // ---- The empty library ---------------------------------------------------

    /// <summary>The state in which "greyed, not hidden" is on trial: every command has a count
    /// of zero, and every one still has to be a control the user can find.</summary>
    [Fact]
    public async Task Every_command_counts_zero_on_an_empty_catalogue()
    {
        await _state.LoadAsync();

        Assert.Empty(_state.Images);
        foreach (var scope in Enum.GetValues<Scope>())
            Assert.Equal(0, _state.SelectableCount(scope));
    }

    public void Dispose()
    {
        _session.Dispose();
        _catalog.Dispose();
        try { Directory.Delete(_work, recursive: true); } catch { /* temp dir */ }
    }
}
