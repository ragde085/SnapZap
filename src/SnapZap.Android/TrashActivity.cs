using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Graphics;
using Android.Widget;
using SnapZap.Core.Delete;

namespace SnapZap.Droid;

/// <summary>
/// Screen 12 of the mobile handoff — History and undo: how much the trash holds, what was
/// deleted and when, restore a batch or a single photo, and empty the trash.
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
/// <c>DeleteService.Batches()</c>, same <c>RestoreAsync</c>, same <c>undo_log</c>. The "what
/// happened" classification below (<see cref="Describe"/>) mirrors <c>UndoDialog.What</c>'s
/// <c>batch_id</c>-prefix switch, so a batch tells the same story on both heads.</para>
/// </summary>
[Activity(Label = "History",
          Theme = "@android:style/Theme.Material.Light.NoActionBar")]
public sealed class TrashActivity : Activity
{
    // "In this session" for the undo bar has no cross-activity signal to key off — the recycle
    // that produces a fresh batch normally happens elsewhere (selection mode), and this activity
    // has no channel back to that event. A batch's own timestamp is a real, honest proxy instead
    // of inventing one: newest-first ordering means the batch that was just recycled is the top
    // one, and it shows up here within seconds of it happening. A short window is what keeps this
    // "not persistent chrome" without needing a dismiss control the mock doesn't draw — an old
    // batch, or the same batch a few minutes later, never resurrects the bar.
    const int InkBarWindowSeconds = 45;

    AndroidCatalog _catalog = null!;
    FrameLayout _root = null!;
    LinearLayout _body = null!;
    View? _inkBar;
    string? _inkBarHandledBatchId;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _catalog = new AndroidCatalog();

        // A FrameLayout root rather than the page LinearLayout directly, so the ink bar (screen
        // 12's undo toast) can float over the scrolling content near the bottom instead of taking
        // a slot in the layout flow — exactly how Design.InkBar's own doc says it wants to be used.
        _root = new FrameLayout(this);
        _root.SetBackgroundColor(Design.Bg);
        SetContentView(_root);
        _root.SetOnApplyWindowInsetsListener(new InsetsPadder());
        _root.RequestApplyInsets();

        var page = new LinearLayout(this) { Orientation = Orientation.Vertical };
        page.LayoutParameters = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
        _root.AddView(page);

        page.AddView(Design.HeaderBar(this, "‹", () => Finish(), "History"));

        var scroll = new ScrollView(this);
        scroll.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f);
        page.AddView(scroll);

        _body = new LinearLayout(this) { Orientation = Orientation.Vertical };
        scroll.AddView(_body, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        Refresh();
    }

    void Refresh()
    {
        _body.RemoveAllViews();

        // ── Explanatory note — the block directly under the header in the mock. 2px rule beneath
        // it because it closes out a section, not a row within one (see Design's own remark on
        // 2px-between-sections vs 1px-within-a-list). ──────────────────────────────────────────
        var intro = Design.Note(this,
            "Every batch SnapZap has recycled or moved, newest first. Restore puts the files "
            + "back where they came from.");
        Pad(intro, top: 11, bottom: 11);
        _body.AddView(intro);
        _body.AddView(Design.Rule(this, 2f, Design.Divider));

        // ── Trash storage read-out + Empty trash. The mock doesn't draw this control — this
        // screen exists on Android specifically because there is no OS trash to show it instead
        // (see the class doc) — but it uses the same vocabulary as everything else here: an
        // eyebrow label, a line of fact, a full-width action. ─────────────────────────────────
        var (files, bytes) = new FolderTrashService(_catalog.TrashDir).Measure();
        var storage = new LinearLayout(this) { Orientation = Orientation.Vertical };
        Pad(storage, top: 13, bottom: 13);
        storage.AddView(Design.Eyebrow(this, "Trash storage"));
        storage.AddView(Design.Gap(this, 4f));
        storage.AddView(Design.Body(this, files == 0
            ? "Empty — nothing is being held."
            : $"{files} file(s) · {FormatBytes(bytes)} held in app storage.", 13f));
        storage.AddView(Design.Gap(this, 9f));
        var empty = Design.Button(this, "Empty trash", Design.Btn.Secondary);
        empty.LayoutParameters = Design.Wide();
        empty.Click += (_, _) => ConfirmEmpty();
        storage.AddView(empty);
        _body.AddView(storage);
        _body.AddView(Design.Rule(this, 2f, Design.Divider));

        var svc = _catalog.NewDeleteService();
        var batches = svc.Batches();

        if (batches.Count == 0)
        {
            var noneYet = Design.Note(this, "Nothing deleted yet.");
            Pad(noneYet, top: 13, bottom: 13);
            _body.AddView(noneYet);
        }
        else
        {
            foreach (var b in batches)
                _body.AddView(BuildBatchBlock(b, svc));
        }

        // ── Closing note. The mock's own copy here ("restore from your file manager's own
        // Recycle Bin") is false on Android — there is no such fallback, which is the entire
        // reason this screen exists (see the class doc) — so this says the true, Android-shaped
        // thing rather than sending someone looking for a Recycle Bin that isn't there. ────────
        var closing = Design.Note(this,
            "This is the only place these photos can be restored from — SnapZap's trash is "
            + "private to the app, with no file-manager Recycle Bin behind it.");
        Pad(closing, top: 12, bottom: 12);
        _body.AddView(closing);

        UpdateInkBar(batches.Count > 0 ? batches[0] : null, svc);
    }

    /// <summary>
    /// One batch's block: the bold one-liner, its note line(s), its own full-width Restore
    /// (screen 12's whole point — "History gives every batch its own full-width Restore, because
    /// a 44px row cannot hold a table"), a quieter whole-batch removal, and — preserved from
    /// before the restyle — the per-photo thumbnails, restore and removal that batch-level
    /// controls can't replace (a batch is whatever one delete happened to sweep up, not
    /// necessarily the unit the user wants to act on; see the per-item comment below).
    /// </summary>
    LinearLayout BuildBatchBlock(UndoBatch b, DeleteService svc)
    {
        var when = DateTimeOffset.FromUnixTimeSeconds(b.Ts).ToLocalTime();
        var whenText = $"{when:MMM d, yyyy}, {when:h:mm tt}";
        var (title, location, extra) = Describe(b);

        // Same gate the pre-restyle screen used to decide whether Restore was worth offering:
        // nothing pending means nothing to put back. What changes here is *how* that state
        // renders — a disabled control at reduced opacity per the mock, rather than the button
        // simply disappearing.
        //
        // The mock's own example of this state is captioned "emptied from the bin since", but
        // Core cannot actually tell that story: FolderTrashService.Empty() purges files without
        // touching undo_log (see its own doc comment — this is ROADMAP item 14), so a batch that
        // was emptied rather than restored still reads Restored < Total forever, never >=. The
        // only state this code can honestly detect is "fully restored" — so that's what the note
        // says, rather than copying the mock's literal caption and risking exactly the kind of
        // false, scary claim the per-item warnings below are careful to avoid.
        var restorable = b.Restored < b.Total;

        var block = new LinearLayout(this) { Orientation = Orientation.Vertical };
        Pad(block, top: 13, bottom: 13);
        block.Alpha = restorable ? 1f : 0.5f;   // opacity:.5 on a batch that can't be restored

        block.AddView(Design.Body(this, title, 14f, bold: true));

        var noteText = restorable
            ? $"{whenText} · {b.Total} photo(s) · {location}"
              + (b.Restored > 0 ? $" · {b.Restored} restored so far" : "")
            : $"{whenText} · {b.Total} photo(s) · already restored";
        block.AddView(Design.Note(this, noteText));
        if (extra is not null) block.AddView(Design.Note(this, extra));

        block.AddView(Design.Gap(this, 9f));

        var restore = Design.Button(this,
            restorable ? $"Restore {b.Total - b.Restored} photo(s)" : "Restore",
            Design.Btn.Secondary);
        restore.SetTextSize(Android.Util.ComplexUnitType.Sp, 13.5f);
        restore.LayoutParameters = Design.Wide();
        restore.Enabled = restorable;
        if (restorable)
        {
            restore.Click += async (_, _) =>
            {
                restore.Enabled = false;
                restore.Text = "Restoring…";
                try
                {
                    WorkService.Start(this, "Restoring photos");
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
                // In a finally: a restore that throws used to leave the foreground service running
                // with a "Restoring photos" notification that never went away.
                finally { WorkService.Stop(this); }
                Refresh();
            };
        }
        block.AddView(restore);

        // Whole-batch removal — kept, but deliberately quieter than Restore. The mock doesn't draw
        // this control at all: screen 12's whole point is that a 44px row can't hold a table, and
        // a second full-width button here would compete with the one the screen exists for. Ghost
        // styling (plain text, no border) keeps it available without fighting Restore for weight.
        var forgetBatch = Design.Button(this, "Remove all from history", Design.Btn.Ghost, 36f);
        forgetBatch.SetTextSize(Android.Util.ComplexUnitType.Sp, 12f);
        forgetBatch.LayoutParameters = Design.Wide();
        forgetBatch.Click += (_, _) => ConfirmForgetBatch(b);
        block.AddView(Design.Gap(this, 4f));
        block.AddView(forgetBatch);

        // Per-photo rows: thumbnail, filename, per-item restore/remove. Preserved wholesale from
        // before the restyle — the mock doesn't draw this at all, but a batch is not always the
        // unit a user wants to act on ("restore that one" and "get rid of that one for good" are
        // both ordinary asks). Each row gets more height than a 44px list row rather than trying
        // to fit thumbnail + name + two buttons on one line — the same "can't hold a table"
        // reasoning the batch-level Restore above is built on, applied one level down.
        var items = svc.ItemsInBatch(b.BatchId, 50);
        if (items.Count > 0)
        {
            block.AddView(Design.Gap(this, 10f));
            block.AddView(Design.Rule(this, 1f, Design.Divider));
            block.AddView(Design.Gap(this, 6f));
            foreach (var item in items)
            {
                block.AddView(BuildItemRow(item));
                block.AddView(Design.Rule(this, 1f, Design.Divider));
            }
            if (b.Total > items.Count)
            {
                var more = Design.Note(this,
                    $"+ {b.Total - items.Count} more not shown here — Restore above covers the "
                    + "whole batch.");
                Pad(more, top: 6, bottom: 0);
                block.AddView(more);
            }
        }

        return block;
    }

    /// <summary>
    /// One photo within a batch: thumbnail, filename, and its own Restore / Remove. Functionally
    /// identical to before the restyle — only the visual language changed.
    /// </summary>
    LinearLayout BuildItemRow(UndoItem item)
    {
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(0, Design.Dp(this, 6), 0, Design.Dp(this, 6));

        // The thumbnail is the whole point of history being browsable: a filename tells you
        // nothing about whether you want a photo back. This is also the one place the
        // "delete the catalogue row, keep the thumbnail" rule pays off — the images row is
        // gone, so there is no ImageRecord to ask, and undo_log.content_hash plus the
        // content-addressed cache is all that is left to render from. Anything that later
        // prunes "orphaned" thumbnails breaks exactly this screen.
        var thumbSize = Design.Dp(this, 48f);
        var thumbWrap = new FrameLayout(this);
        thumbWrap.LayoutParameters = new LinearLayout.LayoutParams(thumbSize, thumbSize)
        {
            RightMargin = Design.Dp(this, 12),
        };
        thumbWrap.SetBackgroundColor(Design.Surface);

        var bmp = LoadThumb(item.ContentHash);
        if (bmp is not null)
        {
            var thumb = new ImageView(this);
            thumb.SetScaleType(ImageView.ScaleType.CenterCrop);
            thumb.LayoutParameters = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
            thumb.SetImageBitmap(bmp);
            thumbWrap.AddView(thumb);
        }
        else
        {
            // Same treatment as the desktop's .undo-thumb-missing: a visible, explained tile
            // rather than an empty gap. Entries deleted before content_hash was recorded can
            // never resolve a thumbnail — the images row is gone, so the hash is
            // unrecoverable — and rendering nothing made a working screen look broken.
            var mark = Design.Body(this, "?", 14f, bold: true);
            mark.SetTextColor(Design.Neutral700);
            mark.Gravity = GravityFlags.Center;
            mark.LayoutParameters = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
            mark.ContentDescription = "No preview available for this entry";
            thumbWrap.AddView(mark);
        }
        row.AddView(thumbWrap);

        var col = new LinearLayout(this) { Orientation = Orientation.Vertical };
        col.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        col.AddView(Design.Body(this,
            System.IO.Path.GetFileName(item.OriginalPath) + (item.Restored ? "  ·  restored" : ""),
            12.5f));
        row.AddView(col);

        if (!item.Restored)
        {
            var one = Design.Button(this, "Restore", Design.Btn.Ghost, 32f);
            one.SetTextSize(Android.Util.ComplexUnitType.Sp, 11.5f);
            one.Click += async (_, _) =>
            {
                one.Enabled = false;
                var ok = await Task.Run(() => _catalog.NewDeleteService().RestoreItemAsync(item.Id));
                Toast.MakeText(this, ok ? "Restored" : "No longer in the trash", ToastLength.Short)!.Show();
                Refresh();
            };
            row.AddView(one);
        }

        var forget = Design.Button(this, "Remove", Design.Btn.Ghost, 32f);
        forget.SetTextSize(Android.Util.ComplexUnitType.Sp, 11.5f);
        forget.SetPadding(Design.Dp(this, 10), 0, 0, 0);
        forget.Click += (_, _) => ConfirmForgetItem(item);
        row.AddView(forget);

        return row;
    }

    /// <summary>
    /// The ink undo bar (Design.InkBar): dark fill, "Recycled N photo(s)." on the left, Undo on
    /// the right, floated over the content near the bottom. See the class-level remark on
    /// <see cref="InkBarWindowSeconds"/> for why a batch's own age is the trigger, rather than an
    /// explicit cross-activity event this screen has no channel to receive.
    /// </summary>
    void UpdateInkBar(UndoBatch? top, DeleteService svc)
    {
        var ageSeconds = top is null
            ? long.MaxValue
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds() - top.Ts;

        var show = top is not null
                   && top.Restored < top.Total
                   && ageSeconds <= InkBarWindowSeconds
                   && top.BatchId != _inkBarHandledBatchId;

        if (_inkBar is not null) { _root.RemoveView(_inkBar); _inkBar = null; }
        if (!show) return;

        var b = top!;
        var bar = Design.InkBar(this, $"Recycled {b.Total} photo(s).", "Undo", async () =>
        {
            _inkBarHandledBatchId = b.BatchId;   // don't resurrect this bar once acted on
            try
            {
                WorkService.Start(this, "Restoring photos");
                var r = await Task.Run(() => svc.RestoreAsync(b.BatchId));
                WorkService.Stop(this);
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
        });

        var lp = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            Gravity = GravityFlags.Bottom,
            LeftMargin = Design.Dp(this, 12),
            RightMargin = Design.Dp(this, 12),
            BottomMargin = Design.Dp(this, 32),
        };
        bar.LayoutParameters = lp;
        _root.AddView(bar);
        _inkBar = bar;
    }

    /// <summary>
    /// What kind of batch this was, and where its photos are — mirrors the desktop UndoDialog's
    /// <c>What</c>/<c>WasMoved</c> switch on <c>batch_id</c>'s prefix, so the same batch tells the
    /// same story on both heads. <c>DeleteService.RecycleAsync</c> stamps Android's own batches
    /// "delete-…", which falls through to the last case below; the "move-"/"export-" prefixes
    /// come from <c>ExportEngine</c>, a desktop-only feature today — kept here rather than
    /// dropped, so this still reads correctly the day export ships on Android too, at no cost now.
    /// </summary>
    static (string Title, string Location, string? Extra) Describe(UndoBatch b)
    {
        if (b.BatchId.StartsWith("move-", StringComparison.Ordinal))
            return ("Moved out by export", "relocated by export, not in SnapZap's trash",
                    "Relocated, not binned — SnapZap's trash would be the wrong place to look "
                    + "for these.");
        if (b.BatchId.StartsWith("export-", StringComparison.Ordinal))
            return ("Recycled during export", "the ones you didn't pick", null);
        return ("Deleted — recycled from a selection", "in SnapZap's trash", null);
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
                WorkService.Start(this, "Emptying trash");
                WorkService.Start(this, "Emptying trash");
                var removed = new FolderTrashService(_catalog.TrashDir).Empty();
                WorkService.Stop(this);
                WorkService.Stop(this);
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

    /// <summary>Horizontal padding is always the mock's 16px; only top/bottom vary per block.</summary>
    void Pad(View v, float top, float bottom) =>
        v.SetPadding(Design.Dp(this, 16), Design.Dp(this, top), Design.Dp(this, 16), Design.Dp(this, bottom));

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
