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
/// Screen 10 of the mobile handoff — the photo preview: full-bleed image with the details pulled
/// up from the bottom.
/// </summary>
/// <remarks>
/// <para>Opened by tapping a tile in the library grid. Swipe sideways steps through the same list
/// the grid is showing, so the preview inherits whatever filter or folder scope produced it rather
/// than re-querying the whole catalogue — stepping out of the set you were looking at is
/// disorienting and is not what the gesture promises.</para>
///
/// <para>The image is decoded downsampled via <c>inSampleSize</c>: a 24 MP original is ~96 MB as
/// an ARGB_8888 bitmap, and stepping through a handful at full resolution is an OutOfMemoryError
/// on any phone.</para>
/// </remarks>
[Activity(Label = "Photo", Theme = "@android:style/Theme.Material.Light.NoActionBar")]
public sealed class PreviewActivity : Activity
{
    /// <summary>Image id to open at. The rest of the list is re-derived from the catalogue.</summary>
    public const string ExtraImageId = "snapzap.preview.imageId";

    AndroidCatalog _catalog = null!;
    List<ImageRecord> _items = [];
    Dictionary<long, DupeAssignment> _dupes = [];
    int _index;

    FrameLayout _host = null!;
    View? _undoBar;
    bool _deleting;

    ImageView _photo = null!;
    LinearLayout _detail = null!;
    TextView _counter = null!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _catalog = new AndroidCatalog();

        _items = _catalog.ScopedImages.ToList();
        try
        {
            _dupes = DupeAssignmentResolver.Resolve(
                new DupeRepository(_catalog.Db).Groups(), _catalog.Images.BurstAdjacentIds());
        }
        catch (Exception ex) { Android.Util.Log.Warn("SnapZap", $"dupe resolve failed: {ex.Message}"); }

        var openAt = Intent?.GetLongExtra(ExtraImageId, -1) ?? -1;
        _index = Math.Max(0, _items.FindIndex(i => i.Id == openAt));

        // FrameLayout host so the undo bar floats over the photo instead of pushing it, the same
        // shape ReviewActivity uses for the same reason.
        _host = new FrameLayout(this);
        SetContentView(_host);

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(Design.Neutral300);   // the handoff darkens the frame behind the photo
        root.FocusableInTouchMode = true;
        root.LayoutParameters = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
        _host.AddView(root);
        _host.SetOnApplyWindowInsetsListener(new InsetsPadder());
        _host.RequestApplyInsets();

        // top bar — back, position counter
        var top = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        top.SetGravity(GravityFlags.CenterVertical);
        top.SetPadding(Design.Dp(this, 16), Design.Dp(this, 4), Design.Dp(this, 16), Design.Dp(this, 10));
        var back = Design.IconButton(this, "‹", "Back");
        back.Click += (_, _) => Finish();
        top.AddView(back);
        _counter = Design.Note(this, "");
        _counter.SetPadding(Design.Dp(this, 10), 0, 0, 0);
        _counter.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        top.AddView(_counter);

        // Asked for explicitly: the preview could show you a photo you had clearly finished with and
        // offer nothing to do about it. Reversible like every other delete here — it goes to the
        // trash, and the undo bar below puts Restore within reach of the tap that caused it.
        var del = Design.Button(this, "Delete", Design.Btn.Secondary, 40f);
        del.SetTextSize(Android.Util.ComplexUnitType.Sp, 13f);
        del.SetTextColor(Design.Accent700);
        del.Click += (_, _) => DeleteCurrent();
        top.AddView(del);
        root.AddView(top);

        // the photo itself, full bleed
        _photo = new ImageView(this);
        _photo.SetScaleType(ImageView.ScaleType.FitCenter);
        _photo.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f);
        _photo.SetOnTouchListener(new StepListener(this));
        root.AddView(_photo);

        // details sheet
        _detail = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _detail.SetBackgroundColor(Design.Bg);
        root.AddView(_detail);

        Show();
    }

    void Show()
    {
        if (_items.Count == 0) { Finish(); return; }
        _index = Math.Clamp(_index, 0, _items.Count - 1);
        var rec = _items[_index];

        _counter.Text = $"{_index + 1:N0} of {_items.Count:N0}";
        _photo.SetImageBitmap(LoadDownsampled(rec));
        _photo.ContentDescription =
            $"{System.IO.Path.GetFileName(rec.Path)}, {_index + 1:N0} of {_items.Count:N0}. "
            + "Swipe left or right for the next photo.";

        _detail.RemoveAllViews();
        _detail.AddView(Design.Rule(this, 2f, Design.Text));

        var pad = new LinearLayout(this) { Orientation = Orientation.Vertical };
        pad.SetPadding(Design.Dp(this, 16), Design.Dp(this, 10), Design.Dp(this, 16), Design.Dp(this, 12));

        pad.AddView(Design.Body(this, System.IO.Path.GetFileName(rec.Path), 15f, bold: true));
        pad.AddView(Design.Note(this, System.IO.Path.GetDirectoryName(rec.Path) ?? ""));
        pad.AddView(Design.Gap(this, 10));

        pad.AddView(Design.KeyValue(this, "Dimensions",
            rec.Width is { } w && rec.Height is { } h ? $"{w:N0} × {h:N0}" : "—"));
        pad.AddView(Design.KeyValue(this, "File size", FormatBytes(rec.FileSize)));
        pad.AddView(Design.KeyValue(this, "Taken",
            rec.ExifTaken is { } t
                ? DateTimeOffset.FromUnixTimeSeconds(t).ToLocalTime().ToString("d MMM yyyy, HH:mm")
                : "Not recorded"));
        pad.AddView(Design.KeyValue(this, "Camera", rec.ExifCamera ?? "Not recorded"));
        pad.AddView(Design.KeyValue(this, "Sharpness",
            rec.BlurScore is { } b ? b.ToString("F0") : "Not scored"));
        pad.AddView(Design.KeyValue(this, "Content review",
            rec.NsfwScore is { } n ? n.ToString("F3") : "Not checked yet"));

        // Duplicate standing, when the photo has any. Read from the shared resolver rather than
        // re-derived, so this agrees with the review queue and the counts on the library.
        if (_dupes.TryGetValue(rec.Id, out var d))
        {
            pad.AddView(Design.Gap(this, 11));
            var callout = Design.Callout(this);
            callout.AddView(Design.Body(this,
                $"Group {d.GroupId} · {d.Kind.ToString().ToLowerInvariant()}", 13f, bold: true));
            callout.AddView(Design.Note(this,
                d.Kind == DupeKind.Burst
                    ? "A burst frame — a separate photograph. No bulk command will pick it."
                    : d.IsKeeper ? "This is the copy being kept."
                                 : "An extra copy. The group is keeping a different one.",
                Design.Accent700));
            pad.AddView(callout);
        }

        pad.AddView(Design.Gap(this, 9));
        pad.AddView(Design.Note(this, "Swipe sideways to step through · Delete moves this photo to the trash · back to close"));
        _detail.AddView(pad);
    }

    /// <summary>
    /// Moves the photo on screen to the trash and steps to the next one.
    /// </summary>
    /// <remarks>
    /// <para>Advances rather than closing. Deleting from a preview is usually one of a run — you are
    /// stepping through and clearing as you go — and dropping back to the grid after each one would
    /// make the second deletion cost four taps instead of one. When the list empties, there is
    /// nothing left to show and the screen closes.</para>
    ///
    /// <para>No confirmation dialog, deliberately, and the undo bar is why: a modal on a reversible
    /// action trains people to dismiss modals, whereas an undo that is already on screen costs
    /// nothing to ignore and one tap to use. Same reasoning as the review queue's swipe-down.</para>
    /// </remarks>
    async void DeleteCurrent()
    {
        if (_deleting || _items.Count == 0) return;
        var rec = _items[_index];
        _deleting = true;

        try
        {
            var result = await Task.Run(() => _catalog.NewDeleteService().RecycleAsync([rec.Id]));
            if (result.Recycled == 0)
            {
                Toast.MakeText(this, "Could not move that photo to the trash", ToastLength.Short)!.Show();
                return;
            }

            // Drop it from the local list rather than re-querying: the list is the set the grid handed
            // over, and re-reading the catalogue here would silently widen it past whatever filter or
            // folder scope produced this preview.
            _items.RemoveAt(_index);
            if (_items.Count == 0)
            {
                Toast.MakeText(this, $"Moved {System.IO.Path.GetFileName(rec.Path)} to trash — "
                                   + "restore it from History", ToastLength.Long)!.Show();
                Finish();
                return;
            }

            Show();
            ShowUndo($"Moved {System.IO.Path.GetFileName(rec.Path)} to trash", result.BatchId);
        }
        catch (Exception ex)
        {
            Toast.MakeText(this, $"Delete failed: {ex.Message}", ToastLength.Long)!.Show();
            Android.Util.Log.Error("SnapZap", ex.ToString());
        }
        finally { _deleting = false; }
    }

    /// <summary>Floats an undo bar over the photo. Mirrors <c>ReviewActivity.ShowUndo</c>.</summary>
    void ShowUndo(string message, string batchId)
    {
        DismissUndo();

        var bar = Design.InkBar(this, message, "Undo", async () =>
        {
            DismissUndo();
            try
            {
                var r = await Task.Run(() => _catalog.NewDeleteService().RestoreAsync(batchId));
                // The row went with the file on delete, so the file coming back is not enough.
                await _catalog.RescanAfterRestoreAsync();
                Toast.MakeText(this, r.Restored > 0 ? "Restored" : "Nothing left to restore",
                    ToastLength.Short)!.Show();
            }
            catch (Exception ex)
            {
                Toast.MakeText(this, $"Restore failed: {ex.Message}", ToastLength.Long)!.Show();
                Android.Util.Log.Error("SnapZap", ex.ToString());
            }
            // The restored photo belongs back in the list, and its position is the catalogue's to
            // decide, so re-read the scoped set rather than guessing where to reinsert it.
            _items = _catalog.ScopedImages.ToList();
            if (_items.Count == 0) { Finish(); return; }
            _index = Math.Clamp(_index, 0, _items.Count - 1);
            Show();
        });

        bar.LayoutParameters = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            Gravity = GravityFlags.Bottom,
            LeftMargin = Design.Dp(this, 12),
            RightMargin = Design.Dp(this, 12),
            BottomMargin = Design.Dp(this, 20),
        };

        _host.AddView(bar);
        _undoBar = bar;
        bar.Alpha = 0f;
        bar.TranslationY = Design.Dp(this, 20);
        bar.Animate()!.Alpha(1f).TranslationY(0f).SetDuration(180).Start();
        bar.PostDelayed(() => { if (_undoBar == bar) DismissUndo(); }, 6000);
    }

    void DismissUndo()
    {
        if (_undoBar is null) return;
        var bar = _undoBar;
        _undoBar = null;
        bar.Animate()!.Alpha(0f).SetDuration(140)
            .WithEndAction(new Java.Lang.Runnable(() => _host.RemoveView(bar)))!.Start();
    }

    /// <summary>Decodes at roughly screen width. See the class remarks for why never full-size.</summary>
    Bitmap? LoadDownsampled(ImageRecord rec)
    {
        try
        {
            if (File.Exists(rec.Path))
            {
                var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
                BitmapFactory.DecodeFile(rec.Path, bounds);
                var target = Math.Max(Resources!.DisplayMetrics!.WidthPixels, 1);
                int sample = 1;
                while (bounds.OutWidth / (sample * 2) >= target) sample *= 2;
                var bmp = BitmapFactory.DecodeFile(rec.Path, new BitmapFactory.Options { InSampleSize = sample });
                if (bmp is not null) return bmp;
            }
        }
        catch (Exception ex) { Android.Util.Log.Warn("SnapZap", $"preview decode failed: {ex.Message}"); }

        // A photo that will not decode still has a catalogue row worth showing, so fall back to
        // its thumbnail rather than leaving the frame blank with no explanation.
        try
        {
            var h = rec.ContentHash;
            if (h.Length >= 2)
            {
                var thumb = System.IO.Path.Combine(_catalog.ThumbDir, h[..2], h + ".jpg");
                if (File.Exists(thumb)) return BitmapFactory.DecodeFile(thumb);
            }
        }
        catch (Exception) { }
        return null;
    }

    static string FormatBytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):0.##} MB",
        >= 1L << 10 => $"{b / (double)(1L << 10):0.##} KB",
        _ => $"{b} B",
    };

    /// <summary>Horizontal swipe steps through the list; the design's "swipe sideways".</summary>
    sealed class StepListener(PreviewActivity owner) : Java.Lang.Object, View.IOnTouchListener
    {
        float _downX;
        const float Threshold = 100f;

        public bool OnTouch(View? v, MotionEvent? e)
        {
            if (e is null) return false;
            switch (e.Action)
            {
                case MotionEventActions.Down: _downX = e.GetX(); return true;
                case MotionEventActions.Up:
                    var dx = e.GetX() - _downX;
                    if (Math.Abs(dx) >= Threshold)
                    {
                        // Left drags forward, matching how every photo viewer behaves: the content
                        // follows the finger.
                        owner._index += dx < 0 ? 1 : -1;
                        owner.Show();
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
