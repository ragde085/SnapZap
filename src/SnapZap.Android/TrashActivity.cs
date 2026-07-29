using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
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

            // A fully-restored batch has nothing left to put back, so offering the button would be
            // a control that reports success without doing anything.
            if (b.Restored < b.Total)
            {
                var restore = Button($"Restore {b.Total - b.Restored}", null);
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
                row.AddView(restore);
            }

            _batchList.AddView(row);
        }
    }

    /// <summary>
    /// The only irreversible action in the app, so the confirmation names the consequence rather
    /// than asking "are you sure?" — the user needs to know that Restore stops working, which is
    /// not obvious from "empty trash".
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
