using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using SnapZap.Core;
using SnapZap.Core.Data;
using SnapZap.Core.Dedup;

namespace SnapZap.Droid;

/// <summary>
/// Screen 8 of the mobile handoff — Folders: a drill-down browser over the folders the catalogue's
/// photos live in, ending in <c>SetResult</c> so the caller can scope the library to whatever the
/// user picked.
/// </summary>
/// <remarks>
/// <para><b>Why drill-down, not a tree.</b> The handoff is explicit: an indented tree is wrong here
/// because "no thumb can hit accurately" — a nested-disclosure row at 44dp tall with three levels
/// of indent leaves almost no width for the label, and the twisty itself is far smaller than the
/// row. Showing exactly one level at a time (this folder's immediate children) keeps every row a
/// full-width, full-height target, and the breadcrumb plus the header's <c>‹</c> are what carry the
/// "where am I" job a tree would otherwise do with indentation.</para>
///
/// <para><b>How the tree is derived.</b> There is no Android-visible <c>FolderTree</c>/
/// <c>FolderTreeBuilder</c> — that type lives in <c>SnapZap.App</c>, which is a non-self-contained
/// desktop <c>Exe</c> this project cannot reference (docs/ANDROID-PORT-PLAN.md). <c>PathScope</c>
/// (Core) is the closest fit and is reused for its prefix-range *idea* (a folder's subtree is every
/// path starting with <c>folder + "/"</c>), but it is a SQL predicate, not a tree, so it does not
/// itself answer "what are this folder's children." That is computed here, directly from
/// <c>catalog.Images.All()</c>'s paths, in one linear pass per navigation
/// (<see cref="ComputeLevel"/>): every photo's path is trimmed to what falls under the folder being
/// browsed, and the first remaining path segment is either "no separator left" (the photo sits
/// directly in this folder) or a child folder name — which also means a child's *whole subtree*
/// count falls out of the same pass with no extra work, however many levels deeper it goes.
/// Deliberately not the FolderTree that ships in the App project, and not a smarter recursive
/// structure either — a flat pass per level is easy to get right, and correctness mattered more
/// here than avoiding an O(photo count) scan on each navigation.</para>
///
/// <para><b>Why "unanalysed" reads as "not yet duplicate-checked."</b> The task copy for the third
/// row state says "analysis hasn't reached here yet," attributed to "folders with unanalysed
/// photos." In this catalogue, though, <c>ImageRecord.AnalyzedAt</c> is set unconditionally by
/// <c>Scanner.Analyze</c> before a row is ever written (see <c>Scanner.cs</c>) — a row cannot exist
/// with it null, so that literal field can never produce this state. The one coverage flag that
/// genuinely can be null per-photo, and is null exactly when a later pass "hasn't reached" a folder
/// yet, is <c>DupeCheckedAt</c> (set by <c>ImageRepository.MarkDupeChecked</c> after a completed
/// detection run scoped to a root) — CLAUDE.md draws the same "hasn't reached here yet" comparison
/// for its NSFW analogue, <c>nsfw_tile_mean</c>. So a folder reads as "analysis hasn't reached here
/// yet" when any photo in its subtree has <c>DupeCheckedAt == null</c>, in preference to a group
/// count that would otherwise be incomplete and misleading.</para>
///
/// <para><b>The selection model — inferred, not shown in the static mock.</b> The handoff renders one
/// frozen frame, not a full interaction spec, and it has no visible "confirm" control. Three
/// behaviours had to be decided:
/// <list type="bullet">
/// <item>Tapping a folder row navigates into it (drills one level deeper) — this is the mechanic
/// the task explicitly asks for ("Drill-down... one level at a time"), so a row tap cannot also be
/// a terminal pick or multi-level drilling would be impossible.</item>
/// <item>The header's <c>‹</c> undoes that, one level at a time, and only <i>finishes</i> the
/// activity once already at the root — exactly the literal spec ("Back goes up one level; at the
/// root it finishes the activity"), with no result attached (the user backed all the way out
/// without choosing anything).</item>
/// <item>The system/gesture Back button is treated as "I'm done, use where I've drilled to" —
/// it finishes the activity and returns <see cref="_current"/> via <see cref="ExtraSelectedFolder"/>
/// (or nothing, if that is still the root). This is the one place a result besides "cancelled" can
/// come from, since nothing else in this screen commits a choice.</item>
/// </list>
/// Reasonable people could wire this differently; the reasoning is recorded here so it is a
/// decision, not an accident.</para>
///
/// <para><b>Local additions Design.cs does not offer.</b> <see cref="Design.HeaderBar"/> takes a
/// plain, non-interactive subtitle string, but the task requires each breadcrumb *segment* to be
/// independently tappable ("Tap its name in the breadcrumb to come back out") — so the header here
/// is hand-built from <see cref="Design.IconButton"/>/<see cref="Design.Title"/>/
/// <see cref="Design.Note"/>/<see cref="Design.Rule"/> rather than reusing <c>HeaderBar</c> whole.
/// Separately, the bottom "photos directly in this folder" grid is a real <c>GridView</c> +
/// <see cref="ThumbAdapter"/> (matching <c>MainActivity.RenderLibrary</c>) sitting inside a
/// <c>ScrollView</c> alongside the folder-row list above it; <c>GridView</c> owns its own scrolling,
/// which fights an ancestor <c>ScrollView</c>, so it is given a fixed height equal to its own
/// content (rows × row height) rather than <c>MatchParent</c>/<c>WrapContent</c> — it then has
/// nothing left to scroll internally, and the outer <c>ScrollView</c> is the only one that ever
/// engages.</para>
/// </remarks>
[Activity(Label = "Folders", Theme = "@android:style/Theme.Material.Light.NoActionBar")]
public sealed class FoldersActivity : Activity
{
    const string Tag = "SnapZap";

    /// <summary>
    /// Input extra: the folder the library is presently scoped to, if any. Used only to decide
    /// where this screen opens (one level above it, so that folder appears in the list, highlighted
    /// with a "Showing" trailing label) — it is read once in <see cref="OnCreate"/> and never
    /// updated as the user navigates around during this screen's lifetime.
    /// </summary>
    public const string ExtraCurrentFolder = "snapzap.folders.currentFolder";

    /// <summary>
    /// Output extra, present only when <see cref="Result.Ok"/> is returned: the folder the caller
    /// (<c>MainActivity</c>, not edited by this change) should scope the library to next.
    /// </summary>
    public const string ExtraSelectedFolder = "snapzap.folders.selectedFolder";

    AndroidCatalog _catalog = null!;
    List<ImageRecord> _photos = [];
    Dictionary<long, DupeAssignment> _assign = [];

    /// <summary>
    /// The tightest folder that contains every photo in the catalogue — the top of the drill-down,
    /// above which <c>‹</c> finishes the activity. There is no separately-recorded "the folder that
    /// was scanned" reachable from Android (that lives only as a local in <c>MainActivity</c>), so
    /// this is derived from the photo paths themselves: the common ancestor directory of all of
    /// them. In the ordinary case (one folder scanned, nothing outside it) that ancestor *is* the
    /// scanned folder; it can only end up deeper if every single photo happens to share a common
    /// subfolder, which is harmless — there would be nothing to show for the shallower levels
    /// anyway, since this tree only ever has folders that contain at least one photo.
    /// </summary>
    string _root = "/";

    /// <summary>The folder whose children are currently displayed.</summary>
    string _current = "/";

    /// <summary>
    /// The folder the library was scoped to when this screen opened (from
    /// <see cref="ExtraCurrentFolder"/>), or null if there was none. Fixed for this screen's
    /// lifetime; only used to decide which child row (if any) renders as "Showing".
    /// </summary>
    string? _activeScope;

    string _filter = "";

    Dictionary<string, ChildAgg> _children = [];
    List<ImageRecord> _directPhotos = [];
    int _currentSubtreeCount;

    LinearLayout _rootView = null!, _content = null!, _rowsContainer = null!;

    /// <summary>Per-child-folder rollup, built in one pass over the catalogue by <see cref="ComputeLevel"/>.</summary>
    sealed class ChildAgg
    {
        public int Count;
        public bool AnyUnchecked;
        public int DupeCount;
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _catalog = new AndroidCatalog();
        _photos = _catalog.Images.All().ToList();
        _root = ComputeRoot(_photos);

        try
        {
            _assign = DupeAssignmentResolver.Resolve(
                new DupeRepository(_catalog.Db).Groups(), _catalog.Images.BurstAdjacentIds());
        }
        catch (Exception ex)
        {
            _assign = [];
            Android.Util.Log.Warn(Tag, $"dupe resolve failed: {ex.Message}");
        }

        var incoming = Intent?.GetStringExtra(ExtraCurrentFolder);
        if (!string.IsNullOrEmpty(incoming) && IsUnderOrEqual(incoming, _root))
        {
            _activeScope = incoming;
            _current = incoming == _root ? _root : (ParentOf(incoming) ?? _root);
        }
        else
        {
            _current = _root;
            _activeScope = null;
        }

        _rootView = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _rootView.SetBackgroundColor(Design.Bg);
        _rootView.FocusableInTouchMode = true;
        SetContentView(_rootView);
        _rootView.SetOnApplyWindowInsetsListener(new InsetsPadder());
        _rootView.RequestApplyInsets();

        _content = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _content.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
        _rootView.AddView(_content);

        Render();
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    //  Navigation / result
    // ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The header's <c>‹</c> and a breadcrumb-segment tap both call this: pure navigation,
    /// one level up, finishing only once nothing is left to go up to.</summary>
    void GoUpOrFinish()
    {
        if (_current == _root) { Choose(null); return; }
        _current = ParentOf(_current) ?? _root;
        _filter = "";
        Render();
    }

    /// <summary>
    /// The system/gesture Back button. Unlike the header's own <c>‹</c> (which only ever steps up
    /// one level), this is treated as "leaving the screen," so it commits wherever the user has
    /// drilled to rather than silently discarding it — see the class remarks for why.
    /// </summary>
    public override void OnBackPressed() => Choose(_current == _root ? null : _current);

    /// <summary>Finishes the activity. <paramref name="folder"/> null means "nothing chosen" —
    /// <see cref="Result.Canceled"/>, no extra. Otherwise <see cref="Result.Ok"/> with
    /// <see cref="ExtraSelectedFolder"/> set.</summary>
    void Choose(string? folder)
    {
        if (folder is null) { SetResult(Result.Canceled); Finish(); return; }
        var data = new Intent();
        data.PutExtra(ExtraSelectedFolder, folder);
        SetResult(Result.Ok, data);
        Finish();
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    //  Render
    // ══════════════════════════════════════════════════════════════════════════════════════

    void Render()
    {
        _content.RemoveAllViews();

        if (_photos.Count == 0)
        {
            // Nothing has ever been scanned into the catalogue from this device — there is no
            // photo path to derive a folder from, so there is nothing to browse. Still exits
            // cleanly via the same Choose(null) path rather than a bare Finish().
            _content.AddView(Design.HeaderBar(this, "‹", () => Choose(null),
                "Folders", "Nothing scanned yet"));
            var empty = Design.Note(this, "Scan a folder first — then its contents show up here.");
            empty.SetPadding(Design.Dp(this, 16), Design.Dp(this, 16), Design.Dp(this, 16), Design.Dp(this, 16));
            _content.AddView(empty);
            return;
        }

        ComputeLevel();

        _content.AddView(BuildHeader());
        _content.AddView(BuildFilterField());

        var scroll = new ScrollView(this);
        scroll.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f);

        var body = new LinearLayout(this) { Orientation = Orientation.Vertical };

        _rowsContainer = new LinearLayout(this) { Orientation = Orientation.Vertical };
        body.AddView(_rowsContainer);
        RenderRows();

        var note = Design.Note(this,
            "Tap a folder to narrow the library to it. Tap its name in the breadcrumb to come back out.");
        note.SetPadding(Design.Dp(this, 16), Design.Dp(this, 11), Design.Dp(this, 16), Design.Dp(this, 11));
        body.AddView(note);
        body.AddView(Design.Rule(this, 2f, Design.Divider));

        body.AddView(BuildPhotosSection());

        scroll.AddView(body, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        _content.AddView(scroll);
    }

    /// <summary>Header: back, current folder name as title, tappable breadcrumb as subtitle.
    /// Built by hand rather than via <see cref="Design.HeaderBar"/> — see the class remarks.</summary>
    View BuildHeader()
    {
        var wrap = new LinearLayout(this) { Orientation = Orientation.Vertical };
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(Design.Dp(this, 16), Design.Dp(this, 6), Design.Dp(this, 16), Design.Dp(this, 12));

        var back = Design.IconButton(this, "‹");
        back.Click += (_, _) => GoUpOrFinish();
        back.LayoutParameters = new LinearLayout.LayoutParams(Design.Dp(this, 40), Design.Dp(this, 40))
        { RightMargin = Design.Dp(this, 10) };
        row.AddView(back);

        var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
        col.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        col.AddView(Design.Title(this, CurrentFolderName(), 20f));
        col.AddView(BuildBreadcrumb());
        row.AddView(col);

        wrap.AddView(row);
        wrap.AddView(Design.Rule(this, 2f, Design.Divider));
        return wrap;
    }

    /// <summary>
    /// "DCIM / 2019 · 4,110 photos" — every segment but the last is its own clickable
    /// <see cref="TextView"/> (bolded as a hint that it is tappable), jumping straight to that
    /// ancestor level. <see cref="Design.Note"/> supplies the exact typography the handoff's
    /// <c>.note</c> class uses, so splitting it into several views does not change how it reads.
    /// </summary>
    View BuildBreadcrumb()
    {
        var line = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        line.SetGravity(GravityFlags.CenterVertical);

        var segments = BreadcrumbSegments();
        for (int i = 0; i < segments.Count; i++)
        {
            if (i > 0) line.AddView(Design.Note(this, " / "));

            var (name, path) = segments[i];
            var seg = Design.Note(this, name);
            if (i < segments.Count - 1)
            {
                seg.SetTypeface(null, Android.Graphics.TypefaceStyle.Bold);
                seg.Click += (_, _) => { _current = path; _filter = ""; Render(); };
            }
            line.AddView(seg);
        }

        line.AddView(Design.Note(this, $" · {_currentSubtreeCount:N0} photos"));
        return line;
    }

    string CurrentFolderName()
    {
        var name = System.IO.Path.GetFileName(_current);
        return string.IsNullOrEmpty(name) ? _current : name;
    }

    /// <summary>Folder names from <see cref="_root"/> down through <see cref="_current"/>, paired
    /// with the full path tapping that name should jump to.</summary>
    List<(string Name, string Path)> BreadcrumbSegments()
    {
        var rootName = System.IO.Path.GetFileName(_root);
        var list = new List<(string, string)> { (string.IsNullOrEmpty(rootName) ? _root : rootName, _root) };

        if (_current != _root)
        {
            var rel = _current[_root.Length..].TrimStart('/');
            var acc = _root;
            foreach (var part in rel.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                acc = JoinPrefix(acc) + part;
                list.Add((part, acc));
            }
        }
        return list;
    }

    /// <summary>"Find a folder…" — narrows the row list only; the photos-directly-here grid below
    /// it is unaffected, since the filter is about finding a *child folder*, not a photo.</summary>
    View BuildFilterField()
    {
        var wrap = new LinearLayout(this) { Orientation = Orientation.Vertical };
        wrap.SetPadding(Design.Dp(this, 16), Design.Dp(this, 9), Design.Dp(this, 16), Design.Dp(this, 9));

        var field = Design.Field(this, "", 42f);
        field.Hint = "Find a folder…";
        field.TextChanged += (_, _) => { _filter = field.Text ?? ""; RenderRows(); };
        wrap.AddView(field);

        wrap.AddView(Design.Rule(this, 2f, Design.Divider));
        return wrap;
    }

    /// <summary>
    /// Populates <see cref="_rowsContainer"/> from the already-computed <see cref="_children"/>,
    /// filtered by <see cref="_filter"/>. Split out from <see cref="Render"/> so typing in the
    /// filter field only ever rebuilds this list, not the field itself — rebuilding the
    /// <see cref="EditText"/> on every keystroke would drop focus and the keyboard mid-type.
    /// </summary>
    void RenderRows()
    {
        _rowsContainer.RemoveAllViews();
        var prefix = JoinPrefix(_current);

        var names = _children.Keys
            .Where(n => _filter.Length == 0 || n.Contains(_filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

        var any = false;
        foreach (var name in names)
        {
            any = true;
            var agg = _children[name];
            var path = prefix + name;
            var isActive = _activeScope == path;

            var status = agg.AnyUnchecked ? "analysis hasn't reached here yet"
                        : agg.DupeCount > 0 ? $"{agg.DupeCount:N0} in a duplicate group"
                        : "checked, no duplicates";
            var trailing = isActive ? "Showing" : agg.AnyUnchecked ? "●" : "›";

            var row = Design.ListRow(this, name, $"{agg.Count:N0} · {status}", trailing, "📁",
                                     highlight: isActive);
            // Matches the handoff's opacity:.55 on an unchecked folder — and FiltersSheet's own
            // precedent for "this option exists but is not fully ready" (its unscored facets get
            // the same treatment).
            if (agg.AnyUnchecked) row.Alpha = 0.55f;
            row.Click += (_, _) => { _current = path; _filter = ""; Render(); };
            _rowsContainer.AddView(row);
        }

        if (!any)
        {
            var msg = Design.Note(this, _filter.Length > 0
                ? $"No folders match “{_filter}”."
                : "No subfolders here.");
            msg.SetPadding(Design.Dp(this, 16), Design.Dp(this, 12), Design.Dp(this, 16), Design.Dp(this, 12));
            _rowsContainer.AddView(msg);
        }
    }

    /// <summary>"Photos directly in &lt;folder&gt;" — the eyebrow plus a 3-up grid, 86dp rows
    /// (vs Library's 129dp square tiles) per the handoff.</summary>
    View BuildPhotosSection()
    {
        var wrap = new LinearLayout(this) { Orientation = Orientation.Vertical };

        var eyebrow = Design.Eyebrow(this, $"Photos directly in {CurrentFolderName()}");
        eyebrow.SetPadding(Design.Dp(this, 16), Design.Dp(this, 12), Design.Dp(this, 16), Design.Dp(this, 7));
        wrap.AddView(eyebrow);

        if (_directPhotos.Count == 0)
        {
            var note = Design.Note(this, "Nothing directly here — only inside its subfolders.");
            note.SetPadding(Design.Dp(this, 16), 0, Design.Dp(this, 16), Design.Dp(this, 16));
            wrap.AddView(note);
            return wrap;
        }

        var grid = new GridView(this) { NumColumns = 3 };
        var gap = Design.Dp(this, 2);
        grid.SetVerticalSpacing(gap);
        grid.SetHorizontalSpacing(gap);
        var colWidth = (Resources!.DisplayMetrics!.WidthPixels - Design.Dp(this, 4)) / 3;
        grid.SetColumnWidth(colWidth);
        grid.StretchMode = StretchMode.StretchColumnWidth;

        // ThumbAdapter sizes each cell cellPx×cellPx, but GridView's column-stretch mode
        // recalculates the actual on-screen column WIDTH from ColumnWidth/NumColumns regardless
        // of a cell's own requested width (the same mechanism Library's square grid already
        // depends on to fill the screen edge to edge) — only the HEIGHT a cell asks for survives
        // that override. So handing 86dp as cellPx here, instead of Library's width/3 square,
        // is what turns the same adapter into the handoff's shorter, still full-width, 3-across
        // strip rather than a resize of the square tiles.
        var rowH = Design.Dp(this, 86);
        grid.Adapter = new ThumbAdapter(this, _directPhotos, rowH, _catalog.ThumbDir);

        grid.ItemClick += (_, e) =>
        {
            var rec = _directPhotos[e.Position];
            var i = new Intent(this, typeof(PreviewActivity));
            i.PutExtra(PreviewActivity.ExtraImageId, rec.Id);
            StartActivity(i);
        };

        // GridView owns its own scrolling, which fights the ScrollView this section lives inside
        // (Design.cs has no non-scrolling grid primitive, and this is not enough content to
        // justify building one). Given a fixed height equal to its own content instead of
        // MatchParent/WrapContent, it never has anything left to scroll internally, so the outer
        // ScrollView is the only one that ever engages.
        var rows = (int)Math.Ceiling(_directPhotos.Count / 3.0);
        var totalHeight = rows * rowH + Math.Max(0, rows - 1) * gap;
        grid.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, totalHeight);

        wrap.AddView(grid);
        return wrap;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    //  Folder tree, derived from photo paths
    // ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// One linear pass over the whole catalogue: buckets every photo either into
    /// <see cref="_directPhotos"/> (it sits directly in <see cref="_current"/>) or into the
    /// <see cref="ChildAgg"/> for whichever immediate child folder it falls under — however many
    /// levels deeper the photo actually is, so a child's count is already its whole subtree, not
    /// just what is one level down.
    /// </summary>
    void ComputeLevel()
    {
        _children = new Dictionary<string, ChildAgg>(StringComparer.Ordinal);
        _directPhotos = [];
        var prefix = JoinPrefix(_current);

        foreach (var img in _photos)
        {
            if (!img.Path.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rest = img.Path[prefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash < 0) { _directPhotos.Add(img); continue; }

            var name = rest[..slash];
            if (!_children.TryGetValue(name, out var agg)) _children[name] = agg = new ChildAgg();
            agg.Count++;
            if (img.DupeCheckedAt is null) agg.AnyUnchecked = true;
            if (_assign.ContainsKey(img.Id)) agg.DupeCount++;
        }

        _currentSubtreeCount = _directPhotos.Count + _children.Values.Sum(a => a.Count);
    }

    /// <summary>The common ancestor directory of every photo path in the catalogue — see the
    /// <see cref="_root"/> doc for why this stands in for "the folder that was scanned."</summary>
    static string ComputeRoot(IReadOnlyList<ImageRecord> photos)
    {
        if (photos.Count == 0)
            return SnapZap.Core.Platform.DirectoryRoots.DefaultStart(
                SnapZap.Core.Platform.DirectoryPlatform.Android);

        var root = System.IO.Path.GetDirectoryName(photos[0].Path) ?? "/";
        foreach (var img in photos)
        {
            while (!(img.Path == root || img.Path.StartsWith(JoinPrefix(root), StringComparison.Ordinal)))
            {
                var parent = System.IO.Path.GetDirectoryName(root);
                if (string.IsNullOrEmpty(parent) || parent == root) { root = "/"; break; }
                root = parent;
            }
        }
        return root;
    }

    static bool IsUnderOrEqual(string path, string root) =>
        path == root || path.StartsWith(JoinPrefix(root), StringComparison.Ordinal);

    static string? ParentOf(string path) => System.IO.Path.GetDirectoryName(path.TrimEnd('/'));

    static string JoinPrefix(string dir) => dir.EndsWith('/') ? dir : dir + "/";

    sealed class InsetsPadder : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
        {
            var bars = insets.GetInsets(WindowInsets.Type.SystemBars());
            v.SetPadding(0, bars.Top, 0, bars.Bottom);
            return insets;
        }
    }

    protected override void OnDestroy()
    {
        _catalog?.Dispose();
        base.OnDestroy();
    }
}
