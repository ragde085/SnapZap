using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Graphics;
using Android.Widget;
using SnapZap.Core.Delete;

namespace SnapZap.Droid;

/// <summary>
/// Trash and delete history: how much the trash holds, what was deleted and when, restore a batch,
/// and empty the trash.
///
/// <para><b>Why Android needs this screen and the desktop does not.</b> On Windows and macOS
/// SnapZap recycles into the OS trash, and the OS already provides the read-out, the restore and
/// the empty — Explorer's Recycle Bin and Finder's Trash. Android has no equivalent for an
/// app-private directory: nothing in the system will ever show the user that
/// <c>files/SnapZap/trash</c> is holding two gigabytes of photos they deleted last week. So the
/// app has to supply what the OS supplies elsewhere. This is a genuine platform difference, not a
/// gap in parity — the underlying operations are the shared Core ones
/// (<c>DeleteService</c>, <c>FolderTrashService</c>), only the surface is Android-specific.</para>
///
/// <para>The delete-history half <i>is</i> shared with the desktop's <c>UndoDialog</c>: same
/// <c>DeleteService.Batches()</c>, same <c>RestoreAsync</c>, same <c>undo_log</c>.</para>
/// </summary>
[Activity(Label = "Trash & history",
          Theme = "@android:style/Theme.Material.Light.NoActionBar")]
public sealed class TrashActivity : Activity
{
    AndroidCatalog _catalog = null!;
    LinearLayout _root = null!;
    TextView _summary = null!;
    LinearLayout _batchList = null!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _catalog = new AndroidCatalog();

        _root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _root.SetPadding(24, 24, 24, 24);
        _root.FocusableInTouchMode = true;

        var scroll = new ScrollView(this);
        scroll.AddView(_root, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        SetContentView(scroll);
        scroll.SetOnApplyWindowInsetsListener(new InsetsPadder());
        scroll.RequestApplyInsets();

        _summary = Text("", 15f);
        _root.AddView(Text("Trash", 22f, bold: true));
        _root.AddView(_summary);

        _root.AddView(Button("Empty trash", ConfirmEmpty));

        _root.AddView(Text("Delete history", 18f, bold: true));
        _root.AddView(Text(
            "Deleting moves photos into SnapZap's own trash — it never removes them from your "
            + "device. Restoring puts them back where they were.", 12f));

        _batchList = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _root.AddView(_batchList);

        Refresh();
    }

    void Refresh()
    {
        var (files, bytes) = new FolderTrashService(_catalog.TrashDir).Measure();
        _summary.Text = files == 0
            ? "Empty — nothing is being held."
            : $"{files} file(s) · {FormatBytes(bytes)} held in app storage.";

        _batchList.RemoveAllViews();
        var svc = _catalog.NewDeleteService();
        var batches = svc.Batches();

        if (batches.Count == 0)
        {
            _batchList.AddView(Text("Nothing deleted yet.", 13f));
            return;
        }

        foreach (var b in batches)
        {
            var when = DateTimeOffset.FromUnixTimeSeconds(b.Ts).ToLocalTime();
            var row = new LinearLayout(this) { Orientation = Orientation.Vertical };
            row.SetPadding(0, 16, 0, 16);

            row.AddView(Text($"{b.Total} photo(s) · {when:yyyy-MM-dd HH:mm}"
                           + (b.Restored > 0 ? $" · {b.Restored} restored" : ""), 14f));

            var batchButtons = new LinearLayout(this) { Orientation = Orientation.Horizontal };

            // A fully-restored batch has nothing left to put back, so offering the button would be
            // a control that reports success without doing anything.
            if (b.Restored < b.Total)
            {
                var restore = Button($"Restore {b.Total - b.Restored}", null);
                restore.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
                restore.Click += async (_, _) =>
                {
                    restore.Enabled = false;
                    restore.Text = "Restoring…";
                    try
                    {
                        var r = await Task.Run(() => svc.RestoreAsync(b.BatchId));
                        Toast.MakeText(this,
                            r.Missing > 0
                                ? $"Restored {r.Restored}; {r.Missing} no longer in the trash"
                                : $"Restored {r.Restored}",
                            ToastLength.Long)!.Show();
                    }
                    catch (Exception ex)
                    {
                        Toast.MakeText(this, $"Restore failed: {ex.Message}", ToastLength.Long)!.Show();
                        Android.Util.Log.Error("SnapZap", ex.ToString());
                    }
                    Refresh();
                };
                batchButtons.AddView(restore);
            }

            var forgetBatch = Button("Remove all", () => ConfirmForgetBatch(b));
            forgetBatch.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            batchButtons.AddView(forgetBatch);
            row.AddView(batchButtons);

            // Per-photo controls. A batch is whatever one delete happened to sweep up, which is not
            // necessarily a set the user wants to act on as a unit — "restore that one" and "get rid
            // of that one for good" are both ordinary asks, and only offering batch-level actions
            // would force an all-or-nothing choice over a grouping the app invented.
            foreach (var item in svc.ItemsInBatch(b.BatchId, 50))
            {
                var itemRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                itemRow.SetGravity(GravityFlags.CenterVertical);

                // The thumbnail is the whole point of history being browsable: a filename tells you
                // nothing about whether you want a photo back. This is also the one place the
                // "delete the catalogue row, keep the thumbnail" rule pays off — the images row is
                // gone, so there is no ImageRecord to ask, and undo_log.content_hash plus the
                // content-addressed cache is all that is left to render from. Anything that later
                // prunes "orphaned" thumbnails breaks exactly this screen.
                var thumb = new ImageView(this);
                thumb.SetScaleType(ImageView.ScaleType.CenterCrop);
                thumb.LayoutParameters = new LinearLayout.LayoutParams(140, 140) { RightMargin = 16 };

                var bmp = LoadThumb(item.ContentHash);
                if (bmp is not null) thumb.SetImageBitmap(bmp);
                else
                {
                    // Same treatment as the desktop's .undo-thumb-missing: a visible, explained tile
                    // rather than an empty gap. Entries deleted before content_hash was recorded can
                    // never resolve a thumbnail — the images row is gone, so the hash is
                    // unrecoverable — and rendering nothing made a working screen look broken.
                    thumb.SetBackgroundColor(Color.ParseColor("#22000000"));
                    thumb.ContentDescription = "No preview available for this entry";
                }
                itemRow.AddView(thumb);

                var name = Text(System.IO.Path.GetFileName(item.OriginalPath)
                              + (item.Restored ? "  (restored)" : ""), 12f);
                name.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.4f);
                itemRow.AddView(name);

                if (!item.Restored)
                {
                    var one = Button("Restore", null);
                    // Smaller text and equal weights so the label fits on one line next to the
                    // thumbnail — at the default size "Restore" wrapped to "RESTOR / E".
                    one.SetTextSize(Android.Util.ComplexUnitType.Sp, 11f);
                    one.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
                    one.Click += async (_, _) =>
                    {
                        one.Enabled = false;
                        var ok = await Task.Run(() => svc.RestoreItemAsync(item.Id));
                        Toast.MakeText(this, ok ? "Restored" : "No longer in the trash", ToastLength.Short)!.Show();
                        Refresh();
                    };
                    itemRow.AddView(one);
                }

                var forget = Button("Remove", () => ConfirmForgetItem(item));
                forget.SetTextSize(Android.Util.ComplexUnitType.Sp, 11f);
                forget.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
                itemRow.AddView(forget);

                row.AddView(itemRow);
            }

            _batchList.AddView(row);
        }
    }

    /// <summary>
    /// One of the two irreversible actions in the app (the other is removing a single photo from
    /// the trash). The confirmation names the consequence rather than asking "are you sure?" — the
    /// user needs to know that Restore stops working, which is not obvious from "empty trash".
    /// </summary>
    void ConfirmEmpty()
    {
        var (files, bytes) = new FolderTrashService(_catalog.TrashDir).Measure();
        if (files == 0)
        {
            Toast.MakeText(this, "The trash is already empty.", ToastLength.Short)!.Show();
            return;
        }

        new AlertDialog.Builder(this)
            .SetTitle("Empty trash permanently?")!
            .SetMessage($"{files} file(s), {FormatBytes(bytes)}, will be deleted for good.\n\n"
                      + "This cannot be undone, and anything in the history above will no longer "
                      + "be restorable.")!
            .SetNegativeButton("Cancel", (EventHandler<DialogClickEventArgs>?)null)!
            .SetPositiveButton("Delete permanently", (_, _) =>
            {
                var removed = new FolderTrashService(_catalog.TrashDir).Empty();
                Android.Util.Log.Info("SnapZap", $"emptied trash: {removed} file(s)");
                Toast.MakeText(this, $"Deleted {removed} file(s).", ToastLength.Long)!.Show();
                Refresh();
            })!
            .Show();
    }

    /// <summary>
    /// Removing one photo from the trash. Confirmed, because it is irreversible — this and
    /// emptying the trash are the only two actions in SnapZap that are.
    /// </summary>
    void ConfirmForgetItem(UndoItem item)
    {
        var name = System.IO.Path.GetFileName(item.OriginalPath);

        // Two genuinely different actions behind one word, so they get two different warnings. For
        // a restored entry the photo is back in the library and only the record goes; saying
        // "deleted for good" there would be a lie that scares the user off a harmless action.
        var message = item.Restored
            ? $"“{name}” was already restored, so only this history entry is removed. The photo "
            + "itself stays where it is."
            : $"“{name}” will be deleted for good and cannot be restored.";

        new AlertDialog.Builder(this)
            .SetTitle(item.Restored ? "Remove history entry?" : "Delete permanently?")!
            .SetMessage(message)!
            .SetNegativeButton("Cancel", (EventHandler<DialogClickEventArgs>?)null)!
            .SetPositiveButton(item.Restored ? "Remove entry" : "Delete permanently", (_, _) =>
            {
                var ok = _catalog.NewDeleteService().ForgetItem(item.Id);
                Toast.MakeText(this, ok ? "Removed" : "Could not remove that file", ToastLength.Short)!.Show();
                Refresh();
            })!
            .Show();
    }

    /// <summary>Same, for a whole batch. Also confirmed, and it names the count.</summary>
    void ConfirmForgetBatch(UndoBatch b)
    {
        var pending = b.Total - b.Restored;

        new AlertDialog.Builder(this)
            .SetTitle("Remove these from the trash?")!
            .SetMessage(pending > 0
                ? $"{pending} photo(s) will be deleted for good and cannot be restored."
                  + (b.Restored > 0 ? $" The {b.Restored} already restored will keep their files." : "")
                : $"All {b.Total} in this batch were already restored, so only the history entries "
                  + "are removed. The photos themselves stay where they are.")!
            .SetNegativeButton("Cancel", (EventHandler<DialogClickEventArgs>?)null)!
            .SetPositiveButton(pending > 0 ? "Delete permanently" : "Remove entries", (_, _) =>
            {
                var n = _catalog.NewDeleteService().ForgetBatch(b.BatchId);
                Android.Util.Log.Info("SnapZap", $"forgot {n} entries from {b.BatchId}");
                Toast.MakeText(this, $"Removed {n}", ToastLength.Short)!.Show();
                Refresh();
            })!
            .Show();
    }

    /// <summary>
    /// The cached thumbnail for a deleted photo, keyed by content hash —
    /// <c>&lt;thumbs&gt;/&lt;hash[..2]&gt;/&lt;hash&gt;.jpg</c>, the same content-addressed layout the
    /// desktop's <c>/api/thumb/{hash}</c> serves and the Android grid reads.
    /// </summary>
    /// <remarks>
    /// Null when the row predates <c>undo_log.content_hash</c> (added by migration) or the
    /// thumbnail was never produced — a blank tile, not a missing row, because a history entry you
    /// cannot preview is still one you may want to restore.
    /// </remarks>
    Bitmap? LoadThumb(string? contentHash)
    {
        if (contentHash is not { Length: >= 2 }) return null;
        try
        {
            var path = System.IO.Path.Combine(_catalog.ThumbDir, contentHash[..2], contentHash + ".jpg");
            if (File.Exists(path)) return BitmapFactory.DecodeFile(path);
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>Matches the desktop's formatting so the same number reads the same on both.</summary>
    static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.##} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.##} KB",
        _ => $"{bytes} B",
    };

    TextView Text(string s, float sp, bool bold = false)
    {
        var t = new TextView(this) { Text = s };
        t.SetTextSize(Android.Util.ComplexUnitType.Sp, sp);
        t.SetPadding(0, 8, 0, 8);
        if (bold) t.SetTypeface(null, Android.Graphics.TypefaceStyle.Bold);
        return t;
    }

    Button Button(string label, Action? onClick)
    {
        var b = new Button(this) { Text = label };
        if (onClick is not null) b.Click += (_, _) => onClick();
        return b;
    }

    sealed class InsetsPadder : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
        {
            var bars = insets.GetInsets(WindowInsets.Type.SystemBars());
            v.SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom);
            return insets;
        }
    }

    protected override void OnDestroy()
    {
        _catalog.Dispose();
        base.OnDestroy();
    }
}
