using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using SnapZap.Core;
using SnapZap.Core.Dedup;
using SnapZap.Core.Nsfw;
using SnapZap.Core.Delete;
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

    /// <summary>
    /// The scan target. <c>DefaultStart</c> now resolves this to <c>DCIM/Camera</c> when it exists
    /// rather than to the whole of internal storage — see that method for why the old default made
    /// "scan every file on this phone" the pre-filled choice on first run.
    /// </summary>
    string _folder = SnapZap.Core.Platform.DirectoryRoots.DefaultStart(
        SnapZap.Core.Platform.DirectoryPlatform.Android);

    /// <summary>
    /// Cancels the scan *and* the dedup that follows it — they are one operation to the user, so
    /// one Stop has to end both. Non-null only while <see cref="_scanning"/>.
    /// </summary>
    CancellationTokenSource? _scanCts;
    bool _scanStopping;
    Button? _stopButton;

    /// <summary>Whether <see cref="_folder"/> has been reconciled with the catalogue's stored
    /// scan root yet. Once per launch — after that the user's choice wins.</summary>
    bool _folderRestored;

    /// <summary>Set by <see cref="OnResume"/>; the next <see cref="Render"/> re-reads the catalogue.</summary>
    bool _needsReload;

    // The folder the Library grid is narrowed to, distinct from _folder (the scan target): picking
    // one from the Folders chip filters what is already in the catalogue, it does not change what a
    // re-scan would read. Null means "whole library".
    string? _libraryScope;

    ScanResult? _lastScan;
    DuplicateReport? _lastDupes;

    // Group counts read back off the catalogue, not just from this session's scan. Detection
    // results persist, so a relaunch must still show the badge and the "waiting" callout —
    // otherwise the app forgets there is work outstanding the moment it is closed.
    int _reviewable, _burstGroups;

    /// <summary>Whether burst protection is switched on, read back from the catalogue in
    /// <see cref="CountGroups"/>. Several lines of copy here assert that it is.</summary>
    bool _burstEnabled = true;

    /// <summary>Photos in the current scope carrying an NSFW score, and whether a pass is running.</summary>
    int _nsfwScored;
    bool _nsfwRunning;

    /// <summary>
    /// Above this many photos in scope, scoring is refused rather than warned about.
    /// </summary>
    /// <remarks>
    /// Measured on a Galaxy S23: 2.9 s per photo at <c>NsfwDepth.Tiled</c>, which is ten inferences
    /// through a ViT-base model. 2,000 photos is already about 1.6 hours. Two things make a higher
    /// cap dishonest rather than merely slow — a phone throttles long before the end of a run that
    /// size, and Android 15 stops a <c>dataSync</c> foreground service after six hours in any 24, so
    /// a big enough job cannot finish however patient the user is. A dialog saying "this will take
    /// eight hours, continue?" gets tapped through; refusing and pointing at the Folders chip does
    /// not.
    /// </remarks>
    const int NsfwScopeLimit = 2000;
    List<ImageRecord> _photos = [];

    // Live scan read-outs, held so progress can update them without rebuilding the screen.
    TextView? _scanCount, _scanFile;

    // What the last scan needs to say for itself beyond the photo count — nothing found, or files
    // it counted but could not read. Null when the scan was unremarkable.
    string? _scanNotice;
    View? _scanFill;

    // Screen 9 — selection is a mode you enter, not an always-on toolbar. Holding a photo enters
    // it and the header turns to ink, so there is no doubt you are in it.
    BackHandler? _back;
    bool _selectionMode;
    readonly HashSet<long> _selected = [];

    // Screen 7 — the active facet. Null means "Any".
    DupeFacet _facet = DupeFacet.Any;
    enum DupeFacet { Any, ExtrasOnly, KeepersOnly, BurstFrames, LikelyExplicit, WorthALook }

    /// <summary>
    /// The explicit-content rule in force. Read through <c>NsfwSettings</c> rather than compared
    /// inline anywhere, exactly as CLAUDE.md requires of the desktop: the badge, the filter and the
    /// counts have to be one decision or they start disagreeing.
    /// </summary>
    static readonly NsfwSettings NsfwRule = NsfwSettings.Default;
    Dictionary<long, DupeAssignment> _assign = [];

    /// <summary>
    /// Grid order. Names and null-handling match the desktop's <c>SortKey</c> deliberately, so the
    /// two heads cannot mean different things by "Captured"; the desktop's <c>Blur</c> and
    /// <c>Nsfw</c> members are left out because a phone chip row has no space for six options and
    /// NSFW is not scored on Android at all (see the Plan tab's step 3).
    /// </summary>
    enum SortKey { Scanned, Captured, Name, Size }
    SortKey _sort = SortKey.Scanned;
    bool _sortDescending;

    /// <summary>
    /// Whether the "extras are waiting" callout has been dismissed for this session. The handoff
    /// makes it dismissible (<c>showNextStep</c>); without that it is a banner you cannot get rid
    /// of until the last duplicate is resolved, which punishes anyone who just wants to browse.
    /// Session-only on purpose — a new launch is a new chance to mention there is work waiting.
    /// </summary>
    bool _calloutDismissed;

    // Library view plumbing. The header and action bar live in slots so entering selection mode,
    // or changing what is selected, can swap them *without* rebuilding the grid — see
    // SelectionChanged. Rebuilding it lost the scroll position on every single tap, and made the
    // handoff's hold-and-drag range gesture impossible to implement at all.
    FrameLayout? _headerSlot, _actionSlot;
    GridView? _grid;
    TextView? _selCount;
    List<ImageRecord> _shown = [];
    bool _dragSelecting;
    int _lastDragPos = -1;

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

        // Back has to be registered through the dispatcher: the OnBackPressed override below is
        // never called on API 36 (predictive back is default-on there). See BackHandler.
        _back = BackHandler.Attach(this, HandleBack);

        WorkService.EnsureChannel(this);
        // Asked for, never insisted on: refusing it costs the progress read-out and nothing else,
        // so there is no gate and no second prompt.
        if (OperatingSystem.IsAndroidVersionAtLeast(33) &&
            CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Permission.Granted)
            RequestPermissions([Android.Manifest.Permission.PostNotifications], 42);

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

    /// <summary>
    /// Anything could have changed the catalogue while another screen was in front.
    /// </summary>
    /// <remarks>
    /// <para>The reload used to be gated on <c>!_hasScanned</c>, so it ran once — on the very first
    /// render — and never again. Everything that edits the catalogue from another activity therefore
    /// left this screen showing a stale list: Settings' Forget left the Plan tab reporting photos
    /// that no longer existed, deleting from the preview left the tile in the grid, and re-running
    /// detection left the counts on the previous answer. It is also half of why changing the scan
    /// folder appeared not to refresh anything (the other half was reading the catalogue unscoped —
    /// see <c>AndroidCatalog.ScopedImages</c>).</para>
    ///
    /// <para>Once per resume rather than once per <see cref="Render"/>: Render also runs on every
    /// tab switch and selection change, and this is a full table read.</para>
    /// </remarks>
    protected override void OnResume()
    {
        base.OnResume();
        if (_scanning) return;
        _needsReload = true;
        Render();
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

        // Once per launch, and only before this session has scanned anything: the catalogue knows
        // which folder it was built from, and that beats the platform default.
        if (!_folderRestored)
        {
            _folderRestored = true;
            var stored = _catalog.ScanRoot;
            if (!string.IsNullOrEmpty(stored) && Directory.Exists(stored)) _folder = stored!;
        }

        if (_needsReload || (!_hasScanned && !_scanning))
        {
            _needsReload = false;
            _photos = _catalog.ScopedImages.ToList();
            _hasScanned = _photos.Count > 0;
            // Reset so a catalogue emptied by Settings' Forget does not leave the grid narrowed to a
            // folder that no longer has rows, which reads as "the scan lost my photos".
            if (!_hasScanned) { _libraryScope = null; _facet = DupeFacet.Any; }
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
            // Read, not assumed: every line of copy on this screen that claims bursts are held back
            // becomes false when the setting is off, and "0 burst groups held back" would read as
            // "the protection ran and found none" rather than "the protection is not running".
            _burstEnabled = DedupSettings.Load(_catalog.Db).BurstEnabled;
            _nsfwScored = ScopedPhotos().Count(p => p.NsfwScore is not null);
        }
        catch (Exception ex)
        {
            _reviewable = 0; _burstGroups = 0; _assign = [];
            _burstEnabled = true;   // the safe reading if the catalogue could not be asked
            _nsfwScored = 0;
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

        // The label is drawn upper-cased with wide tracking, which some screen readers spell out a
        // letter at a time, and the count sits in a separate view that reads as a bare number.
        cell.ContentDescription = badge is > 0
            ? $"{label}, {badge.Value:N0} waiting{(on ? ", selected" : "")}"
            : $"{label}{(on ? ", selected" : "")}";

        cell.Click += (_, _) =>
        {
            if (tab == Tab.Review) { OpenReview(); return; }   // Review is its own activity
            _tab = tab;
            Render();
        };
        return cell;
    }

    void OpenReview() => StartActivity(new Intent(this, typeof(ReviewActivity)));

    const int RequestFolderForScan = 1;
    const int RequestFolderForScope = 2;

    /// <summary>
    /// Screen 8, opened from the pre-scan "Browse…" button. Reachable only before a scan (see
    /// <see cref="Render"/>), when the catalogue is empty — <c>FoldersActivity</c> always shows its
    /// "nothing scanned yet" state at that point, so this can only come back cancelled. Left wired
    /// up rather than removed, so the handoff's affordance stays present and this is a documented,
    /// deliberate gap rather than something a future change quietly "fixes" into a raw filesystem
    /// picker without that being a decision.
    /// </summary>
    const int RequestPickScanFolder = 3;

    /// <summary>
    /// Picks a new scan target — by browsing the filesystem, not by typing a path.
    /// </summary>
    /// <remarks>
    /// Deliberately <see cref="DirectoryPickerActivity"/> and not <see cref="FoldersActivity"/>:
    /// the latter derives its tree from photos already catalogued, so it can only walk inside what
    /// has been scanned, which is useless for reaching somewhere new. That is why this gap survived
    /// review — the Folders browser looked like it covered the case and did not.
    /// </remarks>
    void ChangeScanFolder()
    {
        var i = new Intent(this, typeof(DirectoryPickerActivity));
        i.PutExtra(DirectoryPickerActivity.ExtraPath, _folder);
        StartActivityForResult(i, RequestPickScanFolder);
    }

    void OpenFoldersForScan() =>
        StartActivityForResult(new Intent(this, typeof(FoldersActivity)), RequestFolderForScan);

    /// <summary>
    /// Screen 8, opened from the Library tab's Folders chip: narrows the already-scanned library to
    /// whatever folder the user drills to, without touching <see cref="_folder"/> (the scan target)
    /// or requiring a re-scan.
    /// </summary>
    void OpenFoldersForScope()
    {
        var i = new Intent(this, typeof(FoldersActivity));
        if (_libraryScope is not null) i.PutExtra(FoldersActivity.ExtraCurrentFolder, _libraryScope);
        StartActivityForResult(i, RequestFolderForScope);
    }

    protected override async void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (resultCode != Result.Ok) return;
        if (requestCode == RequestPickScanFolder)
        {
            var chosen = data?.GetStringExtra(DirectoryPickerActivity.ExtraPath);
            if (string.IsNullOrEmpty(chosen) || !await Task.Run(() => Directory.Exists(chosen))) return;
            _folder = chosen!;
            _libraryScope = null;   // a new scan root makes the old library scope meaningless
            StartScan();
            return;
        }

        if (requestCode != RequestFolderForScan && requestCode != RequestFolderForScope) return;

        var picked = data?.GetStringExtra(FoldersActivity.ExtraSelectedFolder);
        if (string.IsNullOrEmpty(picked) || !await Task.Run(() => Directory.Exists(picked))) return;

        if (requestCode == RequestFolderForScan)
        {
            _folder = picked;
            Toast.MakeText(this, $"Folder set to {picked}", ToastLength.Short)!.Show();
        }
        else
        {
            _libraryScope = picked;
            Toast.MakeText(this, $"Library narrowed to {System.IO.Path.GetFileName(picked)}",
                ToastLength.Short)!.Show();
        }
        Render();
    }

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

        if (_scanNotice is not null)
        {
            // Persisted on the screen, not only in a toast: a toast that has already faded cannot
            // answer "did that do anything?", which is the exact question an empty scan raises.
            var notice = Design.Callout(this);
            notice.AddView(Design.Body(this, "Last scan found nothing to add", 13f, bold: true));
            notice.AddView(Design.Note(this, _scanNotice, Design.Accent700));
            body.AddView(notice);
            body.AddView(Gap(16));
        }

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
        body.AddView(Gap(8));
        body.AddView(ScanTargetChips(input));
        body.AddView(Gap(12));

        var scan = Design.Button(this, "Scan this folder", Design.Btn.Primary, 52f);
        scan.LayoutParameters = Wide();
        scan.Click += (_, _) => StartScan();
        body.AddView(scan);
        body.AddView(Gap(10));

        var browse = Design.Button(this, "Browse…", Design.Btn.Secondary, 52f);
        browse.LayoutParameters = Wide();
        browse.Click += (_, _) => ChangeScanFolder();
        body.AddView(browse);
        body.AddView(Gap(24));

        body.AddView(Design.Eyebrow(this, "Two promises"));
        body.AddView(Gap(10));
        body.AddView(Promise("Your originals stay put",
            "Scanning only reads. Files move in exactly two cases, both of which you ask for: an "
            + "answering “Remove originals” when you move them, or ticking “also recycle what I didn’t pick”."));
        body.AddView(Promise("Nothing is ever hard-deleted",
            "Delete means the Recycle Bin. Every batch lands in History with a Restore beside it."));

        var offline = Design.Body(this, "OFFLINE · NOTHING UPLOADED · SHOWN ONCE", 10f, bold: true);
        offline.SetTextColor(Design.Accent700);
        offline.LetterSpacing = 0.06f;
        offline.SetPadding(0, Design.Dp(this, 14), 0, Design.Dp(this, 10));
        body.AddView(offline);

        _content.AddView(Scroll(body));
    }

    /// <summary>
    /// One-tap scan targets: the folders a phone actually keeps photos in, then "Everything on this
    /// phone" last.
    /// </summary>
    /// <remarks>
    /// <para>Order is the argument. Internal storage used to be the pre-filled default, so the
    /// easiest possible first run swept <c>Android/data</c>, app caches and every messaging app's
    /// media into the catalogue — and from there into the duplicate groups and the review queue,
    /// before the user had decided anything. Scanning everything is still here, because sometimes
    /// it is what someone wants; it is just no longer what happens by default.</para>
    ///
    /// <para>Candidates come from <c>DirectoryRoots.PhotoFolders</c> — shared with the desktop
    /// picker's quick-jumps rather than hard-coded here, so the two heads agree on where photos
    /// live.</para>
    /// </remarks>
    View ScanTargetChips(EditText input)
    {
        var scroller = new HorizontalScrollView(this);
        scroller.HorizontalScrollBarEnabled = false;
        scroller.LayoutParameters = Wide();

        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };

        var targets = SnapZap.Core.Platform.DirectoryRoots
            .PhotoFolders(SnapZap.Core.Platform.DirectoryPlatform.Android)
            .Append(SnapZap.Core.Platform.DirectoryRoots.AndroidPrimaryStorage)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        for (int i = 0; i < targets.Count; i++)
        {
            var path = targets[i];
            var chip = Design.Button(this, ScanTargetLabel(path), Design.Btn.Secondary, 38f);
            chip.SetTextSize(Android.Util.ComplexUnitType.Sp, 12.5f);
            chip.ContentDescription = $"Scan {ScanTargetLabel(path)} — {path}";
            chip.LayoutParameters = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
            { RightMargin = i == targets.Count - 1 ? 0 : Design.Dp(this, 6) };
            // Sets the field rather than starting the scan: the path is still worth seeing before
            // committing to a run that can take minutes.
            chip.Click += (_, _) => { _folder = path; input.Text = path; };
            row.AddView(chip);
        }

        scroller.AddView(row);
        return scroller;
    }

    /// <summary>What a phone calls each well-known folder, rather than its last path segment.</summary>
    static string ScanTargetLabel(string path)
    {
        var root = SnapZap.Core.Platform.DirectoryRoots.AndroidPrimaryStorage;
        var rel = path.StartsWith(root, StringComparison.Ordinal)
            ? path[root.Length..].Trim('/')
            : path;
        return rel switch
        {
            "" => "Everything on this phone",
            "DCIM/Camera" => "Camera roll",
            "DCIM" => "All DCIM",
            "Download" => "Downloads",
            _ => rel,
        };
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
        // The design puts Stop in this header, and the copy at the foot of the screen has always
        // promised it ("Stopping keeps what is analysed"). It was never built — so until now the
        // app told the user it could be interrupted and offered no way to do it, on a screen that
        // can legitimately run for minutes over a large library.
        _stopButton = Design.Button(this, _scanStopping ? "Stopping…" : "Stop", Design.Btn.Secondary, 40f);
        _stopButton.SetTextSize(Android.Util.ComplexUnitType.Sp, 13f);
        _stopButton.Enabled = !_scanStopping;
        _stopButton.Click += (_, _) => StopScan();
        _content.AddView(Header("Scanning", null, _stopButton));

        var pad = Design.Dp(this, 16);
        var body = new LinearLayout(this) { Orientation = Orientation.Vertical };
        body.SetPadding(pad, pad, pad, 0);

        _scanCount = Design.Title(this, "0", 38f);
        _scanCount.LetterSpacing = -0.03f;
        // Announced as it changes rather than only when focused: this is the one screen whose whole
        // content is a number that moves on its own.
        _scanCount.AccessibilityLiveRegion = AccessibilityLiveRegion.Polite;
        body.AddView(_scanCount);
        body.AddView(Gap(8));

        var (track, fill) = Design.ProgressBar(this, 6f);
        _scanFill = fill;
        body.AddView(track);
        body.AddView(Gap(6));

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

    /// <summary>
    /// Asks the in-flight scan to stop. Idempotent, and deliberately does not touch
    /// <see cref="_scanning"/> — the operation is still running until the awaits in
    /// <see cref="StartScan"/> unwind, and pretending otherwise would let a second scan start on
    /// top of the first. The button label carries the interim state instead.
    /// </summary>
    void StopScan()
    {
        if (!_scanning || _scanStopping) return;
        _scanStopping = true;
        if (_stopButton is not null) { _stopButton.Text = "Stopping…"; _stopButton.Enabled = false; }
        _scanCts?.Cancel();
    }

    async void StartScan()
    {
        if (_scanning) return;
        if (!await Task.Run(() => Directory.Exists(_folder)))
        {
            Toast.MakeText(this, $"Not a folder: {_folder}", ToastLength.Long)!.Show();
            return;
        }

        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        _scanning = true;
        _scanStopping = false;
        Render();

        // Scanning a folder is what chooses the folder you are working on — the same rule, and the
        // same meta key, as the desktop's CatalogService.ScanAsync.
        _catalog!.ScanRoot = _folder;

        // Held across the scan *and* the dedup that follows it: they are one operation from the
        // user's point of view, and dropping the service between them would leave the longer half
        // unprotected exactly when the app is most likely to be backgrounded.
        WorkService.Start(this, "Scanning photos");

        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                var done = p.Analyzed + p.Cached;
                if (_scanCount is not null) _scanCount.Text = done.ToString("N0");
                if (_scanFile is not null)
                    _scanFile.Text = p.Total > 0
                        ? $"of ~{p.Total:N0} read · {System.IO.Path.GetFileName(p.Current ?? "")}"
                        : p.Current ?? "";

                if (_scanFill is { Parent: View parent } && p.Total > 0)
                {
                    var lp = _scanFill.LayoutParameters!;
                    lp.Width = (int)(parent.Width * Math.Clamp(done / (double)p.Total, 0, 1));
                    _scanFill.LayoutParameters = lp;
                }

                WorkService.Report(this, "Scanning photos",
                    p.Total > 0 ? $"{done:N0} of ~{p.Total:N0}" : $"{done:N0} read", done, p.Total);
            });

            _lastScan = await Task.Run(() => _catalog!.NewScanner().ScanAsync(_folder, progress, ct), ct);

            // "Duplicates run next, on their own." The scanning screen promises it, so it happens
            // here rather than behind a button the user would have to go and find.
            // Dedup reports per-phase rather than per-photo — the exact pass is one SQL statement
            // and the perceptual passes are a single parallel sweep with no honest midpoint, which
            // DedupProgress's own remarks explain. A moving label beats an invented percentage.
            if (_scanCount is not null) _scanCount.Text = "…";
            var dedupProgress = new Progress<DedupProgress>(p =>
            {
                if (_scanFile is not null) _scanFile.Text = p.Detail ?? "Finding duplicates…";
                WorkService.Report(this, "Finding duplicates", p.Detail, p.Done, p.Total);
            });

            _lastDupes = await Task.Run(() =>
                new DuplicateService(_catalog!.Db, _catalog.Imaging, _catalog.ThumbDir)
                    .DetectAsync(_folder, dedupProgress, ct), ct);

            _photos = await Task.Run(() => _catalog!.ScopedImages.ToList());
            _hasScanned = _photos.Count > 0;
            _scanNotice = ScanNotice(_lastScan);

            Android.Util.Log.Info(Tag,
                $"{_lastScan.Total} photos · {_lastDupes.ExactGroups} exact · "
                + $"{_lastDupes.VariantGroups} variant · {_lastDupes.BurstGroups} burst · "
                + $"{_lastScan.UnsupportedTotal} unsupported · {_lastScan.Failed} failed");

            // A scan that finds nothing used to return to first run unchanged: same screen, same
            // button, no message. The user could not tell whether it had run, failed, or found
            // nothing — and if the folder was full of HEIC, "found nothing" was actively wrong.
            if (_scanNotice is not null)
                Toast.MakeText(this, _scanNotice, ToastLength.Long)!.Show();
        }
        // Spelled out: `using Android.OS` puts Android.OS.OperationCanceledException in scope, and
        // the bare name is ambiguous — the same trap as Android.Graphics.Path vs System.IO.Path.
        catch (System.OperationCanceledException)
        {
            // Stopping is an outcome, not a failure, and the screen's own copy promises what
            // happens: everything analysed so far is already committed, so the catalogue is the
            // answer. _lastScan is cleared because a partial run's totals would be read as the
            // folder's totals everywhere they are shown.
            _lastScan = null;
            _photos = await Task.Run(() => _catalog!.ScopedImages.ToList());
            _hasScanned = _photos.Count > 0;
            _scanNotice = _hasScanned
                ? $"Scan stopped. {_photos.Count:N0} photos kept — scanning again picks up where it left off."
                : "Scan stopped before anything was read.";
            Toast.MakeText(this, _scanNotice, ToastLength.Long)!.Show();
            Android.Util.Log.Info(Tag, "scan cancelled by the user");
        }
        catch (Exception ex)
        {
            Toast.MakeText(this, $"Scan failed: {ex.Message}", ToastLength.Long)!.Show();
            Android.Util.Log.Error(Tag, ex.ToString());
        }
        finally
        {
            WorkService.Stop(this);
            _scanning = false;
            _scanStopping = false;
            _stopButton = null;
            _scanCts?.Dispose();
            _scanCts = null;
            _tab = Tab.Library;
            Render();
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    //  3 · Library — one next step; three tabs carry everything else
    // ══════════════════════════════════════════════════════════════════════════════════════

    void RenderLibrary()
    {
        // Header and action bar go in slots so SelectionChanged/SelectionModeChanged can swap them
        // without touching the grid. See those methods.
        _headerSlot = new FrameLayout(this);
        _headerSlot.LayoutParameters = Wide();
        _content.AddView(_headerSlot);
        FillHeaderSlot();

        var sum = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        sum.SetGravity(GravityFlags.CenterVertical);
        sum.SetPadding(Design.Dp(this, 16), Design.Dp(this, 10), Design.Dp(this, 16), Design.Dp(this, 10));

        var left = new LinearLayout(this) { Orientation = Orientation.Vertical };
        left.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        left.AddView(Design.Body(this, $"{ScopedPhotos().Count:N0} photos", 13f, bold: true));
        left.AddView(Design.Note(this, SummaryNote()));
        sum.AddView(left);
        sum.AddView(PlanStrip());
        _content.AddView(sum);
        _content.AddView(Design.Rule(this, 2f, Design.Divider));

        var waiting = ScopedExtras().Count;
        if (waiting > 0 && !_calloutDismissed)
        {
            var callout = Design.Callout(this);

            var top = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            top.SetGravity(GravityFlags.Top);
            var headings = new LinearLayout(this) { Orientation = Orientation.Vertical };
            headings.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            headings.AddView(Design.Body(this,
                $"{waiting:N0} extra {(waiting == 1 ? "copy is" : "copies are")} waiting", 15f, bold: true));
            headings.AddView(Design.Note(this,
                _burstEnabled
                    ? "Keeper already picked for each — you just confirm. Bursts are held back."
                    : "Keeper already picked for each — you just confirm. Burst frames are included, "
                      + "because burst grouping is switched off.",
                Design.Accent700));
            top.AddView(headings);

            // The handoff makes this dismissible and the first build did not, which left a banner
            // that could not be got rid of until the last duplicate was resolved. The work does not
            // disappear with it — the Review tab keeps its badge, and Plan keeps step 2.
            var close = Design.IconButton(this, "✕", "Dismiss this reminder");
            close.LayoutParameters = new LinearLayout.LayoutParams(Design.Dp(this, 32), Design.Dp(this, 32))
            { LeftMargin = Design.Dp(this, 8) };
            close.Click += (_, _) => { _calloutDismissed = true; Render(); };
            top.AddView(close);
            callout.AddView(top);

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

        // The handoff's sort chip. Without it the grid was permanently in scan order, which is the
        // one order nobody thinks in — "the photos I took last week" is unreachable by scrolling.
        var sort = Design.Button(this, SortLabel(), Design.Btn.Secondary, 38f);
        sort.SetTextSize(Android.Util.ComplexUnitType.Sp, 13f);
        sort.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        { LeftMargin = Design.Dp(this, 8) };
        sort.ContentDescription = $"Sort order: {SortLabel()}. Tap to change.";
        sort.Click += (_, _) => ShowSortMenu();
        chips.AddView(sort);

        var folders = Design.Button(this, FolderScopeLabel(), Design.Btn.Secondary, 38f);
        folders.SetTextSize(Android.Util.ComplexUnitType.Sp, 13f);
        folders.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        { LeftMargin = Design.Dp(this, 8) };
        folders.Click += (_, _) => OpenFoldersForScope();
        chips.AddView(folders);

        // Narrowing is otherwise a one-way trip — this is the only way back to the whole library
        // short of relaunching the app.
        if (_libraryScope is not null)
        {
            var clearScope = Design.IconButton(this, "✕", "Show the whole library again");
            clearScope.LayoutParameters = new LinearLayout.LayoutParams(Design.Dp(this, 32), Design.Dp(this, 32))
            { LeftMargin = Design.Dp(this, 6) };
            clearScope.Click += (_, _) => { _libraryScope = null; Render(); };
            chips.AddView(clearScope);
        }

        var hint = Design.Note(this, "Hold a photo to select · keep dragging for a range");
        hint.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        { LeftMargin = Design.Dp(this, 10) };
        chips.AddView(hint);

        // The chip row scrolls now that there are three of them plus a clear button — the hint used
        // to be pinned right on the same line, which left the Folders chip nowhere to grow.
        var chipScroll = new HorizontalScrollView(this) { HorizontalScrollBarEnabled = false };
        chipScroll.LayoutParameters = Wide();
        chipScroll.AddView(chips);
        _content.AddView(chipScroll);
        _content.AddView(Design.Rule(this, 1f, Design.Divider));

        _shown = Visible();

        if (_shown.Count == 0)
        {
            // An empty grid used to say nothing at all, so a facet that matched nothing looked
            // identical to a broken screen. Name what is filtering, and offer the way out.
            _content.AddView(EmptyView());
        }
        else
        {
            // .g3 — three up, 2px gutters
            var grid = new GridView(this) { NumColumns = 3 };
            grid.SetVerticalSpacing(Design.Dp(this, 2));
            grid.SetHorizontalSpacing(Design.Dp(this, 2));
            var cell = (Resources!.DisplayMetrics!.WidthPixels - Design.Dp(this, 4)) / 3;
            grid.SetColumnWidth(cell);
            grid.StretchMode = StretchMode.StretchColumnWidth;
            grid.LayoutParameters = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, 0, 1f);
            grid.Adapter = new ThumbAdapter(this, _shown, cell, _catalog!.ThumbDir,
                _selectionMode ? id => _selected.Contains(id) : null);
            _grid = grid;

            grid.ItemClick += (_, e) =>
            {
                var rec = _shown[e.Position];
                if (_selectionMode)
                {
                    if (!_selected.Add(rec.Id)) _selected.Remove(rec.Id);
                    SelectionChanged();
                    return;
                }
                var i = new Intent(this, typeof(PreviewActivity));
                i.PutExtra(PreviewActivity.ExtraImageId, rec.Id);
                StartActivity(i);
            };

            // "Hold a photo to select" — the mode is entered by the gesture the chip row advertises,
            // and the same gesture continues into a drag (see HandleGridTouch). Entering the mode
            // must not rebuild the grid, or the touch stream the drag needs dies with the old view.
            grid.ItemLongClick += (_, e) =>
            {
                var rec = _shown[e.Position];
                var entering = !_selectionMode;
                _selectionMode = true;
                _selected.Add(rec.Id);
                _dragSelecting = true;
                _lastDragPos = e.Position;
                if (entering) SelectionModeChanged(); else SelectionChanged();
            };

            grid.Touch += (_, e) => e.Handled = HandleGridTouch(e.Event!);

            _content.AddView(grid);
        }

        _actionSlot = new FrameLayout(this);
        _actionSlot.LayoutParameters = Wide();
        _content.AddView(_actionSlot);
        FillActionSlot();
    }

    /// <summary>
    /// The handoff's "hold and drag across for a range", which had never been built — the footer
    /// copy was quietly reworded to promise only tap-to-add instead.
    /// </summary>
    /// <remarks>
    /// Only ever consumes events once a long-press has armed <see cref="_dragSelecting"/>, so the
    /// grid keeps its normal scrolling the rest of the time. Returning true from here suppresses
    /// <c>GridView.OnTouchEvent</c>, which is what stops the list scrolling under the finger while
    /// a range is being swept.
    /// </remarks>
    bool HandleGridTouch(MotionEvent ev)
    {
        if (!_dragSelecting) return false;

        switch (ev.ActionMasked)
        {
            case MotionEventActions.Move:
                var pos = _grid!.PointToPosition((int)ev.GetX(), (int)ev.GetY());
                if (pos != AdapterView.InvalidPosition && pos != _lastDragPos && pos < _shown.Count)
                {
                    _lastDragPos = pos;
                    // Add-only. A drag that toggled would flip photos back off as the finger
                    // wandered, and "I dragged over it and it deselected" is not recoverable
                    // without watching every cell.
                    if (_selected.Add(_shown[pos].Id)) SelectionChanged();
                }
                return true;

            case MotionEventActions.Up:
            case MotionEventActions.Cancel:
                _dragSelecting = false;
                _lastDragPos = -1;
                return true;

            default:
                return true;
        }
    }

    void FillHeaderSlot()
    {
        if (_headerSlot is null) return;
        _headerSlot.RemoveAllViews();
        _headerSlot.AddView(_selectionMode ? SelectionHeader() : Header("Library", null));
    }

    void FillActionSlot()
    {
        if (_actionSlot is null) return;
        _actionSlot.RemoveAllViews();
        if (_selectionMode) _actionSlot.AddView(SelectionActions());
    }

    /// <summary>
    /// What changed is <i>which</i> photos are selected, not whether selection is on. Updates the
    /// count and repaints the tiles in place.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="Render"/>. Rebuilding the screen on every tap threw away the
    /// grid's scroll position, so selecting a photo two hundred rows down jumped straight back to
    /// the top — which made selecting more than a handful of photos genuinely impractical.
    /// </remarks>
    void SelectionChanged()
    {
        if (_selCount is not null) _selCount.Text = $"{_selected.Count:N0} selected";
        if (_grid?.Adapter is BaseAdapter a) a.NotifyDataSetChanged();
    }

    /// <summary>Selection mode was entered or left: the header and the action bar swap.</summary>
    void SelectionModeChanged()
    {
        if (_headerSlot is null || _actionSlot is null || _grid is null) { Render(); return; }

        FillHeaderSlot();
        FillActionSlot();
        if (_grid.Adapter is ThumbAdapter t)
            t.SelectionTest = _selectionMode ? id => _selected.Contains(id) : null;
        SelectionChanged();
    }

    /// <summary>The screen a facet with no matches lands on. Says what is filtering, and undoes it.</summary>
    View EmptyView()
    {
        var wrap = new LinearLayout(this) { Orientation = Orientation.Vertical };
        wrap.SetGravity(GravityFlags.CenterHorizontal);
        wrap.SetPadding(Design.Dp(this, 28), Design.Dp(this, 44), Design.Dp(this, 28), Design.Dp(this, 28));
        wrap.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f);

        var narrowed = _facet != DupeFacet.Any || _libraryScope is not null;

        wrap.AddView(Design.Eyebrow(this, narrowed ? "Nothing matches" : "Nothing here"));
        wrap.AddView(Gap(10));

        var headline = Design.Title(this, narrowed
            ? "No photos match this view."
            : "This library is empty.", 22f);
        headline.Gravity = GravityFlags.CenterHorizontal;
        wrap.AddView(headline);
        wrap.AddView(Gap(8));

        var detail = Design.Note(this, narrowed
            ? $"Showing {FacetLabel()}{(_libraryScope is null ? "" : $" in {System.IO.Path.GetFileName(_libraryScope)}")}. "
              + "Everything else is still in the library."
            : "Scan a folder from the Plan tab to fill it.");
        detail.Gravity = GravityFlags.CenterHorizontal;
        wrap.AddView(detail);

        if (narrowed)
        {
            wrap.AddView(Gap(16));
            var clear = Design.Button(this, "Show everything", Design.Btn.Primary, 48f);
            clear.Click += (_, _) => { _facet = DupeFacet.Any; _libraryScope = null; Render(); };
            wrap.AddView(clear);
        }
        return wrap;
    }

    // ---- Sort ---------------------------------------------------------------------------------

    string SortLabel() => (_sort, _sortDescending) switch
    {
        (SortKey.Scanned, _) => "Scan order",
        (SortKey.Captured, true) => "Newest first",
        (SortKey.Captured, false) => "Oldest first",
        (SortKey.Name, _) => "Name A–Z",
        (SortKey.Size, _) => "Largest first",
        _ => "Sort",
    };

    void ShowSortMenu()
    {
        var options = new (string Label, SortKey Key, bool Desc)[]
        {
            ("Scan order", SortKey.Scanned, false),
            ("Newest first", SortKey.Captured, true),
            ("Oldest first", SortKey.Captured, false),
            ("Name A–Z", SortKey.Name, false),
            ("Largest first", SortKey.Size, true),
        };

        new AlertDialog.Builder(this)
            .SetTitle("Sort by")!
            .SetItems(options.Select(o => o.Label).ToArray(), (_, e) =>
            {
                var pick = options[e.Which];
                _sort = pick.Key;
                _sortDescending = pick.Desc;
                Render();
            })!
            .Show();
    }

    /// <summary>
    /// Applies <see cref="_sort"/>. Photos with no capture date sort last regardless of direction,
    /// matching the desktop's <c>Nulls</c> helper — otherwise "oldest first" opens on a wall of
    /// screenshots, which is exactly the sort nobody wanted.
    /// </summary>
    List<ImageRecord> Sorted(IEnumerable<ImageRecord> items) => _sort switch
    {
        SortKey.Name => (_sortDescending
            ? items.OrderByDescending(p => System.IO.Path.GetFileName(p.Path), StringComparer.OrdinalIgnoreCase)
            : items.OrderBy(p => System.IO.Path.GetFileName(p.Path), StringComparer.OrdinalIgnoreCase)).ToList(),
        SortKey.Size => (_sortDescending
            ? items.OrderByDescending(p => p.FileSize)
            : items.OrderBy(p => p.FileSize)).ToList(),
        SortKey.Captured => (_sortDescending
            ? items.OrderBy(p => p.ExifTaken is null).ThenByDescending(p => p.ExifTaken)
            : items.OrderBy(p => p.ExifTaken is null).ThenBy(p => p.ExifTaken)).ToList(),
        _ => (_sortDescending ? items.OrderByDescending(p => p.Id) : items.OrderBy(p => p.Id)).ToList(),
    };

    /// <summary>
    /// _photos narrowed to <see cref="_libraryScope"/>, if one is set — the base every part of the
    /// Library screen that describes "what's in view" reads from, so the header count, the Folders
    /// chip, the Filters sheet and bulk actions cannot disagree about what "narrowed" means.
    /// </summary>
    List<ImageRecord> ScopedPhotos() => _libraryScope is null
        ? _photos
        : _photos.Where(p => p.Path.StartsWith(
            SnapZap.Core.Data.PathScope.Prefix(_libraryScope), StringComparison.Ordinal)).ToList();

    /// <summary>
    /// The photos the grid is showing, after the active library scope and facet.
    /// </summary>
    /// <remarks>
    /// Facets are answered through <c>DupeAssignment</c> rather than by re-deriving the rule here,
    /// so the chip's count, the grid's contents and the review queue cannot disagree about what an
    /// "extra copy" is — the same argument CLAUDE.md makes for NsfwDecision and InScope.
    /// </remarks>
    List<ImageRecord> Visible() => Sorted(_facet switch
    {
        DupeFacet.ExtrasOnly => ScopedPhotos().Where(p => _assign.TryGetValue(p.Id, out var a) && a.IsBulkSelectableExtra),
        DupeFacet.KeepersOnly => ScopedPhotos().Where(p => _assign.TryGetValue(p.Id, out var a) && a.IsKeeper),
        DupeFacet.BurstFrames => ScopedPhotos().Where(p => _assign.TryGetValue(p.Id, out var a) && a.Kind == DupeKind.Burst),
        DupeFacet.LikelyExplicit => ScopedPhotos().Where(p => NsfwRule.IsExplicit(p.NsfwScore, p.NsfwTileMean)),
        DupeFacet.WorthALook => ScopedPhotos().Where(p => NsfwRule.IsUnsure(p.NsfwScore, p.NsfwTileMean)),
        _ => ScopedPhotos(),
    });

    string FacetLabel() => _facet switch
    {
        DupeFacet.ExtrasOnly => "Extra copies only",
        DupeFacet.KeepersOnly => "Keepers only",
        DupeFacet.BurstFrames => "Burst frames",
        DupeFacet.LikelyExplicit => "Likely explicit",
        DupeFacet.WorthALook => "Worth a look",
        _ => "Filters",
    };

    string FolderScopeLabel() => _libraryScope is null
        ? "Folders" : $"Folders: {System.IO.Path.GetFileName(_libraryScope)}";

    Dictionary<string, int> FacetCounts()
    {
        var scoped = ScopedPhotos();
        return new()
        {
            ["Any"] = scoped.Count,
            ["ExtrasOnly"] = scoped.Count(p => _assign.TryGetValue(p.Id, out var a) && a.IsBulkSelectableExtra),
            ["KeepersOnly"] = scoped.Count(p => _assign.TryGetValue(p.Id, out var a) && a.IsKeeper),
            ["BurstFrames"] = scoped.Count(p => _assign.TryGetValue(p.Id, out var a) && a.Kind == DupeKind.Burst),
            ["LikelyExplicit"] = scoped.Count(p => NsfwRule.IsExplicit(p.NsfwScore, p.NsfwTileMean)),
            ["WorthALook"] = scoped.Count(p => NsfwRule.IsUnsure(p.NsfwScore, p.NsfwTileMean)),
            // Not a facet — it is what decides whether the two above are offered at all.
            ["_scored"] = scoped.Count(p => p.NsfwScore is not null),
        };
    }

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
        cancel.ContentDescription = "Leave selection mode";
        cancel.Click += (_, _) => { _selectionMode = false; _selected.Clear(); SelectionModeChanged(); };
        row.AddView(cancel);

        // Held so SelectionChanged can update it without rebuilding the header — the count is the
        // only part of this bar that moves.
        _selCount = Design.Title(this, $"{_selected.Count:N0} selected", 17f);
        _selCount.SetTextColor(Design.Bg);
        _selCount.Gravity = GravityFlags.Center;
        _selCount.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        _selCount.AccessibilityLiveRegion = AccessibilityLiveRegion.Polite;
        row.AddView(_selCount);

        var invert = Design.Button(this, "Invert", Design.Btn.Ghost, 40f);
        invert.SetTextColor(Design.Bg);
        invert.ContentDescription = "Invert the selection within this view";
        invert.Click += (_, _) =>
        {
            var shown = _shown.Select(p => p.Id).ToHashSet();
            var flipped = shown.Where(id => !_selected.Contains(id)).ToHashSet();
            _selected.Clear();
            foreach (var id in flipped) _selected.Add(id);
            SelectionChanged();
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

        var extras = ScopedPhotos().Where(p => _assign.TryGetValue(p.Id, out var a) && a.IsBulkSelectableExtra).ToList();
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
            // The single most load-bearing line on this screen: it sits directly above a control
            // that selects everything for deletion. It must describe the setting in force, never the
            // setting we would prefer.
            col.AddView(Design.Note(this,
                _burstEnabled
                    ? "Keepers and bursts are not in here"
                    : "Keepers are not in here — but burst frames are, because burst grouping is off",
                Design.Accent700));
            line.AddView(col);
            var all = Design.Button(this, "Select all", Design.Btn.Secondary, 38f);
            all.SetTextSize(Android.Util.ComplexUnitType.Sp, 12.5f);
            all.ContentDescription =
                $"Select all {extras.Count:N0} extra copies in this view. Keepers and burst frames are excluded.";
            all.Click += (_, _) => { foreach (var e in extras) _selected.Add(e.Id); SelectionChanged(); };
            line.AddView(all);
            callout.AddView(line);
            wrap.AddView(callout);
        }

        var bar = Design.ActionBar(this);
        var body = Design.ActionBarBody(bar);
        body.AddView(Design.Note(this, "Tap to add · hold and drag across for a range"));
        body.AddView(Design.Gap(this, 9));

        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var del = Design.Button(this, "Delete…", Design.Btn.Secondary, 48f);
        del.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        del.Click += (_, _) => ConfirmDelete();
        row.AddView(del);

        var exp = Design.Button(this, "Move…", Design.Btn.Primary, 48f);
        exp.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f)
        { LeftMargin = Design.Dp(this, 8) };
        // Screen 11 is out of v1 scope (ANDROID-PORT-PLAN §1). Dimmed to the system's own
        // disabled opacity — a full-strength primary button that does nothing is worse than an
        // obviously unavailable one, because it reads as broken rather than as not-yet-built.
        exp.Enabled = false;
        exp.Alpha = 0.45f;
        exp.Click += (_, _) => Toast.MakeText(this,
            "Moving photos to a folder is not part of the Android version yet.", ToastLength.Short)!.Show();
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
                    WorkService.Start(this, "Moving to trash");
                    var deleteProgress = new Progress<DeleteProgress>(p =>
                        WorkService.Report(this, "Moving to trash",
                            System.IO.Path.GetFileName(p.Current ?? ""), p.Done, p.Total));

                    var r = await Task.Run(() =>
                        _catalog!.NewDeleteService().RecycleAsync(ids, deleteProgress));
                    _selectionMode = false; _selected.Clear();
                    _photos = await Task.Run(() => _catalog!.ScopedImages.ToList());
                    Toast.MakeText(this, $"Moved {r.Recycled:N0} to trash", ToastLength.Short)!.Show();
                }
                catch (Exception ex)
                {
                    Toast.MakeText(this, $"Delete failed: {ex.Message}", ToastLength.Long)!.Show();
                    Android.Util.Log.Error(Tag, ex.ToString());
                }
                finally { WorkService.Stop(this); }
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

    /// <summary>
    /// The one thing a finished scan still owes the user, or null.
    /// </summary>
    /// <remarks>
    /// Unreadable formats are the case that matters. <c>ScanResult.UnsupportedByFormat</c> has
    /// always been populated and the mobile design shows it in as many words ("1,204 HEIC and 312
    /// RAW counted, not readable"), but nothing rendered it — so a library of HEIC, which is the
    /// default on modern phones rather than an edge case, reported "0 photos" and read as a broken
    /// app. Naming the format is what turns that into an explanation.
    /// </remarks>
    static string? ScanNotice(ScanResult r)
    {
        var formats = r.UnsupportedByFormat
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"{kv.Value:N0} {kv.Key.TrimStart('.').ToUpperInvariant()}")
            .Take(3)
            .ToList();

        if (r.Total == 0)
            return formats.Count > 0
                ? $"No readable photos here. Counted {string.Join(", ", formats)} — SnapZap cannot read those."
                : "No photos found in that folder.";

        if (formats.Count > 0)
            return $"{r.Total:N0} photos. {string.Join(", ", formats)} counted but not readable — listed, never touched.";

        return r.Failed > 0 ? $"{r.Total:N0} photos · {r.Failed:N0} could not be read." : null;
    }

    /// <summary>
    /// Counts here follow the active folder scope, like the header count and the Filters sheet do.
    /// A scoped view that still reports whole-library extras is the same disagreement-between-
    /// counts problem this codebase keeps designing out.
    /// </summary>
    string SummaryNote()
    {
        var when = _lastScan is null ? "from the catalogue" : "scanned just now";
        var extras = ScopedExtras().Count;
        return extras > 0 ? $"{when} · {extras:N0} extra{(extras == 1 ? "" : "s")}" : when;
    }

    /// <summary>Extras inside the active scope — the set "Select all" would take.</summary>
    List<ImageRecord> ScopedExtras() =>
        ScopedPhotos().Where(p => _assign.TryGetValue(p.Id, out var a) && a.IsBulkSelectableExtra).ToList();

    /// <summary>The plan's five steps, compressed to a progress strip (handoff §3).</summary>
    View PlanStrip()
    {
        var strip = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        strip.LayoutParameters = new LinearLayout.LayoutParams(
            Design.Dp(this, 96), Design.Dp(this, 5));
        // A restatement of the Plan tab in five coloured bars — nothing here that the Plan tab does
        // not say in words, so it stays out of the accessibility tree rather than reading as five
        // anonymous views.
        strip.ImportantForAccessibility = ImportantForAccessibility.NoHideDescendants;

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
        // Counted against the steps this version actually has. It used to say "of 5" while two of
        // those five could never be completed on Android at all — a plan permanently 60% finished by
        // construction reads as a stalled app rather than a shorter feature set. Content review
        // rejoined the count once the model became fetchable; export is still the only one that
        // cannot be done here.
        var done = (_hasScanned ? 1 : 0) + (_reviewable > 0 || _lastDupes is not null ? 1 : 0) + (_hasScanned ? 1 : 0);
        _content.AddView(Header("Plan", $"{done} of 4 done"));

        var body = new LinearLayout(this) { Orientation = Orientation.Vertical };

        var head = new LinearLayout(this) { Orientation = Orientation.Vertical };
        head.SetPadding(Design.Dp(this, 16), Design.Dp(this, 11), Design.Dp(this, 16), Design.Dp(this, 11));
        head.AddView(Design.Body(this, _folder, 12.5f, bold: true));
        head.AddView(Design.Note(this, $"{_photos.Count:N0} photos"));
        body.AddView(head);
        body.AddView(Design.Rule(this, 2f, Design.Divider));

        // Two actions, because they are different questions. Re-scan re-reads the same folder;
        // Change folder picks a new scan target — which the app previously had no way to do at all
        // once the first scan existed, since the folder input lives only on first run and the
        // Folders browser can only walk what is already catalogued. The desktop has always had
        // "Change folder…" in its Plan rail; this is the same affordance.
        body.AddView(Step(1, _hasScanned, "Scan",
            _lastScan is { } s ? $"{s.Total:N0} photos · {s.ElapsedMs:N0} ms" : $"{_photos.Count:N0} photos",
            "Re-scan", StartScan, secondary: ("Change folder…", ChangeScanFolder)));

        body.AddView(Step(2, _reviewable > 0 || _lastDupes is not null, "Duplicates",
            _reviewable == 0 && _lastDupes is null
                ? "Runs on its own when the scan finishes"
                : _burstEnabled
                    ? $"{_reviewable:N0} extras · {_burstGroups:N0} burst {(_burstGroups == 1 ? "group" : "groups")} held back"
                    : $"{_reviewable:N0} extras · burst grouping is off, so burst frames are included",
            _reviewable > 0 ? $"Review {_reviewable:N0} extras" : null, OpenReview,
            highlight: _reviewable > 0));

        // Reachable now that the model can be fetched, but deliberately scoped and capped: ~2.9 s
        // per photo on a Galaxy S23 makes this a per-folder job, not a library one. See ScoreNsfw.
        var modelPath = _catalog!.FindNsfwModel();
        var scope = ScopedPhotos();
        body.AddView(Step(3, _nsfwScored > 0, "Content review",
            modelPath is null
                ? "Scores photos on this device. Needs a one-time model download — set it up in Settings."
                : _nsfwScored > 0
                    ? $"{_nsfwScored:N0} scored in this view"
                    : $"{scope.Count:N0} photos in view · about {NsfwEta(scope.Count)} on this phone",
            modelPath is null ? null : _nsfwRunning ? "Scoring…" : $"Score {scope.Count:N0} photos",
            modelPath is null ? null : () => ScoreNsfw(modelPath)));

        body.AddView(Step(4, _hasScanned, "Sharpness", "Scored during the scan", null, null));

        body.AddView(Step(5, false, "Move to a clean folder",
            "Desktop only in this version.", null, null, unavailable: true));

        var wrap = new LinearLayout(this) { Orientation = Orientation.Vertical };
        wrap.SetPadding(Design.Dp(this, 16), Design.Dp(this, 12), Design.Dp(this, 16), Design.Dp(this, 12));
        var trash = Design.Button(this, "Trash & history", Design.Btn.Secondary, 44f);
        trash.LayoutParameters = Wide();
        trash.Click += (_, _) => StartActivity(new Intent(this, typeof(TrashActivity)));
        wrap.AddView(trash);
        wrap.AddView(Gap(8));

        var settings = Design.Button(this, "Settings", Design.Btn.Secondary, 44f);
        settings.LayoutParameters = Wide();
        settings.Click += (_, _) => StartActivity(new Intent(this, typeof(SettingsActivity)));
        wrap.AddView(settings);
        wrap.AddView(Gap(10));
        wrap.AddView(Design.Note(this,
            "Steps run in any order — this is the cheapest order, not a wizard."));
        body.AddView(wrap);

        _content.AddView(Scroll(body));
    }

    /// <param name="unavailable">
    /// This step cannot be taken in this build at all. Marked with a dash rather than a number so
    /// it reads as "not part of this version" instead of "not done yet" — the distinction the Plan
    /// tab previously could not make, which is why Content review sat at step 3 looking merely
    /// pending.
    /// </param>
    View Step(int n, bool done, string title, string note, string? action, Action? onAction,
              bool highlight = false, (string Label, Action OnTap)? secondary = null,
              bool unavailable = false)
    {
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetPadding(Design.Dp(this, highlight ? 12 : 16), Design.Dp(this, 11),
                       Design.Dp(this, 16), Design.Dp(this, 11));
        if (highlight) row.SetBackgroundColor(Design.Accent100);
        if (unavailable) row.Alpha = 0.6f;

        var num = new TextView(this) { Text = unavailable ? "—" : done ? "✓" : n.ToString() };
        num.SetTextSize(Android.Util.ComplexUnitType.Sp, 12f);
        num.SetTypeface(null, TypefaceStyle.Bold);
        num.Gravity = GravityFlags.Center;
        num.SetTextColor(done && !unavailable ? Design.Accent700 : Design.Neutral700);
        num.Background = Design.Outline(Color.Transparent,
            done && !unavailable ? Design.Accent : Design.Neutral400, Design.Dp(this, 2));
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

        if (secondary is { } sec)
        {
            col.AddView(Gap(8));
            var b2 = Design.Button(this, sec.Label, Design.Btn.Ghost, 40f);
            b2.LayoutParameters = Wide();
            b2.Click += (_, _) => sec.OnTap();
            col.AddView(b2);
        }
        row.AddView(col);

        var holder = new LinearLayout(this) { Orientation = Orientation.Vertical };
        holder.AddView(row);
        holder.AddView(Design.Rule(this, 1f, Design.Divider));

        // Read as one step. The number, tick or dash is state, not content — "step 2, Duplicates,
        // done" rather than "2" then "Duplicates".
        var state = unavailable ? "not in this version" : done ? "done" : "not started";
        row.ContentDescription = $"Step {n}, {title}, {state}. {note}";
        num.ImportantForAccessibility = ImportantForAccessibility.No;
        return holder;
    }

    /// <summary>Rough wall-clock for a tiled pass, from the S23 measurement.</summary>
    static string NsfwEta(int photos)
    {
        var t = TimeSpan.FromSeconds(photos * 2.9);
        return t.TotalMinutes < 1 ? "under a minute"
             : t.TotalHours < 1 ? $"{t.TotalMinutes:N0} minutes"
             : $"{t.TotalHours:N1} hours";
    }

    /// <summary>
    /// Scores the photos currently in view, on this device.
    /// </summary>
    /// <remarks>
    /// <para>Scoped to <see cref="ScopedPhotos"/> rather than the catalogue, for the reason
    /// <c>NsfwScorer</c>'s own <c>root</c> parameter documents: the catalogue outlives every folder
    /// ever scanned, so an unscoped run goes off and scores thousands of photos the user cannot see
    /// and did not ask about. Here that is worse than untidy — it is hours of phone CPU.</para>
    ///
    /// <para>Always <see cref="NsfwDepth.Tiled"/>. Whole-frame is ten times cheaper and is the mode
    /// that scored 0.0014 on a photo the tiled pass scored 0.9983 — a fast scan that misses what it
    /// exists to find is worse than none, because it leaves the user believing a folder was
    /// checked.</para>
    /// </remarks>
    async void ScoreNsfw(string modelPath)
    {
        if (_nsfwRunning) return;

        var scope = ScopedPhotos();
        if (scope.Count == 0) { Toast.MakeText(this, "Nothing in view to score.", ToastLength.Short)!.Show(); return; }

        if (scope.Count > NsfwScopeLimit)
        {
            new AlertDialog.Builder(this)
                .SetTitle("Too many photos to score")!
                .SetMessage($"{scope.Count:N0} photos would take about {NsfwEta(scope.Count)} on this "
                          + $"phone, and Android stops background work of that length before it finishes.\n\n"
                          + $"Narrow the library to a folder first — up to {NsfwScopeLimit:N0} photos at a time.")!
                .SetPositiveButton("Choose a folder", (_, _) => OpenFoldersForScope())!
                .SetNegativeButton("Cancel", (EventHandler<DialogClickEventArgs>?)null)!
                .Show();
            return;
        }

        new AlertDialog.Builder(this)
            .SetTitle($"Score {scope.Count:N0} photos?")!
            .SetMessage($"About {NsfwEta(scope.Count)} on this phone. Everything runs here — nothing "
                      + "about your photos is sent anywhere.\n\nKeep SnapZap open or let it run in the "
                      + "background; it is best on a charger.")!
            .SetNegativeButton("Cancel", (EventHandler<DialogClickEventArgs>?)null)!
            .SetPositiveButton("Score them", async (_, _) =>
            {
                _nsfwRunning = true;
                Render();
                WorkService.Start(this, "Scoring photos");
                try
                {
                    var ids = scope.Select(p => p.Id).ToList();
                    var progress = new Progress<NsfwProgress>(p =>
                        WorkService.Report(this, "Scoring photos",
                            $"{p.Done:N0} of {p.Total:N0}", p.Done, p.Total));

                    var result = await Task.Run(() =>
                        new NsfwScorer(_catalog!.Db, modelPath)
                            .ScoreAllAsync(imageIds: ids, progress: progress, depth: NsfwDepth.Tiled));

                    Toast.MakeText(this,
                        result.ModelAvailable
                            ? $"Scored {result.Scored:N0}{(result.Failed > 0 ? $", {result.Failed:N0} failed" : "")}"
                            : result.Note ?? "Model unavailable",
                        ToastLength.Long)!.Show();
                }
                catch (Exception ex)
                {
                    Toast.MakeText(this, $"Scoring failed: {ex.Message}", ToastLength.Long)!.Show();
                    Android.Util.Log.Error(Tag, ex.ToString());
                }
                finally
                {
                    WorkService.Stop(this);
                    _nsfwRunning = false;
                    _photos = await Task.Run(() => _catalog!.Images.All().ToList());
                    Render();
                }
            })!
            .Show();
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
            "Your photos never leave this device. SnapZap downloads one thing — the optional "
            + "content-scoring model — and sends nothing back."));
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

    /// <summary>.hd — title left, optional eyebrow or control right, 2px rule beneath.</summary>
    View Header(string title, string? right, View? trailing = null)
    {
        var wrap = new LinearLayout(this) { Orientation = Orientation.Vertical };
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(Design.Dp(this, 16), Design.Dp(this, 6), Design.Dp(this, 16), Design.Dp(this, 12));
        var t = Design.Title(this, title);
        t.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        row.AddView(t);
        if (right is not null) row.AddView(Design.Eyebrow(this, right));
        if (trailing is not null) row.AddView(trailing);
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

    /// <summary>Pre-33 path. On 33+ the dispatcher calls <see cref="HandleBack"/> instead.</summary>
    public override void OnBackPressed() => HandleBack();

    /// <summary>Selection "leaves the way it came" — Back cancels the mode before leaving the screen.</summary>
    void HandleBack()
    {
        if (_selectionMode) { _selectionMode = false; _selected.Clear(); SelectionModeChanged(); return; }
        Finish();
    }

    protected override void OnDestroy()
    {
        _back?.Detach();
        // The catalogue is about to be disposed out from under anything still running, so an
        // in-flight scan has to be told to stop first rather than be left to fault on a closed
        // connection.
        _scanCts?.Cancel();
        _catalog?.Dispose();
        base.OnDestroy();
    }
}
