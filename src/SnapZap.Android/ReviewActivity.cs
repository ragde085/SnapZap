using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using SnapZap.Core;
using SnapZap.Core.Data;
using SnapZap.Core.Dedup;
using SnapZap.Core.Delete;

namespace SnapZap.Droid;

/// <summary>
/// Screens 5 and 6 of the mobile handoff: the duplicate review queue, one <b>group</b> at a time
/// rather than one photo at a time. This is the touch expression of the desktop's
/// <c>DupeReview.razor</c> and the screen the handoff singles out as "the reason to build this" —
/// on a phone the whole job collapses to a queue you can clear one-handed, keeper pre-picked, one
/// primary button, swipe to advance.
/// </summary>
/// <remarks>
/// <para><b>The unit of the queue is a group, not a candidate.</b> A previous version of this
/// screen queued individual bulk-selectable photos and swiped keep/remove one at a time; the
/// handoff's screens 5-6 instead show one whole duplicate group per step — the keeper large, its
/// reason stated, the extras as a tappable filmstrip — because that is what actually lets someone
/// "just confirm" rather than re-litigate every photo. <b>Nothing on this screen deletes anything.</b>
/// Screen 5's own footer says so: "this only decides which copy survives an export or delete." The
/// only destructive action left is the swipe-down-to-delete carried over from the previous
/// implementation, and it goes through <see cref="DeleteService.RecycleAsync"/> exactly as before.</para>
///
/// <para><b>Two safety rules are load-bearing here, and neither is re-derived locally.</b></para>
///
/// <para>1. A group only enters this queue if it actually has something to decide. For
/// <see cref="DupeKind.Exact"/>/<see cref="DupeKind.Variant"/> groups that means at least one member
/// whose resolved <see cref="DupeAssignment.IsBulkSelectableExtra"/> is true — read straight off
/// <see cref="DupeAssignmentResolver"/>, never re-derived, exactly as CLAUDE.md requires of every
/// other caller. <see cref="DupeKind.Burst"/> groups always enter regardless, because nothing is
/// ever pre-picked for them and reviewing them is the point.</para>
///
/// <para>2. A group's keeper is never something this screen invents. Every keeper flip goes through
/// <see cref="DupeRepository.ToggleKeeper"/>, the one place the "a group always keeps at least one"
/// invariant is enforced — see <see cref="PromoteToKeeper"/> and <see cref="CommitBurstChoice"/> for
/// how the ordering of those calls respects that guard instead of tripping it.</para>
/// </remarks>
[Activity(Label = "Review duplicates",
          Theme = "@android:style/Theme.Material.Light.NoActionBar")]
public sealed class ReviewActivity : Activity
{
    // A soft/sharp split for the burst filmstrip's caption. Mirrors the desktop's
    // ImageView.BlurFlagThreshold (App/Services/ImageView.cs) — a value this build cannot read
    // directly, since the Android head cannot reference SnapZap.App (docs/ANDROID-PORT-ACS.md
    // §1.5). Android has no per-user override for it yet, so this is the same number, not a
    // re-derivation of the rule; if the desktop default ever moves, move this too.
    const double BurstSoftThreshold = 100;

    AndroidCatalog _catalog = null!;
    LinearLayout _root = null!;

    readonly List<GroupEntry> _queue = [];
    int _index;
    int _initialCount;

    /// <summary>Extra (non-keeper, bulk-selectable) images across the whole queue, counted once at
    /// load. Shown as the header's context line on screen 5 — a static "how much is queued" figure
    /// rather than a live "marked so far" tally, since this build has no library-wide running
    /// counter the way the desktop's AppState does. Documented deviation, not an oversight.</summary>
    int _totalExtras;

    /// <summary>Which images are currently ticked on the burst grid, local to the on-screen group
    /// until committed — see <see cref="CommitBurstChoice"/>. Tapping is cheap precisely because it
    /// does not write to the catalogue on every tap.</summary>
    readonly HashSet<long> _burstChosen = [];
    long? _burstChosenForGroup;

    /// <summary>One image inside a reviewable group, with just the fields this screen renders.</summary>
    sealed record MemberInfo(
        long ImageId, string Path, string? ThumbPath, bool IsKeeper,
        int? Width, int? Height, long FileSize, long? ExifTaken, string? ExifCamera, double? BlurScore);

    /// <summary>One step in the queue: the single group a set of images resolve to under
    /// <see cref="DupeAssignmentResolver"/>, plus the member details needed to render it.</summary>
    sealed record GroupEntry(long GroupId, DupeKind Kind, List<MemberInfo> Members);

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _catalog = new AndroidCatalog();

        _root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _root.SetBackgroundColor(Design.Bg);
        _root.FocusableInTouchMode = true;
        SetContentView(_root);

        // Top/bottom insets only, no horizontal — the header, progress bar and action bar are
        // meant to run edge to edge (their own children carry the 16dp gutter), matching
        // MainActivity's InsetsPadder rather than this screen's own previous 24dp-all-round one.
        _root.SetOnApplyWindowInsetsListener(new InsetsPadder());
        _root.RequestApplyInsets();

        LoadQueue();
        _initialCount = _queue.Count;
        Render();
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    //  Building the queue
    // ══════════════════════════════════════════════════════════════════════════════════════

    void LoadQueue()
    {
        var groups = new DupeRepository(_catalog.Db).Groups();
        var images = _catalog.Images.All().ToDictionary(i => i.Id);
        var assignment = DupeAssignmentResolver.Resolve(groups, _catalog.Images.BurstAdjacentIds());

        // Recover, per resolved group id, the full set of images presented under it. This is what
        // "the one group a photo is presented as belonging to" means for a *group-at-a-time*
        // queue: a step here is exactly the images whose resolved assignment points at that group
        // id, not the raw dupe_groups row, which may have members the resolver routed elsewhere
        // (a Variant group that is a strict superset of a Burst group — see DupeAssignment's own
        // class doc for why that split matters).
        var byGroup = new Dictionary<long, List<(long ImageId, DupeAssignment A)>>();
        foreach (var (imageId, a) in assignment)
        {
            if (!byGroup.TryGetValue(a.GroupId, out var list))
                byGroup[a.GroupId] = list = [];
            list.Add((imageId, a));
        }

        var built = new List<GroupEntry>();
        foreach (var (groupId, members) in byGroup)
        {
            if (members.Count < 2) continue;   // not a duplicate of anything once this thin
            var kind = members[0].A.Kind;

            // The safety-critical gate: a bulk-selectable kind only queues when there is something
            // left to decide. Read straight from IsBulkSelectableExtra — never re-derived — so a
            // group whose extras were already recycled elsewhere does not clutter the queue with
            // nothing to do. Burst groups always queue: nothing is ever pre-picked for them, so
            // "is there something to decide" is always true.
            if (kind != DupeKind.Burst && !members.Any(m => m.A.IsBulkSelectableExtra)) continue;

            var infos = new List<MemberInfo>();
            foreach (var (imageId, a) in members)
            {
                if (!images.TryGetValue(imageId, out var rec)) continue;
                infos.Add(new MemberInfo(imageId, rec.Path, ThumbFor(rec), a.IsKeeper,
                    rec.Width, rec.Height, rec.FileSize, rec.ExifTaken, rec.ExifCamera, rec.BlurScore));
            }
            if (infos.Count < 2) continue;

            built.Add(new GroupEntry(groupId, kind, infos));
            if (kind != DupeKind.Burst)
                _totalExtras += infos.Count(m => !m.IsKeeper);
        }

        // Burst groups come last in the queue, deliberately (handoff: "at the end on purpose") —
        // the decisive, pre-picked groups are the quick wins; bursts need an actual look. Stable
        // order otherwise, so the queue is a function of the catalogue, not of dictionary iteration.
        _queue.AddRange(built.OrderBy(g => g.Kind == DupeKind.Burst ? 1 : 0).ThenBy(g => g.GroupId));
    }

    string? ThumbFor(ImageRecord rec)
    {
        if (!string.IsNullOrEmpty(rec.ThumbPath) && File.Exists(rec.ThumbPath)) return rec.ThumbPath;
        var h = rec.ContentHash;
        return h.Length < 2 ? null : System.IO.Path.Combine(_catalog.ThumbDir, h[..2], h + ".jpg");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    //  Render — the whole screen is rebuilt per group, same idiom as MainActivity.Render()
    // ══════════════════════════════════════════════════════════════════════════════════════

    void Render()
    {
        _root.RemoveAllViews();

        if (_index >= _queue.Count) { RenderDone(); return; }
        var g = _queue[_index];

        // Seed the local tick-set from the catalogue only when we land on a *different* group —
        // re-rendering after a tap on this same group must not stomp the tap that caused it.
        if (g.Kind == DupeKind.Burst && _burstChosenForGroup != g.GroupId)
        {
            _burstChosen.Clear();
            _burstChosen.UnionWith(g.Members.Where(m => m.IsKeeper).Select(m => m.ImageId));
            _burstChosenForGroup = g.GroupId;
        }

        var tag = Design.Tag(this, KindTag(g.Kind), accent: g.Kind != DupeKind.Burst);
        var subtitle = g.Kind == DupeKind.Burst ? BurstSubtitle() : ExtrasSubtitle();
        _root.AddView(Design.HeaderBar(this, "✕", Finish,
            $"GROUP {_index + 1} OF {_queue.Count}", subtitle, tag));

        var (track, fill) = Design.ProgressBar(this, 4f);
        _root.AddView(track);
        SetProgress(fill, (_index + 1.0) / _queue.Count);

        if (g.Kind == DupeKind.Burst) RenderBurstGroup(g);
        else RenderExtrasGroup(g);
    }

    /// <summary>
    /// The Fill view's width is set in real pixels rather than a percentage because it sits inside
    /// a FrameLayout (<see cref="Design.ProgressBar"/> gives it <c>LayoutParams(0, MatchParent)</c>
    /// precisely so callers finish the job here). <see cref="_root"/> carries no horizontal padding
    /// (see the insets comment in <see cref="OnCreate"/>), so the device width is the track's width.
    /// </summary>
    void SetProgress(View fill, double fraction)
    {
        var w = (int)Math.Round(Resources!.DisplayMetrics!.WidthPixels * Math.Clamp(fraction, 0.0, 1.0));
        fill.LayoutParameters = new FrameLayout.LayoutParams(w, ViewGroup.LayoutParams.MatchParent);
    }

    string KindTag(DupeKind k) => k switch
    {
        DupeKind.Exact => "Exact copy",
        DupeKind.Variant => "Same shot",
        _ => "Burst",
    };

    string ExtrasSubtitle() => _totalExtras == 1
        ? "1 extra copy queued for review"
        : $"{_totalExtras:N0} extra copies queued for review";

    string BurstSubtitle()
    {
        var burstTotal = _queue.Count(g => g.Kind == DupeKind.Burst);
        return burstTotal == 1
            ? "1 burst group, at the end on purpose"
            : $"{burstTotal:N0} burst groups, at the end on purpose";
    }

    void RenderDone()
    {
        _root.AddView(Design.HeaderBar(this, "✕", Finish, "Review", null, null));

        var body = new LinearLayout(this) { Orientation = Orientation.Vertical };
        body.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f);
        body.SetGravity(GravityFlags.Center);
        var pad = Design.Dp(this, 24);
        body.SetPadding(pad, pad, pad, pad);

        body.AddView(Design.Title(this, _initialCount == 0 ? "Nothing to review." : "Review complete.", 24f));
        body.AddView(Design.Gap(this, 10));
        body.AddView(Design.Note(this, _initialCount == 0
            ? "Run a scan first, or there are no duplicate groups in this folder yet."
            : "Every group has a keeper. Extras are ready for Export or the Select mode's bulk "
            + "delete — nothing was deleted from here."));
        body.AddView(Design.Gap(this, 20));

        var done = Design.Button(this, "Done", Design.Btn.Primary, 52f);
        done.LayoutParameters = Design.Wide();
        done.Click += (_, _) => Finish();
        body.AddView(done);

        _root.AddView(body);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    //  Screen 5 · a same-shot / exact-copy group — keeper large, extras as a filmstrip
    // ══════════════════════════════════════════════════════════════════════════════════════

    void RenderExtrasGroup(GroupEntry g)
    {
        // DupeAssignmentResolver guarantees a group always keeps at least one keeper; the
        // fallback here is defence-in-depth for the in-memory copy, not a second source of truth.
        var keeper = g.Members.FirstOrDefault(m => m.IsKeeper) ?? g.Members[0];
        var extras = g.Members.Where(m => !m.IsKeeper).ToList();

        var scroll = new ScrollView(this);
        scroll.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f);
        var body = new LinearLayout(this) { Orientation = Orientation.Vertical };
        scroll.AddView(body, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        var pad = Design.Dp(this, 16);

        if (extras.Count > 0)
        {
            var kRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            kRow.SetGravity(GravityFlags.CenterVertical);
            kRow.SetPadding(pad, Design.Dp(this, 12), pad, Design.Dp(this, 9));
            kRow.AddView(Design.Tag(this, "Keeping this one", accent: true));
            var reason = Design.Body(this, KeeperReason(g, keeper), 11.5f, bold: true);
            reason.SetTextColor(Design.Accent700);
            reason.SetPadding(Design.Dp(this, 8), 0, 0, 0);
            kRow.AddView(reason);
            body.AddView(kRow);
        }
        else
        {
            var kRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            kRow.SetPadding(pad, Design.Dp(this, 12), pad, Design.Dp(this, 9));
            kRow.AddView(Design.Tag(this, "Keeping this one", accent: true));
            body.AddView(kRow);
        }

        // The large keeper photo, 3dp accent border (.tl border:3px solid var(--color-accent)).
        // The mock's tile is flex:1 — it fills whatever vertical space is left; inside a scrolling
        // column that has to become a fixed height instead, chosen tall enough to actually judge
        // sharpness against the filmstrip below.
        var frame = new FrameLayout(this);
        frame.Background = Design.Outline(Color.Transparent, Design.Accent, Design.Dp(this, 3));
        frame.SetPadding(Design.Dp(this, 3), Design.Dp(this, 3), Design.Dp(this, 3), Design.Dp(this, 3));
        frame.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Design.Dp(this, 320))
        { LeftMargin = pad, RightMargin = pad };

        var keeperImage = new ImageView(this);
        keeperImage.SetScaleType(ImageView.ScaleType.CenterCrop);
        keeperImage.LayoutParameters = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
        var bmp = LoadPhoto(keeper);
        if (bmp is not null) keeperImage.SetImageBitmap(bmp);
        // Swipe left/right/down live on the keeper photo, matching the handoff's own framing —
        // this is the one large, focused image on the screen. The burst grid has no equivalent
        // single "current photo" (it is a 2-column grid of equally-weighted frames), so it gets
        // tap-to-toggle only; see RenderBurstGroup.
        keeperImage.SetOnTouchListener(new SwipeListener(this));
        frame.AddView(keeperImage);
        body.AddView(frame);
        body.AddView(Design.Gap(this, 10));

        var kv = new LinearLayout(this) { Orientation = Orientation.Vertical };
        kv.SetPadding(pad, 0, pad, 0);
        kv.AddView(Design.KeyValue(this, "Resolution", Resolution(keeper)));
        kv.AddView(Design.KeyValue(this, "File size", FormatBytes(keeper.FileSize)));
        kv.AddView(Design.KeyValue(this, "Where", ShortPath(keeper.Path)));
        body.AddView(kv);

        var note = Design.Note(this, DistinguishNote(g));
        note.SetPadding(pad, Design.Dp(this, 8), pad, Design.Dp(this, 2));
        body.AddView(note);

        if (extras.Count > 0)
        {
            var eyeWrap = new LinearLayout(this) { Orientation = Orientation.Vertical };
            eyeWrap.SetPadding(pad, Design.Dp(this, 10), pad, 0);
            eyeWrap.AddView(Design.Eyebrow(this,
                $"the {extras.Count} extra{(extras.Count == 1 ? "" : "s")} — tap one to keep it instead"));
            body.AddView(eyeWrap);
            body.AddView(Design.Gap(this, 6));

            var strip = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            strip.SetPadding(pad, 0, pad, pad);
            for (int i = 0; i < extras.Count; i++)
            {
                var ex = extras[i];
                var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
                col.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f)
                { RightMargin = i == extras.Count - 1 ? 0 : Design.Dp(this, 8) };

                var iv = new ImageView(this);
                iv.SetScaleType(ImageView.ScaleType.CenterCrop);
                iv.LayoutParameters = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent, Design.Dp(this, 58));
                var thumb = LoadThumbBitmap(ex);
                if (thumb is not null) iv.SetImageBitmap(thumb);
                var tappedId = ex.ImageId;
                iv.Click += (_, _) => PromoteToKeeper(tappedId);
                col.AddView(iv);

                var caption = Design.Note(this, $"{Resolution(ex)} · {FormatBytes(ex.FileSize)}");
                caption.SetPadding(0, Design.Dp(this, 3), 0, 0);
                col.AddView(caption);

                strip.AddView(col);
            }
            body.AddView(strip);
        }
        else
        {
            var allKept = Design.Note(this, "All copies in this group are marked as keepers now.");
            allKept.SetPadding(pad, Design.Dp(this, 10), pad, pad);
            body.AddView(allKept);
        }

        _root.AddView(scroll);

        var bar = Design.ActionBar(this);
        var ab = Design.ActionBarBody(bar);

        var primary = Design.Button(this, "Keep it — next group", Design.Btn.Primary, 52f);
        primary.LayoutParameters = Design.Wide();
        primary.Click += (_, _) => Advance();
        ab.AddView(primary);
        ab.AddView(Design.Gap(this, 8));

        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var skip = Design.Button(this, "Skip", Design.Btn.Secondary, 44f);
        skip.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f)
        { RightMargin = Design.Dp(this, 8) };
        skip.Click += (_, _) => Advance();
        row.AddView(skip);

        var keepAll = Design.Button(this, $"Keep all {g.Members.Count}", Design.Btn.Secondary, 44f);
        keepAll.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        keepAll.Enabled = extras.Count > 0;
        keepAll.Click += (_, _) => KeepAllExtras(g);
        row.AddView(keepAll);
        ab.AddView(row);

        var hint = Design.Note(this,
            "Swipe left to keep and advance · right to go back. Nothing is deleted here — this "
            + "only decides which copy survives an export or delete.");
        hint.Gravity = GravityFlags.CenterHorizontal;
        hint.SetPadding(0, Design.Dp(this, 9), 0, 0);
        ab.AddView(hint);

        _root.AddView(bar);
    }

    /// <summary>
    /// Describes why the current keeper is the one kept, mirroring — not re-implementing —
    /// <c>StoreGroups.Keeper</c>'s <c>HighestResolution</c> rule ("most pixels, then largest
    /// file"): if resolution actually differs across the group, credit resolution; otherwise the
    /// file size is what a plain manual override would notice.
    /// </summary>
    static string KeeperReason(GroupEntry g, MemberInfo keeper)
    {
        var pixelsDiffer = g.Members.Any(m => Pixels(m) != Pixels(keeper));
        var word = pixelsDiffer ? "largest" : "biggest";
        return $"{word} of {g.Members.Count}";
    }

    static long Pixels(MemberInfo m) => (long)(m.Width ?? 0) * (m.Height ?? 0);

    /// <summary>
    /// The one-line explanation of what actually distinguishes the copies (handoff: "Same date and
    /// camera on all three, so only size tells them apart."), derived from the EXIF fields on the
    /// group's own members rather than hardcoded — a group that differs by camera or date gets a
    /// different sentence instead of a copy-pasted one that happens to be wrong for it.
    /// </summary>
    static string DistinguishNote(GroupEntry g)
    {
        // Day-level, not exact timestamp: two re-encodes of the same photo can carry EXIF
        // timestamps a few seconds apart purely from re-processing, which would otherwise read as
        // "different dates" for what is obviously the same shot.
        var dayKeys = g.Members.Select(m => m.ExifTaken is { } t ? t / 86_400 : (long?)null).Distinct().ToList();
        var cameras = g.Members.Select(m => m.ExifCamera ?? "").Distinct().ToList();
        var dims = g.Members.Select(m => (m.Width, m.Height)).Distinct().ToList();

        var sameDate = dayKeys.Count == 1 && dayKeys[0] is not null;
        var sameDims = dims.Count == 1;

        // ⚠ Absence of a field is not a difference between fields, and conflating the two makes
        // the app state something false with total confidence. A group where nothing carries
        // camera EXIF — screenshots, exports, anything stripped — has `cameras == [""]`, which is
        // one distinct value but not evidence of anything. Reporting that as "different cameras
        // recorded these" is a confident lie in a tool whose next screen deletes a photo.
        var known = cameras.Where(c => c.Length > 0).ToList();
        var sameCamera = known.Count == 1 && known.Count == cameras.Count;
        var differentCameras = known.Count > 1;

        if (sameDate && sameCamera && !sameDims)
            return "Same date and camera on every copy, so only size tells them apart.";
        if (sameDate && sameCamera && sameDims)
            return "Same date, camera and dimensions — only file size differs.";
        if (dayKeys.All(d => d is not null) && !sameDate)
            return "These were taken on different dates.";
        if (differentCameras)
            return "Different cameras recorded these.";
        if (!sameDims)
            return "Same shot at different sizes — the largest is kept.";
        // Nothing on record distinguishes them, which for byte-identical copies is the truth
        // rather than a gap: say so plainly instead of implying a difference nobody can see.
        return known.Count == 0 && dayKeys.All(d => d is null)
            ? "No date or camera recorded on any of these copies."
            : "These copies differ, but not in an obvious way.";
    }

    void PromoteToKeeper(long imageId)
    {
        var g = _queue[_index];
        var repo = new DupeRepository(_catalog.Db);

        // ToggleKeeper flips exactly one member and nothing else. The design shows a single
        // keeper at a time, so promote the tapped extra *first* — bringing the group briefly to
        // two keepers, which the repository allows (see DupeReviewResources:
        // "toggle a keeper (more than one can survive)") — and only then demote the previous one.
        // Demoting first would trip ToggleKeeper's "never turn off the group's last keeper" guard
        // whenever there was exactly one keeper to begin with, which is the common case.
        repo.ToggleKeeper(g.GroupId, imageId);
        foreach (var old in g.Members.Where(m => m.IsKeeper && m.ImageId != imageId))
            repo.ToggleKeeper(g.GroupId, old.ImageId);

        _queue[_index] = g with
        {
            Members = g.Members.Select(m => m with { IsKeeper = m.ImageId == imageId }).ToList()
        };
        Render();
    }

    void KeepAllExtras(GroupEntry g)
    {
        var repo = new DupeRepository(_catalog.Db);
        // Only ever turning keepers *on* here — never off — so the "at least one keeper" guard
        // is never in play; there is no ordering to get wrong.
        foreach (var m in g.Members.Where(m => !m.IsKeeper))
            repo.ToggleKeeper(g.GroupId, m.ImageId);

        _queue[_index] = g with { Members = g.Members.Select(m => m with { IsKeeper = true }).ToList() };
        Advance();
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    //  Screen 6 · a burst group — nothing pre-picked, no bulk path, ever
    // ══════════════════════════════════════════════════════════════════════════════════════

    void RenderBurstGroup(GroupEntry g)
    {
        var scroll = new ScrollView(this);
        scroll.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f);
        var body = new LinearLayout(this) { Orientation = Orientation.Vertical };
        scroll.AddView(body, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        var pad = Design.Dp(this, 16);

        var headWrap = new LinearLayout(this) { Orientation = Orientation.Vertical };
        headWrap.SetPadding(pad, Design.Dp(this, 13), pad, Design.Dp(this, 13));
        headWrap.AddView(Design.Body(this, BurstHeadline(g), 15f, bold: true));
        headWrap.AddView(Design.Note(this,
            "These are different photographs, not copies — so nothing is pre-picked and no bulk "
            + "command will ever touch them. Pick the ones worth keeping, or keep the lot."));
        body.AddView(headWrap);
        body.AddView(Design.Rule(this, 2f, Design.Divider));

        // .g3-style 2-column grid, 2dp gutter, tap to toggle.
        var grid = new LinearLayout(this) { Orientation = Orientation.Vertical };
        for (int i = 0; i < g.Members.Count; i += 2)
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.LayoutParameters = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
            { BottomMargin = Design.Dp(this, 2) };

            row.AddView(BurstTile(g.Members[i], rightGutter: true));
            row.AddView(i + 1 < g.Members.Count ? BurstTile(g.Members[i + 1], rightGutter: false)
                                                 : Filler());
            grid.AddView(row);
        }
        body.AddView(grid);

        var counter = Design.Note(this, $"tap to toggle · {_burstChosen.Count} of {g.Members.Count} chosen");
        counter.SetPadding(pad, Design.Dp(this, 9), pad, Design.Dp(this, 9));
        body.AddView(counter);

        _root.AddView(scroll);

        var bar = Design.ActionBar(this);
        var ab = Design.ActionBarBody(bar);

        var primaryLabel = _burstChosen.Count == 1
            ? "Keep the 1 I chose — next group"
            : $"Keep the {_burstChosen.Count} I chose — next group";
        var primary = Design.Button(this, primaryLabel, Design.Btn.Primary, 52f);
        primary.LayoutParameters = Design.Wide();
        primary.Click += (_, _) => CommitBurstChoice(g);
        ab.AddView(primary);
        ab.AddView(Design.Gap(this, 8));

        var row2 = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var keepAll = Design.Button(this, $"Keep all {g.Members.Count}", Design.Btn.Secondary, 44f);
        keepAll.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f)
        { RightMargin = Design.Dp(this, 8) };
        keepAll.Click += (_, _) => KeepAllBurst(g);
        row2.AddView(keepAll);

        var later = Design.Button(this, "Decide later", Design.Btn.Secondary, 44f);
        later.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        later.Click += (_, _) => Advance();
        row2.AddView(later);
        ab.AddView(row2);

        _root.AddView(bar);
    }

    View BurstTile(MemberInfo m, bool rightGutter)
    {
        var frame = new FrameLayout(this);
        frame.LayoutParameters = new LinearLayout.LayoutParams(0, Design.Dp(this, 160), 1f)
        { RightMargin = rightGutter ? Design.Dp(this, 2) : 0 };

        var iv = new ImageView(this);
        iv.SetScaleType(ImageView.ScaleType.CenterCrop);
        iv.LayoutParameters = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
        var thumb = LoadThumbBitmap(m);
        if (thumb is not null) iv.SetImageBitmap(thumb);
        frame.AddView(iv);

        var badge = Design.TileBadge(this, "✓", _burstChosen.Contains(m.ImageId));
        badge.LayoutParameters = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Right,
            TopMargin = Design.Dp(this, 5),
            RightMargin = Design.Dp(this, 5),
        };
        frame.AddView(badge);

        var caption = new TextView(this) { Text = BurstCaption(m) };
        caption.SetTextSize(Android.Util.ComplexUnitType.Sp, 10f);
        caption.SetTypeface(null, TypefaceStyle.Bold);
        caption.SetTextColor(Color.White);
        caption.Background = Design.Fill(Color.Argb(220, 0x20, 0x1e, 0x1d));
        caption.SetPadding(Design.Dp(this, 4), Design.Dp(this, 1), Design.Dp(this, 4), Design.Dp(this, 1));
        caption.LayoutParameters = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        {
            Gravity = GravityFlags.Bottom | GravityFlags.Left,
            LeftMargin = Design.Dp(this, 5),
            BottomMargin = Design.Dp(this, 4),
        };
        frame.AddView(caption);

        var id = m.ImageId;
        frame.Click += (_, _) => ToggleBurstChoice(id);
        return frame;
    }

    /// <summary>An empty half of the last row when a burst has an odd frame count.</summary>
    View Filler()
    {
        var v = new View(this);
        v.LayoutParameters = new LinearLayout.LayoutParams(0, Design.Dp(this, 160), 1f);
        return v;
    }

    static string BurstHeadline(GroupEntry g)
    {
        var times = g.Members.Where(m => m.ExifTaken is not null).Select(m => m.ExifTaken!.Value).ToList();
        var count = g.Members.Count;
        var noun = count switch
        {
            2 => "Two frames", 3 => "Three frames", 4 => "Four frames", 5 => "Five frames",
            6 => "Six frames", 7 => "Seven frames", 8 => "Eight frames", _ => $"{count} frames",
        };
        if (times.Count < 2) return $"{noun}, moments apart.";

        var span = times.Max() - times.Min();
        return span == 1 ? $"{noun}, one second apart." : $"{noun}, {span} seconds apart.";
    }

    static string BurstCaption(MemberInfo m)
    {
        var time = m.ExifTaken is { } t
            ? DateTimeOffset.FromUnixTimeSeconds(t).ToLocalTime().ToString("HH:mm:ss")
            : "unknown time";
        var sharpness = m.BlurScore is { } b
            ? (b >= BurstSoftThreshold ? $"sharp {b:0}" : $"soft {b:0}")
            : "unscored";
        return $"{time} · {sharpness}";
    }

    void ToggleBurstChoice(long imageId)
    {
        if (!_burstChosen.Remove(imageId))
        {
            _burstChosen.Add(imageId);
        }
        else if (_burstChosen.Count == 0)
        {
            // Mirrors the repository's own "a group always keeps at least one" rule locally, so
            // the counter and the primary button never promise a commit that CommitBurstChoice's
            // own ToggleKeeper calls would then silently refuse.
            _burstChosen.Add(imageId);
        }
        Render();
    }

    void CommitBurstChoice(GroupEntry g)
    {
        var repo = new DupeRepository(_catalog.Db);
        // Turn newly-chosen frames on before turning newly-unchosen ones off, for the same reason
        // as PromoteToKeeper: turning off first can trip ToggleKeeper's "never leave a group with
        // zero keepers" guard on a group that currently has only one.
        foreach (var m in g.Members.Where(m => _burstChosen.Contains(m.ImageId) && !m.IsKeeper))
            repo.ToggleKeeper(g.GroupId, m.ImageId);
        foreach (var m in g.Members.Where(m => !_burstChosen.Contains(m.ImageId) && m.IsKeeper))
            repo.ToggleKeeper(g.GroupId, m.ImageId);

        _queue[_index] = g with
        {
            Members = g.Members.Select(m => m with { IsKeeper = _burstChosen.Contains(m.ImageId) }).ToList()
        };
        Advance();
    }

    void KeepAllBurst(GroupEntry g)
    {
        var repo = new DupeRepository(_catalog.Db);
        foreach (var m in g.Members.Where(m => !m.IsKeeper))
            repo.ToggleKeeper(g.GroupId, m.ImageId);

        _queue[_index] = g with { Members = g.Members.Select(m => m with { IsKeeper = true }).ToList() };
        Advance();
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    //  Navigation and the one destructive action (swipe down)
    // ══════════════════════════════════════════════════════════════════════════════════════

    void Advance()
    {
        if (_index < _queue.Count) _index++;
        Render();
    }

    void Back()
    {
        if (_index > 0) _index--;
        Render();
    }

    /// <summary>
    /// Deletes the photo currently framed large — the swipe-down gesture carried over from the
    /// previous version of this screen.
    /// </summary>
    /// <remarks>
    /// <para>Distinct from every other action here on purpose: everything else on this screen only
    /// changes which copy is flagged as the keeper. This is the one path that actually removes a
    /// file, and it still goes through <see cref="DeleteService.RecycleAsync"/> — recycled, not
    /// hard-deleted, undo-loggable, restorable from Trash &amp; history — so adding a fast gesture
    /// here never opens a path around that.</para>
    ///
    /// <para>Scoped to the keeper screen only. The burst grid has no single "current photo" — it
    /// is a 2-column grid of equally-weighted frames — so a swipe over it would be ambiguous about
    /// which frame it targets; tap-to-toggle is that screen's only gesture.</para>
    /// </remarks>
    async void DeleteCurrentKeeperPhoto()
    {
        if (_index >= _queue.Count) return;
        var g = _queue[_index];
        if (g.Kind == DupeKind.Burst) return;

        var keeper = g.Members.FirstOrDefault(m => m.IsKeeper);
        if (keeper is null) return;

        try
        {
            var result = await Task.Run(() => _catalog.NewDeleteService().RecycleAsync([keeper.ImageId]));
            Toast.MakeText(this,
                result.Recycled > 0
                    ? $"Moved {System.IO.Path.GetFileName(keeper.Path)} to trash"
                    : "Could not move that photo to the trash",
                ToastLength.Short)!.Show();

            var remaining = g.Members.Where(m => m.ImageId != keeper.ImageId).ToList();
            if (remaining.Count < 2)
            {
                // No longer a duplicate of anything once one side is gone.
                _queue.RemoveAt(_index);
            }
            else
            {
                // The row that carried the keeper flag is gone from the catalogue (cascaded with
                // the deleted images row, not merely flipped), so mirror
                // DupeAssignmentResolver's own read-time fallback here: if nothing survives with
                // the flag, the lowest-id survivor stands in for it. This is a display-only
                // fallback, same as the resolver's — no repository write, because there is
                // nothing wrong to correct in the catalogue.
                if (!remaining.Any(m => m.IsKeeper))
                {
                    var promoteId = remaining.Min(m => m.ImageId);
                    remaining = remaining.Select(m => m with { IsKeeper = m.ImageId == promoteId }).ToList();
                }
                _queue[_index] = g with { Members = remaining };
            }
            Render();
        }
        catch (Exception ex)
        {
            Toast.MakeText(this, $"Delete failed: {ex.Message}", ToastLength.Long)!.Show();
            Android.Util.Log.Error("SnapZap", ex.ToString());
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    //  Small helpers
    // ══════════════════════════════════════════════════════════════════════════════════════

    static string Resolution(MemberInfo m) => m.Width is { } w && m.Height is { } h ? $"{w} × {h}" : "Unknown";

    static string ShortPath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 3 ? string.Join("/", parts) : string.Join("/", parts[^3..]);
    }

    /// <summary>Matches the desktop's and TrashActivity's own formatting so the same number reads
    /// the same everywhere.</summary>
    static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.##} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.##} KB",
        _ => $"{bytes} B",
    };

    /// <summary>
    /// Decodes the original at roughly screen size rather than the grid thumbnail — reviewing
    /// duplicates is the one place the pixels themselves are the decision. Downsampled via
    /// <c>inSampleSize</c>, never full-resolution: a 24 MP original is ~96 MB as an ARGB_8888
    /// bitmap, an OutOfMemoryError waiting to happen. Falls back to the cached thumbnail when the
    /// original will not decode.
    /// </summary>
    Bitmap? LoadPhoto(MemberInfo m)
    {
        try
        {
            if (File.Exists(m.Path))
            {
                var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
                BitmapFactory.DecodeFile(m.Path, bounds);

                var target = Math.Max(Resources!.DisplayMetrics!.WidthPixels, 1);
                int sample = 1;
                while (bounds.OutWidth / (sample * 2) >= target) sample *= 2;

                var bmp = BitmapFactory.DecodeFile(m.Path, new BitmapFactory.Options { InSampleSize = sample });
                if (bmp is not null) return bmp;
            }
        }
        catch (Exception ex) { Android.Util.Log.Warn("SnapZap", $"full decode failed: {ex.Message}"); }

        return LoadThumbBitmap(m);
    }

    /// <summary>The cached grid thumbnail — cheap, used for the filmstrip and the burst grid,
    /// where the pixels themselves are not the point the way the keeper's own photo is.</summary>
    Bitmap? LoadThumbBitmap(MemberInfo m)
    {
        try { if (m.ThumbPath is not null && File.Exists(m.ThumbPath)) return BitmapFactory.DecodeFile(m.ThumbPath); }
        catch (Exception) { }
        return null;
    }

    sealed class SwipeListener(ReviewActivity owner) : Java.Lang.Object, View.IOnTouchListener
    {
        float _downX, _downY;
        const float Threshold = 120f;

        public bool OnTouch(View? v, MotionEvent? e)
        {
            if (e is null) return false;
            switch (e.Action)
            {
                case MotionEventActions.Down:
                    _downX = e.GetX();
                    _downY = e.GetY();
                    return true;

                case MotionEventActions.Up:
                    var dx = e.GetX() - _downX;
                    var dy = e.GetY() - _downY;

                    // A short drag is not a decision. Below the threshold nothing happens, so a
                    // stray touch cannot advance, rewind or delete anything.
                    if (Math.Abs(dx) < Threshold && Math.Abs(dy) < Threshold) return true;

                    // Whichever axis moved further wins, so a sloppy diagonal resolves to the
                    // gesture the user was more clearly making. Down deletes; a stray upward
                    // flick does nothing at all — there is no "un-delete via swipe" gesture.
                    if (Math.Abs(dy) > Math.Abs(dx))
                    {
                        if (dy > 0) owner.DeleteCurrentKeeperPhoto();
                    }
                    else
                    {
                        // Handoff: "Swipe left to keep and advance · right to go back" — the
                        // opposite of the old per-photo queue's right-to-keep convention, because
                        // there everything is already pre-kept and swiping is navigation, not a
                        // removal decision.
                        if (dx < 0) owner.Advance();
                        else owner.Back();
                    }
                    return true;
            }
            return false;
        }
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

    protected override void OnDestroy()
    {
        _catalog.Dispose();
        base.OnDestroy();
    }
}
