using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using SnapZap.Core;
using SnapZap.Core.Dedup;
using SnapZap.Core.Scanning;

namespace SnapZap.Droid;

/// <summary>
/// Screens 1-4 of the mobile handoff — First run, Scanning, Library and the Plan tab — behind the
/// three-item bottom nav that carries the whole app.
/// </summary>
/// <remarks>
/// <para>The handoff rebuilds the information architecture rather than shrinking the desktop's:
/// the plan becomes a tab instead of a rail, Filters and Folders become sheets, and selection
/// becomes a mode you enter. This activity owns the shell and everything under Library and Plan;
/// Review is its own activity because it is a full-screen queue with its own back behaviour.</para>
///
/// <para>Views are built in code rather than from layout XML — one screen with several states, no
/// designer in this workflow, and a layout file would be indirection with no reader. Every
/// dimension and colour comes from <see cref="Design"/>, the single translation of the handoff's
/// CSS, so a value can be checked against its source in one place.</para>
/// </remarks>
[Activity(Label = "SnapZap", MainLauncher = true,
          Theme = "@android:style/Theme.Material.Light.NoActionBar")]
public sealed class MainActivity : Activity
{
    const string Tag = "SnapZap";

    enum Tab { Library, Review, Plan }

    AndroidCatalog? _catalog;
    LinearLayout _root = null!, _content = null!, _navHost = null!;
    Tab _tab = Tab.Library;

    bool _scanning, _hasScanned;
    string _folder = SnapZap.Core.Platform.DirectoryRoots.DefaultStart(
        SnapZap.Core.Platform.DirectoryPlatform.Android);

    ScanResult? _lastScan;
    DuplicateReport? _lastDupes;

    // Group counts read back off the catalogue, not just from this session's scan. Detection
    // results persist, so a relaunch must still show the badge and the "waiting" callout —
    // otherwise the app forgets there is work outstanding the moment it is closed.
    int _reviewable, _burstGroups;
    List<ImageRecord> _photos = [];

    // Live scan read-outs, held so progress can update them without rebuilding the screen.
    TextView? _scanCount, _scanFile;

    // Screen 9 — selection is a mode you enter, not an always-on toolbar. Holding a photo enters
    // it and the header turns to ink, so there is no doubt you are in it.
    bool _selectionMode;
    readonly HashSet<long> _selected = [];

    // Screen 7 — the active facet. Null means "Any".
    DupeFacet _facet = DupeFacet.Any;
    enum DupeFacet { Any, ExtrasOnly, KeepersOnly, BurstFrames }
    Dictionary<long, DupeAssignment> _assign = [];

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        _root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _root.SetBackgroundColor(Design.Bg);
        _root.FocusableInTouchMode = true;
        SetContentView(_root);

        // Edge-to-edge is enforced at API 36, so padding comes from real insets rather than a
        // guessed constant — cutouts and gesture bars differ per device.
        _root.SetOnApplyWindowInsetsListener(new InsetsPadder());
        _root.RequestApplyInsets();

        _content = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _content.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f);
        _root.AddView(_content);

        _navHost = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _navHost.SetBackgroundColor(Design.Surface);
        _navHost.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Design.Dp(this, 64));
        _root.AddView(_navHost);

        Render();
    }

    protected override void OnResume()
    {
        base.OnResume();
        if (!_scanning) Render();
    }

    static bool HasAllFilesAccess =>
        !OperatingSystem.IsAndroidVersionAtLeast(30) || Android.OS.Environment.IsExternalStorageManager;

    // ══════════════════════════════════════════════════════════════════════════════════════
    //  Shell
    // ══════════════════════════════════════════════════════════════════════════════════════

    void Render()
    {
        _content.RemoveAllViews();

        if (!HasAllFilesAccess)
        {
            _navHost.Visibility = ViewStates.Gone;
            RenderPermissionGate();
            return;
        }

        _catalog ??= new AndroidCatalog();
        if (!_hasScanned && !_scanning)
        {
            _photos = _catalog.Images.All().ToList();
            _hasScanned = _photos.Count > 0;
        }
        if (!_scanning) CountGroups();

        // The nav stays hidden until there is a library to navigate. First run and the scan are
        // single-purpose screens; three tabs over an empty app offer choices that all lead nowhere.
        _navHost.Visibility = (_hasScanned && !_scanning) ? ViewStates.Visible : ViewStates.Gone;

        if (_scanning) { RenderScanning(); return; }
        if (!_hasScanned) { RenderFirstRun(); return; }

        if (_tab == Tab.Plan) RenderPlan(); else RenderLibrary();
        RefreshNav();
    }

    /// <summary>
    /// Counts what is actually left to review, from the catalogue.
    /// </summary>
    /// <remarks>
    /// Counted through <c>DupeAssignmentResolver</c> and <c>IsBulkSelectableExtra</c> rather than
    /// by summing the detectors, for the same reason the desktop reads its counts back off the
    /// catalogue: a group whose members were already recycled is not work, and burst frames must
    /// not be counted as reviewable. Summing detector output would advertise work that the review
    /// queue will then refuse to show.
    /// </remarks>
    void CountGroups()
    {
        try
        {
            var groups = new SnapZap.Core.Data.DupeRepository(_catalog!.Db).Groups();
            _assign = DupeAssignmentResolver.Resolve(groups, _catalog.Images.BurstAdjacentIds());
            _reviewable = _assign.Values.Count(a => a.IsBulkSelectableExtra);
            _burstGroups = groups.Count(g => g.Kind == DupeKind.Burst);
        }
        catch (Exception ex)
        {
            _reviewable = 0; _burstGroups = 0; _assign = [];
            Android.Util.Log.Warn(Tag, $"group count failed: {ex.Message}");
        }
    }

    void RefreshNav()
    {
        _navHost.RemoveAllViews();
        _navHost.AddView(Design.Rule(this, 2f, Design.Divider));    // .nav border-top:2px

        var bar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        bar.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f);

        bar.AddView(NavItem("Library", Tab.Library, null));
        bar.AddView(NavItem("Review", Tab.Review, _reviewable));
        bar.AddView(NavItem("Plan", Tab.Plan, null));
        _navHost.AddView(bar);
    }

    View NavItem(string label, Tab tab, int? badge)
    {
        var cell = new FrameLayout(this);
        cell.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1f);

        var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
        col.LayoutParameters = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);

        var on = _tab == tab;

        // The active tab is marked by a 3px accent rule along its top edge (.nvi-on), never by a
        // filled pill — this system marks state with rules and weight, not rounded chrome.
        var topRule = new View(this);
        topRule.SetBackgroundColor(on ? Design.Accent : Color.Transparent);
        topRule.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Design.Dp(this, 3));
        col.AddView(topRule);

        var t = new TextView(this) { Text = label.ToUpperInvariant() };
        t.SetTextSize(Android.Util.ComplexUnitType.Sp, 10f);
        t.SetTypeface(null, TypefaceStyle.Bold);
        t.LetterSpacing = 0.08f;
        t.SetTextColor(on ? Design.Text : Design.Neutral700);
        t.Gravity = GravityFlags.Center;
        t.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f);
        col.AddView(t);

        cell.AddView(col);

        if (badge is > 0)
        {
            var b = new TextView(this) { Text = badge.Value.ToString("N0") };
            b.SetTextSize(Android.Util.ComplexUnitType.Sp, 9f);
            b.SetTypeface(null, TypefaceStyle.Bold);
            b.SetTextColor(Color.White);
            b.Background = Design.Fill(Design.Accent);
            b.SetPadding(Design.Dp(this, 4), Design.Dp(this, 1), Design.Dp(this, 4), Design.Dp(this, 1));
            b.LayoutParameters = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
            {
                Gravity = GravityFlags.Top | GravityFlags.Right,
                TopMargin = Design.Dp(this, 9),
                RightMargin = Design.Dp(this, 14),
            };
            cell.AddView(b);
        }

        cell.Click += (_, _) =>
        {
            if (tab == Tab.Review) { OpenReview(); return; }   // Review is its own activity
            _tab = tab;
            Render();
        };
        return cell;
    }

    void OpenReview() => StartActivity(new Intent(this, typeof(ReviewActivity)));

    // ══════════════════════════════════════════════════════════════════════════════════════
    //  1 · First run — the two promises, said once, above the fold
    // ══════════════════════════════════════════════════════════════════════════════════════

    void RenderFirstRun()
    {
        var pad = Design.Dp(this, 20);
        var body = new LinearLayout(this) { Orientation = Orientation.Vertical };
        body.SetPadding(pad, Design.Dp(this, 22), pad, pad);

        var wordmark = Design.Title(this, "SNAPZAP", 20f);
        wordmark.LetterSpacing = 0.02f;
        body.AddView(wordmark);
        body.AddView(Gap(28));

        body.AddView(Design.Eyebrow(this, "Nothing scanned yet"));
        body.AddView(Gap(12));

        var h = Design.Title(this, "Pick a folder to clean up.", 32f);
        h.LetterSpacing = -0.025f;
        body.AddView(h);
        body.AddView(Gap(12));

        body.AddView(Design.Body(this,
            "SnapZap reads everything underneath it, works out which photos are copies of each "
            + "other, and hands you a clean library. All of it on this phone.", 14.5f));
        body.AddView(Gap(22));

        body.AddView(Design.Eyebrow(this, "Folder to scan"));
        body.AddView(Gap(5));
        var input = Field(_folder);
        input.TextChanged += (_, _) => _folder = input.Text ?? _folder;
        body.AddView(input);
        body.AddView(Gap(12));

        var scan = Design.Button(this, "Scan this folder", Design.Btn.Primary, 52f);
        scan.LayoutParameters = Wide();
        scan.Click += (_, _) => StartScan();
        body.AddView(scan);
        body.AddView(Gap(10));

        var browse = Design.Button(this, "Browse…", Design.Btn.Secondary, 52f);
        browse.LayoutParameters = Wide();
        browse.Click += (_, _) => Toast.MakeText(this,
            "Type a path for now — the Folders sheet is screen 8.", ToastLength.Short)!.Show();
        body.AddView(browse);
        body.AddView(Gap(24));

        body.AddView(Design.Eyebrow(this, "Two promises"));
        body.AddView(Gap(10));
        body.AddView(Promise("Your originals stay put",
            "Scanning only reads. Files move in exactly two cases, both of which you ask for: an "
            + "export in Move mode, or ticking “also recycle what I didn’t pick”."));
        body.AddView(Promise("Nothing is ever hard-deleted",
            "Delete means the Recycle Bin. Every batch lands in History with a Restore beside it."));

        var offline = Design.Body(this, "OFFLINE · NOTHING UPLOADED · SHOWN ONCE", 10f, bold: true);
        offline.SetTextColor(Design.Accent700);
        offline.LetterSpacing = 0.06f;
        offline.SetPadding(0, Design.Dp(this, 14), 0, Design.Dp(this, 10));
        body.AddView(offline);

        _content.AddView(Scroll(body));
    }

    View Promise(string title, string note)
    {
        var l = new LinearLayout(this) { Orientation = Orientation.Vertical };
        l.AddView(Design.Rule(this, 2f, Design.Divider));
        var inner = new LinearLayout(this) { Orientation = Orientation.Vertical };
        inner.SetPadding(0, Design.Dp(this, 11), 0, Design.Dp(this, 14));
        inner.AddView(Design.Body(this, title, 14f, bold: true));
        inner.AddView(Design.Note(this, note));
        l.AddView(inner);
        return l;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    //  2 · Scanning — duplicates already queued to run by themselves
    // ══════════════════════════════════════════════════════════════════════════════════════

    void RenderScanning()
    {
        _content.AddView(Header("Scanning", null));

        var pad = Design.Dp(this, 16);
        var body = new LinearLayout(this) { Orientation = Orientation.Vertical };
        body.SetPadding(pad, pad, pad, 0);

        _scanCount = Design.Title(this, "0", 38f);
        _scanCount.LetterSpacing = -0.03f;
        body.AddView(_scanCount);
        body.AddView(Gap(8));

        _scanFile = Design.Note(this, "");
        body.AddView(_scanFile);
        body.AddView(Gap(16));

        var callout = Design.Callout(this);
        callout.AddView(Design.Body(this, "Duplicates run next, on their own", 13f, bold: true));
        callout.AddView(Design.Note(this,
            "The moment this finishes. You never have to ask for it.", Design.Accent700));
        body.AddView(callout);
        body.AddView(Gap(14));

        body.AddView(Design.Note(this,
            "Unreadable formats are counted and listed, never touched. Stopping keeps what is "
            + "analysed — re-scanning resumes."));

        _content.AddView(Scroll(body));
    }

    async void StartScan()
    {
        if (_scanning) return;
        if (!Directory.Exists(_folder))
        {
            Toast.MakeText(this, $"Not a folder: {_folder}", ToastLength.Long)!.Show();
            return;
        }

        _scanning = true;
        Render();

        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                if (_scanCount is null) return;
                _scanCount.Text = (p.Analyzed + p.Cached).ToString("N0");
                if (_scanFile is not null)
                    _scanFile.Text = p.Total > 0
                        ? $"of ~{p.Total:N0} read · {p.Current}"
                        : p.Current ?? "";
            });

            _lastScan = await Task.Run(() => _catalog!.NewScanner().ScanAsync(_folder, progress));

            // "Duplicates run next, on their own." The scanning screen promises it, so it happens
            // here rather than behind a button the user would have to go and find.
            _lastDupes = await Task.Run(() =>
                new DuplicateService(_catalog!.Db, _catalog.Imaging, _catalog.ThumbDir)
                    .DetectAsync(_folder));

            _photos = await Task.Run(() => _catalog!.Images.All().ToList());
            _hasScanned = _photos.Count > 0;

            Android.Util.Log.Info(Tag,
                $"{_lastScan.Total} photos · {_lastDupes.ExactGroups} exact · "
                + $"{_lastDupes.VariantGroups} variant · {_lastDupes.BurstGroups} burst");
        }
        catch (Exception ex)
        {
            Toast.MakeText(this, $"Scan failed: {ex.Message}", ToastLength.Long)!.Show();
            Android.Util.Log.Error(Tag, ex.ToString());
        }
        finally
        {
            _scanning = false;
            _tab = Tab.Library;
            Render();
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    //  3 · Library — one next step; three tabs carry everything else
    // ══════════════════════════════════════════════════════════════════════════════════════

    void RenderLibrary()
    {
        if (_selectionMode) { _content.AddView(SelectionHeader()); }
        else _content.AddView(Header("Library", null));

        var sum = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        sum.SetGravity(GravityFlags.CenterVertical);
        sum.SetPadding(Design.Dp(this, 16), Design.Dp(this, 10), Design.Dp(this, 16), Design.Dp(this, 10));

        var left = new LinearLayout(this) { Orientation = Orientation.Vertical };
        left.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        left.AddView(Design.Body(this, $"{_photos.Count:N0} photos", 13f, bold: true));
        left.AddView(Design.Note(this, SummaryNote()));
        sum.AddView(left);
        sum.AddView(PlanStrip());
        _content.AddView(sum);
        _content.AddView(Design.Rule(this, 2f, Design.Divider));

        if (_reviewable > 0)
        {
            var callout = Design.Callout(this);
            callout.AddView(Design.Body(this,
                $"{_reviewable:N0} extra {(_reviewable == 1 ? "copy is" : "copies are")} waiting", 15f, bold: true));
            callout.AddView(Design.Note(this,
                "Keeper already picked for each — you just confirm. Bursts are held back.",
                Design.Accent700));
            callout.AddView(Gap(10));
            var start = Design.Button(this, "Start reviewing", Design.Btn.Primary, 52f);
            start.LayoutParameters = Wide();
            start.Click += (_, _) => OpenReview();
            callout.AddView(start);
            _content.AddView(callout);
            _content.AddView(Design.Rule(this, 2f, Design.Divider));
        }

        var chips = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        chips.SetGravity(GravityFlags.CenterVertical);
        chips.SetPadding(Design.Dp(this, 16), Design.Dp(this, 9), Design.Dp(this, 16), Design.Dp(this, 9));
        var filters = Design.Button(this, FacetLabel(), Design.Btn.Secondary, 38f);
        filters.SetTextSize(Android.Util.ComplexUnitType.Sp, 13f);
        filters.Click += (_, _) => new FiltersSheet(this, _facet.ToString(), FacetCounts(),
            chosen => { _facet = Enum.Parse<DupeFacet>(chosen); Render(); }).Show();
        chips.AddView(filters);
        var hint = Design.Note(this, "Hold a photo to select");
        hint.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        hint.Gravity = GravityFlags.Right;
        chips.AddView(hint);
        _content.AddView(chips);
        _content.AddView(Design.Rule(this, 1f, Design.Divider));

        // .g3 — three up, 2px gutters
        var grid = new GridView(this) { NumColumns = 3 };
        grid.SetVerticalSpacing(Design.Dp(this, 2));
        grid.SetHorizontalSpacing(Design.Dp(this, 2));
        var cell = (Resources!.DisplayMetrics!.WidthPixels - Design.Dp(this, 4)) / 3;
        grid.SetColumnWidth(cell);
        grid.StretchMode = StretchMode.StretchColumnWidth;
        grid.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f);
        var shown = Visible();
        grid.Adapter = new ThumbAdapter(this, shown, cell, _catalog!.ThumbDir,
            _selectionMode ? id => _selected.Contains(id) : null);

        grid.ItemClick += (_, e) =>
        {
            var rec = shown[e.Position];
            if (_selectionMode)
            {
                if (!_selected.Add(rec.Id)) _selected.Remove(rec.Id);
                Render();
                return;
            }
            var i = new Intent(this, typeof(PreviewActivity));
            i.PutExtra(PreviewActivity.ExtraImageId, rec.Id);
            StartActivity(i);
        };

        // "Hold a photo to select" — the mode is entered by the gesture the chip row advertises.
        grid.ItemLongClick += (_, e) =>
        {
            var rec = shown[e.Position];
            _selectionMode = true;
            _selected.Add(rec.Id);
            Render();
        };

        _content.AddView(grid);

        if (_selectionMode) _content.AddView(SelectionActions());
    }

    /// <summary>
    /// The photos the grid is showing, after the active facet.
    /// </summary>
    /// <remarks>
    /// Facets are answered through <c>DupeAssignment</c> rather than by re-deriving the rule here,
    /// so the chip's count, the grid's contents and the review queue cannot disagree about what an
    /// "extra copy" is — the same argument CLAUDE.md makes for NsfwDecision and InScope.
    /// </remarks>
    List<ImageRecord> Visible() => _facet switch
    {
        DupeFacet.ExtrasOnly => _photos.Where(p => _assign.TryGetValue(p.Id, out var a) && a.IsBulkSelectableExtra).ToList(),
        DupeFacet.KeepersOnly => _photos.Where(p => _assign.TryGetValue(p.Id, out var a) && a.IsKeeper).ToList(),
        DupeFacet.BurstFrames => _photos.Where(p => _assign.TryGetValue(p.Id, out var a) && a.Kind == DupeKind.Burst).ToList(),
        _ => _photos,
    };

    string FacetLabel() => _facet switch
    {
        DupeFacet.ExtrasOnly => "Extra copies only",
        DupeFacet.KeepersOnly => "Keepers only",
        DupeFacet.BurstFrames => "Burst frames",
        _ => "Filters",
    };

    Dictionary<string, int> FacetCounts() => new()
    {
        ["Any"] = _photos.Count,
        ["ExtrasOnly"] = _assign.Values.Count(a => a.IsBulkSelectableExtra),
        ["KeepersOnly"] = _assign.Values.Count(a => a.IsKeeper),
        ["BurstFrames"] = _assign.Values.Count(a => a.Kind == DupeKind.Burst),
    };

    // ══════════════════════════════════════════════════════════════════════════════════════
    //  9 · Selection mode — entered by holding a photo; leaves the way it came
    // ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The ink header. Cancel where back lives, the count as the title, Invert opposite.</summary>
    View SelectionHeader()
    {
        var wrap = new LinearLayout(this) { Orientation = Orientation.Vertical };
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetBackgroundColor(Design.Ink);
        row.SetPadding(Design.Dp(this, 10), Design.Dp(this, 6), Design.Dp(this, 10), Design.Dp(this, 10));

        var cancel = Design.Button(this, "Cancel", Design.Btn.Ghost, 40f);
        cancel.SetTextColor(Design.Bg);
        cancel.Click += (_, _) => { _selectionMode = false; _selected.Clear(); Render(); };
        row.AddView(cancel);

        var count = Design.Title(this, $"{_selected.Count:N0} selected", 17f);
        count.SetTextColor(Design.Bg);
        count.Gravity = GravityFlags.Center;
        count.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        row.AddView(count);

        var invert = Design.Button(this, "Invert", Design.Btn.Ghost, 40f);
        invert.SetTextColor(Design.Bg);
        invert.Click += (_, _) =>
        {
            var shown = Visible().Select(p => p.Id).ToHashSet();
            var flipped = shown.Where(id => !_selected.Contains(id)).ToHashSet();
            _selected.Clear();
            foreach (var id in flipped) _selected.Add(id);
            Render();
        };
        row.AddView(invert);

        wrap.AddView(row);
        wrap.AddView(Design.Rule(this, 2f, Design.Divider));
        return wrap;
    }

    /// <summary>The two thumb-height actions, plus the select-all-extras affordance above them.</summary>
    View SelectionActions()
    {
        var wrap = new LinearLayout(this) { Orientation = Orientation.Vertical };

        var extras = _photos.Where(p => _assign.TryGetValue(p.Id, out var a) && a.IsBulkSelectableExtra).ToList();
        if (extras.Count > 0)
        {
            var callout = Design.Callout(this);
            var line = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            line.SetGravity(GravityFlags.CenterVertical);
            var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
            col.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            col.AddView(Design.Body(this,
                $"Every extra copy in this view — {FormatBytes(extras.Sum(e => e.FileSize))}", 13f, bold: true));
            // Stated, not implied: the one thing a bulk select must never quietly include.
            col.AddView(Design.Note(this, "Keepers and bursts are not in here", Design.Accent700));
            line.AddView(col);
            var all = Design.Button(this, "Select all", Design.Btn.Secondary, 38f);
            all.SetTextSize(Android.Util.ComplexUnitType.Sp, 12.5f);
            all.Click += (_, _) => { foreach (var e in extras) _selected.Add(e.Id); Render(); };
            line.AddView(all);
            callout.AddView(line);
            wrap.AddView(callout);
        }

        var bar = Design.ActionBar(this);
        var body = Design.ActionBarBody(bar);
        body.AddView(Design.Note(this, "Tap to add · hold a photo to enter this mode"));
        body.AddView(Design.Gap(this, 9));

        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var del = Design.Button(this, "Delete…", Design.Btn.Secondary, 48f);
        del.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        del.Click += (_, _) => ConfirmDelete();
        row.AddView(del);

        var exp = Design.Button(this, "Export…", Design.Btn.Primary, 48f);
        exp.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f)
        { LeftMargin = Design.Dp(this, 8) };
        // Screen 11 is out of v1 scope (ANDROID-PORT-PLAN §1). Dimmed to the system's own
        // disabled opacity — a full-strength primary button that does nothing is worse than an
        // obviously unavailable one, because it reads as broken rather than as not-yet-built.
        exp.Enabled = false;
        exp.Alpha = 0.45f;
        exp.Click += (_, _) => Toast.MakeText(this,
            "Export is not part of the Android version yet.", ToastLength.Short)!.Show();
        row.AddView(exp);
        body.AddView(row);

        wrap.AddView(bar);
        return wrap;
    }

    void ConfirmDelete()
    {
        if (_selected.Count == 0)
        {
            Toast.MakeText(this, "Nothing selected.", ToastLength.Short)!.Show();
            return;
        }

        var ids = _selected.ToList();
        var bytes = _photos.Where(p => _selected.Contains(p.Id)).Sum(p => p.FileSize);

        new AlertDialog.Builder(this)
            .SetTitle("Move to trash?")!
            .SetMessage($"{ids.Count:N0} photo(s), {FormatBytes(bytes)}, will be moved to SnapZap's "
                      + "trash.\n\nNothing is deleted from your device — they can be restored from History.")!
            .SetNegativeButton("Cancel", (EventHandler<DialogClickEventArgs>?)null)!
            .SetPositiveButton("Move to trash", async (_, _) =>
            {
                try
                {
                    var r = await Task.Run(() => _catalog!.NewDeleteService().RecycleAsync(ids));
                    _selectionMode = false; _selected.Clear();
                    _photos = await Task.Run(() => _catalog!.Images.All().ToList());
                    Toast.MakeText(this, $"Moved {r.Recycled:N0} to trash", ToastLength.Short)!.Show();
                }
                catch (Exception ex)
                {
                    Toast.MakeText(this, $"Delete failed: {ex.Message}", ToastLength.Long)!.Show();
                    Android.Util.Log.Error(Tag, ex.ToString());
                }
                Render();
            })!
            .Show();
    }

    static string FormatBytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):0.##} MB",
        >= 1L << 10 => $"{b / (double)(1L << 10):0.##} KB",
        _ => $"{b} B",
    };

    string SummaryNote()
    {
        var when = _lastScan is null ? "from the catalogue" : "scanned just now";
        return _reviewable > 0 ? $"{when} · {_reviewable:N0} extras" : when;
    }

    /// <summary>The plan's five steps, compressed to a progress strip (handoff §3).</summary>
    View PlanStrip()
    {
        var strip = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        strip.LayoutParameters = new LinearLayout.LayoutParams(
            Design.Dp(this, 96), Design.Dp(this, 5));

        var dupesDone = _reviewable > 0 || _lastDupes is not null;
        var state = new[] { _hasScanned, dupesDone, false, _hasScanned, false };
        for (int i = 0; i < state.Length; i++)
        {
            var seg = new View(this);
            seg.SetBackgroundColor(i == 1 && dupesDone ? Design.Accent
                                 : state[i] ? Design.Text : Design.Neutral300);
            var lp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1f);
            if (i > 0) lp.LeftMargin = Design.Dp(this, 2);
            seg.LayoutParameters = lp;
            strip.AddView(seg);
        }
        return strip;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    //  4 · Plan tab — the spine of the app, full height
    // ══════════════════════════════════════════════════════════════════════════════════════

    void RenderPlan()
    {
        var done = (_hasScanned ? 1 : 0) + (_reviewable > 0 || _lastDupes is not null ? 1 : 0) + (_hasScanned ? 1 : 0);
        _content.AddView(Header("Plan", $"{done} of 5 done"));

        var body = new LinearLayout(this) { Orientation = Orientation.Vertical };

        var head = new LinearLayout(this) { Orientation = Orientation.Vertical };
        head.SetPadding(Design.Dp(this, 16), Design.Dp(this, 11), Design.Dp(this, 16), Design.Dp(this, 11));
        head.AddView(Design.Body(this, _folder, 12.5f, bold: true));
        head.AddView(Design.Note(this, $"{_photos.Count:N0} photos"));
        body.AddView(head);
        body.AddView(Design.Rule(this, 2f, Design.Divider));

        body.AddView(Step(1, _hasScanned, "Scan",
            _lastScan is { } s ? $"{s.Total:N0} photos · {s.ElapsedMs:N0} ms" : $"{_photos.Count:N0} photos",
            "Re-scan", StartScan));

        body.AddView(Step(2, _reviewable > 0 || _lastDupes is not null, "Duplicates",
            _reviewable == 0 && _lastDupes is null
                ? "Runs on its own when the scan finishes"
                : $"{_reviewable:N0} extras · {_burstGroups:N0} burst groups held back",
            _reviewable > 0 ? $"Review {_reviewable:N0} extras" : null, OpenReview,
            highlight: _reviewable > 0));

        body.AddView(Step(3, false, "Content review",
            "Nothing scored yet. Needs the NSFW model beside the app — everything else works without it.",
            null, null));

        body.AddView(Step(4, _hasScanned, "Sharpness", "Scored during the scan", null, null));

        body.AddView(Step(5, false, "Export a clean library",
            "Not available on Android in this version.", null, null));

        var wrap = new LinearLayout(this) { Orientation = Orientation.Vertical };
        wrap.SetPadding(Design.Dp(this, 16), Design.Dp(this, 12), Design.Dp(this, 16), Design.Dp(this, 12));
        var trash = Design.Button(this, "Trash & history", Design.Btn.Secondary, 44f);
        trash.LayoutParameters = Wide();
        trash.Click += (_, _) => StartActivity(new Intent(this, typeof(TrashActivity)));
        wrap.AddView(trash);
        wrap.AddView(Gap(10));
        wrap.AddView(Design.Note(this,
            "Steps run in any order — this is the cheapest order, not a wizard."));
        body.AddView(wrap);

        _content.AddView(Scroll(body));
    }

    View Step(int n, bool done, string title, string note, string? action, Action? onAction, bool highlight = false)
    {
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetPadding(Design.Dp(this, highlight ? 12 : 16), Design.Dp(this, 11),
                       Design.Dp(this, 16), Design.Dp(this, 11));
        if (highlight) row.SetBackgroundColor(Design.Accent100);

        var num = new TextView(this) { Text = done ? "✓" : n.ToString() };
        num.SetTextSize(Android.Util.ComplexUnitType.Sp, 12f);
        num.SetTypeface(null, TypefaceStyle.Bold);
        num.Gravity = GravityFlags.Center;
        num.SetTextColor(done ? Design.Accent700 : Design.Neutral700);
        num.Background = Design.Outline(Color.Transparent,
            done ? Design.Accent : Design.Neutral400, Design.Dp(this, 2));
        num.LayoutParameters = new LinearLayout.LayoutParams(Design.Dp(this, 24), Design.Dp(this, 24))
        { RightMargin = Design.Dp(this, 11) };
        row.AddView(num);

        var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
        col.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        col.AddView(Design.Body(this, title, 15f, bold: true));
        col.AddView(Design.Note(this, note, highlight ? Design.Accent700 : null));
        if (action is not null)
        {
            col.AddView(Gap(9));
            var b = Design.Button(this, action,
                highlight ? Design.Btn.Primary : Design.Btn.Secondary, highlight ? 52f : 40f);
            b.LayoutParameters = Wide();
            if (onAction is not null) b.Click += (_, _) => onAction();
            col.AddView(b);
        }
        row.AddView(col);

        var holder = new LinearLayout(this) { Orientation = Orientation.Vertical };
        holder.AddView(row);
        holder.AddView(Design.Rule(this, 1f, Design.Divider));
        return holder;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    //  Permission gate — not in the handoff, but Android requires it before any of it works
    // ══════════════════════════════════════════════════════════════════════════════════════

    void RenderPermissionGate()
    {
        var pad = Design.Dp(this, 20);
        var body = new LinearLayout(this) { Orientation = Orientation.Vertical };
        body.SetPadding(pad, Design.Dp(this, 22), pad, pad);

        body.AddView(Design.Eyebrow(this, "One thing first"));
        body.AddView(Gap(12));
        body.AddView(Design.Title(this, "Storage access required", 28f));
        body.AddView(Gap(12));
        body.AddView(Design.Body(this,
            "SnapZap reads photos from any folder you choose, including folders the system Photos "
            + "app does not index. Android calls this “All files access” and only lets you grant "
            + "it from Settings.", 14.5f));
        body.AddView(Gap(16));
        body.AddView(Design.Note(this,
            "SnapZap has no internet permission — nothing it reads can leave this device."));
        body.AddView(Gap(20));

        var b = Design.Button(this, "Open Settings to grant access", Design.Btn.Primary, 52f);
        b.LayoutParameters = Wide();
        b.Click += (_, _) =>
        {
            try
            {
                StartActivity(new Intent(Settings.ActionManageAppAllFilesAccessPermission,
                    Android.Net.Uri.Parse("package:" + PackageName)));
            }
            catch (Exception ex)
            {
                // Some OEM builds do not honour the per-package intent; the global list always exists.
                Android.Util.Log.Warn(Tag, $"per-package intent failed: {ex.Message}");
                StartActivity(new Intent(Settings.ActionManageAllFilesAccessPermission));
            }
        };
        body.AddView(b);

        _content.AddView(Scroll(body));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    //  Small builders
    // ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>.hd — title left, optional eyebrow right, 2px rule beneath.</summary>
    View Header(string title, string? right)
    {
        var wrap = new LinearLayout(this) { Orientation = Orientation.Vertical };
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(Design.Dp(this, 16), Design.Dp(this, 6), Design.Dp(this, 16), Design.Dp(this, 12));
        var t = Design.Title(this, title);
        t.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        row.AddView(t);
        if (right is not null) row.AddView(Design.Eyebrow(this, right));
        wrap.AddView(row);
        wrap.AddView(Design.Rule(this, 2f, Design.Divider));
        return wrap;
    }

    EditText Field(string value)
    {
        var e = new EditText(this) { Text = value };
        e.SetTextSize(Android.Util.ComplexUnitType.Sp, 15f);
        e.SetTextColor(Design.Text);
        e.SetMinimumHeight(Design.Dp(this, 48));
        e.Background = Design.Outline(Design.Surface, Design.Divider, Design.Dp(this, 1));
        e.SetPadding(Design.Dp(this, 10), Design.Dp(this, 6), Design.Dp(this, 10), Design.Dp(this, 6));
        e.LayoutParameters = Wide();
        return e;
    }

    LinearLayout.LayoutParams Wide() =>
        new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);

    View Gap(float dp)
    {
        var v = new View(this);
        v.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Design.Dp(this, dp));
        return v;
    }

    ScrollView Scroll(View child)
    {
        var s = new ScrollView(this);
        s.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f);
        s.AddView(child, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        return s;
    }

    sealed class InsetsPadder : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
        {
            var bars = insets.GetInsets(WindowInsets.Type.SystemBars());
            v.SetPadding(0, bars.Top, 0, bars.Bottom);
            return insets;
        }
    }

    public override void OnBackPressed()
    {
        // Selection "leaves the way it came" — back cancels the mode before it leaves the screen.
        if (_selectionMode) { _selectionMode = false; _selected.Clear(); Render(); return; }
        base.OnBackPressed();
    }

    protected override void OnDestroy()
    {
        _catalog?.Dispose();
        base.OnDestroy();
    }
}
