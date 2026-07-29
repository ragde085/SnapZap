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
/// Swipe-based duplicate review (ROADMAP item 11): one photo at a time, swipe right to keep, left
/// to mark for removal. This is the touch expression of the desktop's <c>DupeReview.razor</c> —
/// the same group-at-a-time motion, which is the core motion of the app and the one a flat grid
/// cannot express.
///
/// <para><b>Two safety rules are load-bearing here, and neither is re-derived locally.</b></para>
///
/// <para>1. Only groups whose kind is <c>IsBulkSelectable()</c> — <c>Exact</c> and <c>Variant</c> —
/// are offered for review. A <c>Burst</c> is five <i>different photographs</i> taken seconds apart;
/// sweeping those into a removal decision is what would make this a shredder rather than a
/// deduplicator. The gate is read from <c>DupeKindExtensions</c>, exactly as CLAUDE.md requires of
/// every other caller, so a future kind cannot become swipeable by default.</para>
///
/// <para>2. A group's keeper is never offered for removal, and every group always retains at least
/// one member. Swiping is fast and per-photo, so unlike multi-select there is no selection state
/// to review before committing — which means the invariant has to hold at the point of the
/// gesture, not at the point of a later confirmation.</para>
///
/// <para>Nothing here deletes. A left swipe records an intent, which the caller acts on through
/// <c>DeleteService</c> and its undo log. That separation is deliberate: it keeps the destructive
/// step behind the same hash-verified, reversible path the desktop uses.</para>
/// </summary>
[Activity(Label = "Review duplicates",
          Theme = "@android:style/Theme.Material.Light.NoActionBar")]
public sealed class ReviewActivity : Activity
{
    AndroidCatalog _catalog = null!;
    readonly List<Candidate> _queue = [];
    int _index;

    ImageView _photo = null!;
    TextView _caption = null!, _tally = null!;
    int _kept;
    readonly List<long> _markedIds = [];
    Button _commit = null!;
    string? _lastDeletedBatch;
    TextView _root_hint = null!;

    /// <summary>One reviewable photo: a non-keeper in a bulk-selectable group.</summary>
    sealed record Candidate(long ImageId, long GroupId, DupeKind Kind, string Path, string? ThumbPath);

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _catalog = new AndroidCatalog();

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetPadding(24, 24, 24, 24);
        root.FocusableInTouchMode = true;
        SetContentView(root);
        root.SetOnApplyWindowInsetsListener(new InsetsPadder());
        root.RequestApplyInsets();

        _caption = new TextView(this);
        _caption.SetTextSize(Android.Util.ComplexUnitType.Sp, 15f);
        root.AddView(_caption);

        _photo = new ImageView(this);
        _photo.SetScaleType(ImageView.ScaleType.FitCenter);
        _photo.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f);
        root.AddView(_photo);

        _tally = new TextView(this);
        _tally.SetTextSize(Android.Util.ComplexUnitType.Sp, 13f);
        root.AddView(_tally);

        _root_hint = new TextView(this) { Text = "Swipe ▶ keep · ◀ mark · ▼ delete now" };
        _root_hint.SetTextSize(Android.Util.ComplexUnitType.Sp, 12f);
        root.AddView(_root_hint);

        var buttons = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var remove = new Button(this) { Text = "◀  Remove" };
        var keep = new Button(this) { Text = "Keep  ▶" };
        remove.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        keep.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        remove.Click += (_, _) => Decide(keepIt: false);
        keep.Click += (_, _) => Decide(keepIt: true);
        buttons.AddView(remove);
        buttons.AddView(keep);
        root.AddView(buttons);

        _commit = new Button(this) { Text = "Nothing marked", Enabled = false };
        _commit.Click += (_, _) => ConfirmAndDelete();
        root.AddView(_commit);

        // Buttons as well as swipe, deliberately: the gesture is the primary motion, but a
        // destructive action reachable only by an unlabelled gesture is undiscoverable, and the
        // buttons double as the accessibility path.
        _photo.SetOnTouchListener(new SwipeListener(this));

        LoadQueue();
        ShowCurrent();
    }

    void LoadQueue()
    {
        var groups = new DupeRepository(_catalog.Db).Groups();
        var images = _catalog.Images.All().ToDictionary(i => i.Id);

        // One assignment per photo, resolved in Core with GroupReconciler's precedence, exactly as
        // the desktop's AppState does. Deliberately shared rather than reimplemented here: a photo
        // can belong to several groups at once (a Variant group that is a strict superset of a
        // Burst group survives reconciliation), and picking the wrong one is what made every frame
        // of a five-frame burst fixture eligible for bulk deletion. Precedence puts Burst ahead of
        // Variant, so those frames now present as Burst and fail the IsBulkSelectable() gate.
        //
        // Reading it from DupeAssignmentResolver is the point — two heads applying two subtly
        // different rules to a delete decision is exactly the class of bug this codebase keeps
        // designing out (cf. NsfwDecision, InScope).
        var assignment = DupeAssignmentResolver.Resolve(groups, _catalog.Images.BurstAdjacentIds());

        foreach (var (imageId, a) in assignment)
        {
            // One predicate, shared with the desktop: not the keeper, a bulk-selectable kind, and
            // not burst-adjacent. The third condition is the one that catches a burst frame which
            // complete linkage kept out of the clique and Variant then claimed.
            if (!a.IsBulkSelectableExtra) continue;
            if (!images.TryGetValue(imageId, out var rec)) continue;
            _queue.Add(new Candidate(imageId, a.GroupId, a.Kind, rec.Path, ThumbFor(rec)));
        }

        // Stable order so the queue is a function of the catalogue, not of dictionary iteration.
        _queue.Sort((x, y) => x.GroupId != y.GroupId
            ? x.GroupId.CompareTo(y.GroupId)
            : string.CompareOrdinal(x.Path, y.Path));
    }

    string? ThumbFor(ImageRecord rec)
    {
        if (!string.IsNullOrEmpty(rec.ThumbPath) && File.Exists(rec.ThumbPath)) return rec.ThumbPath;
        var h = rec.ContentHash;
        return h.Length < 2 ? null : System.IO.Path.Combine(_catalog.ThumbDir, h[..2], h + ".jpg");
    }

    void ShowCurrent()
    {
        if (_index >= _queue.Count)
        {
            _caption.Text = _queue.Count == 0
                ? "No duplicates to review.\n\nRun a scan first, or there are none in this folder."
                : "Review complete.";
            _photo.SetImageBitmap(null);
            _tally.Text = $"Kept {_kept} · marked for removal {_markedIds.Count}";
            _commit.Enabled = _markedIds.Count > 0;
            _commit.Text = _markedIds.Count > 0
                ? $"Move {_markedIds.Count} to trash"
                : "Nothing marked";
            return;
        }

        var c = _queue[_index];
        _caption.Text = $"{c.Kind} duplicate · {_index + 1} of {_queue.Count}\n{System.IO.Path.GetFileName(c.Path)}";
        _tally.Text = $"Kept {_kept} · marked {_markedIds.Count}";

        _photo.SetImageBitmap(LoadFull(c));
    }

    /// <summary>
    /// Decodes the original at roughly screen size rather than showing the grid thumbnail.
    /// Reviewing duplicates is the one place the pixels themselves are the decision — a 256px
    /// thumbnail of two near-identical frames tells you nothing about which is sharper.
    /// </summary>
    /// <remarks>
    /// Downsampled via <c>inSampleSize</c> instead of decoding full-resolution: a 24 MP original
    /// is ~96 MB as an ARGB_8888 bitmap and a handful of those is an OutOfMemoryError on any
    /// phone. Falls back to the cached thumbnail when the original will not decode, so a corrupt
    /// or unreadable file is a degraded frame rather than a dead screen.
    /// </remarks>
    Bitmap? LoadFull(Candidate c)
    {
        try
        {
            if (File.Exists(c.Path))
            {
                var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
                BitmapFactory.DecodeFile(c.Path, bounds);

                var target = Math.Max(Resources!.DisplayMetrics!.WidthPixels, 1);
                int sample = 1;
                while (bounds.OutWidth / (sample * 2) >= target) sample *= 2;

                var bmp = BitmapFactory.DecodeFile(c.Path, new BitmapFactory.Options { InSampleSize = sample });
                if (bmp is not null) return bmp;
            }
        }
        catch (Exception ex) { Android.Util.Log.Warn("SnapZap", $"full decode failed: {ex.Message}"); }

        try { if (c.ThumbPath is not null && File.Exists(c.ThumbPath)) return BitmapFactory.DecodeFile(c.ThumbPath); }
        catch (Exception) { }
        return null;
    }

    /// <summary>
    /// Deletes the photo on screen immediately — the swipe-down gesture.
    /// </summary>
    /// <remarks>
    /// <para>Distinct from a left swipe on purpose. Left marks, and marks are committed later in
    /// one confirmed batch; down is "this one, now", for when the comparison already made the
    /// answer obvious and queueing it would just be ceremony.</para>
    ///
    /// <para>"Immediately" still means <c>DeleteService.RecycleAsync</c>: the file moves to the
    /// app trash, an undo_log row is written, and it is restorable from Trash &amp; history. There
    /// is no path in this app that hard-deletes a photo, and adding a fast gesture is not a reason
    /// to open one — so this needs no confirmation dialog, because it is not irreversible.</para>
    /// </remarks>
    async void DeleteNow()
    {
        if (_index >= _queue.Count) return;
        var c = _queue[_index];

        try
        {
            var result = await Task.Run(() => _catalog.NewDeleteService().RecycleAsync([c.ImageId]));
            _lastDeletedBatch = result.Recycled > 0 ? result.BatchId : null;

            Toast.MakeText(this,
                result.Recycled > 0
                    ? $"Moved {System.IO.Path.GetFileName(c.Path)} to trash"
                    : "Could not move that photo to the trash",
                ToastLength.Short)!.Show();

            // Drop it from the queue rather than advancing past it: it no longer exists to review,
            // and leaving it in would make "3 of 7" count photos that are gone.
            _queue.RemoveAt(_index);
            ShowCurrent();
        }
        catch (Exception ex)
        {
            Toast.MakeText(this, $"Delete failed: {ex.Message}", ToastLength.Long)!.Show();
            Android.Util.Log.Error("SnapZap", ex.ToString());
        }
    }

    /// <summary>
    /// Records a decision. Marking does <b>not</b> delete — it flips the row's keeper flag off and
    /// leaves the file untouched; the removal itself goes through <c>DeleteService</c>, which
    /// recycles rather than hard-deletes and writes an undo-log entry.
    /// </summary>
    void Decide(bool keepIt)
    {
        if (_index >= _queue.Count) return;
        var c = _queue[_index];

        if (keepIt)
        {
            // Promote to keeper. ToggleKeeper is the repository's own operation, so the "at least
            // one keeper per group" rule stays where the rest of the app enforces it.
            try { new DupeRepository(_catalog.Db).ToggleKeeper(c.GroupId, c.ImageId); }
            catch (Exception ex) { Android.Util.Log.Warn("SnapZap", $"ToggleKeeper failed: {ex.Message}"); }
            _kept++;
        }
        else _markedIds.Add(c.ImageId);

        _index++;
        ShowCurrent();
    }

    /// <summary>
    /// Commits the marked set. Deliberately a separate, confirmed step rather than something a
    /// left swipe does directly: swiping is fast and per-photo, and a gesture that deleted on
    /// contact would have no moment at which the user can see what they are about to lose.
    /// </summary>
    void ConfirmAndDelete()
    {
        if (_markedIds.Count == 0) return;

        new AlertDialog.Builder(this)
            .SetTitle("Move to trash?")!
            .SetMessage($"{_markedIds.Count} photo(s) will be moved to SnapZap's trash.\n\n"
                      + "Nothing is deleted from your device — the files are moved inside the app "
                      + "and can be restored from History.")!
            .SetNegativeButton("Cancel", (System.EventHandler<DialogClickEventArgs>?)null)!
            .SetPositiveButton("Move to trash", (s, e) => RunDelete())!
            .Show();
    }

    async void RunDelete()
    {
        var ids = _markedIds.ToList();
        _commit.Enabled = false;
        _commit.Text = "Moving to trash…";

        try
        {
            // DeleteService, not File.Delete: it recycles rather than hard-deletes, writes an
            // undo_log row per file so the batch can be restored, and prunes the catalogue row
            // while leaving the thumbnail in place — which is what lets History show a preview of
            // something that no longer has an images row (ROADMAP item 13).
            var result = await Task.Run(() => _catalog.NewDeleteService().RecycleAsync(ids));

            _markedIds.Clear();
            _caption.Text = "Moved to trash.";
            _tally.Text = $"{result.Recycled} moved"
                        + (result.Failed > 0 ? $" · {result.Failed} failed" : "")
                        + $" · batch {result.BatchId}";
            _commit.Text = "Undo — restore this batch";
            _commit.Enabled = result.Recycled > 0;
            _commit.Click += (_, _) => RunUndo(result.BatchId);

            Android.Util.Log.Info("SnapZap", $"recycled {result.Recycled}, failed {result.Failed}, batch {result.BatchId}");
        }
        catch (Exception ex)
        {
            _tally.Text = $"Delete failed: {ex.Message}";
            _commit.Enabled = true;
            _commit.Text = $"Move {ids.Count} to trash";
            Android.Util.Log.Error("SnapZap", ex.ToString());
        }
    }

    async void RunUndo(string batchId)
    {
        _commit.Enabled = false;
        _commit.Text = "Restoring…";
        try
        {
            var r = await Task.Run(() => _catalog.NewDeleteService().RestoreAsync(batchId));
            _tally.Text = $"Restored {r.Restored}" + (r.Missing > 0 ? $" · {r.Missing} missing" : "");
            _commit.Text = "Restored";
            _caption.Text = "Restored. Re-scan to see them in the grid again.";
        }
        catch (Exception ex)
        {
            _tally.Text = $"Restore failed: {ex.Message}";
            Android.Util.Log.Error("SnapZap", ex.ToString());
        }
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
                    // stray touch cannot mark or delete a photo.
                    if (Math.Abs(dx) < Threshold && Math.Abs(dy) < Threshold) return true;

                    // Whichever axis moved further wins, so a sloppy diagonal resolves to the
                    // gesture the user was more clearly making rather than to whichever axis is
                    // tested first. Down deletes; a stray upward flick does nothing at all.
                    if (Math.Abs(dy) > Math.Abs(dx))
                    {
                        if (dy > 0) owner.DeleteNow();
                    }
                    else owner.Decide(keepIt: dx > 0);
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
            v.SetPadding(24 + bars.Left, 24 + bars.Top, 24 + bars.Right, 24 + bars.Bottom);
            return insets;
        }
    }

    protected override void OnDestroy()
    {
        _catalog.Dispose();
        base.OnDestroy();
    }
}
