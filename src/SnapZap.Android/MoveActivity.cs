using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using SnapZap.Core;
using SnapZap.Core.Export;

namespace SnapZap.Droid;

/// <summary>
/// Writes the photos in view to a folder of the user's choosing, verifying each one, and offers to
/// remove the originals afterwards.
/// </summary>
/// <remarks>
/// <para><b>This was "desktop only" until it wasn't.</b> The Android port excluded it as v1 scope
/// (docs/ANDROID-PORT-PLAN.md §1, screen 11), and the exclusion outlived its reasons: every
/// dependency already had a portable implementation. <c>ExportEngine</c> is Core;
/// <c>PortableLinkStub</c> exists precisely for a platform with no hardlinks and reports
/// <c>SameVolume = false</c> so the engine degrades hardlink mode to a copy rather than reaching a
/// path Android cannot take; <c>FolderTrashService</c> is what deletes already use here; and
/// <see cref="DirectoryPickerActivity"/> can pick a destination. Nothing platform-shaped was left.</para>
///
/// <para><b>Same shape as the desktop's dialog, deliberately.</b> Destination, folder structure,
/// a preflight that has to pass before the action arms, and then the originals question asked as a
/// confirmation at commit time rather than as a transfer mode chosen up front. Hardlink is offered
/// on neither head any more, which is convenient here — it is the one mode Android genuinely could
/// not do.</para>
///
/// <para><b>Scoped to what the caller passes, never the catalogue.</b> The catalogue outlives every
/// folder ever scanned, so an unscoped move would write out photos the user cannot see — the same
/// hazard <c>AndroidCatalog.ScopedImages</c> exists to close for the grid.</para>
/// </remarks>
[Activity(Label = "Move to a folder", Theme = "@android:style/Theme.Material.Light.NoActionBar")]
public sealed class MoveActivity : Activity
{
    /// <summary>Input: the folder whose photos are being moved. Scope comes from the catalogue.</summary>
    public const string ExtraSourceRoot = "snapzap.move.source";

    const int RequestDestination = 7;

    AndroidCatalog _catalog = null!;
    BackHandler? _back;
    LinearLayout _body = null!;

    string _sourceRoot = "";
    string _destination = "";
    ExportStructure _structure = ExportStructure.Date;
    List<ImageRecord> _photos = [];

    Preflight? _preflight;
    bool _running;
    string? _result;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _back = BackHandler.Attach(this, Finish);
        _catalog = new AndroidCatalog();

        _sourceRoot = Intent?.GetStringExtra(ExtraSourceRoot) ?? _catalog.ScanRoot ?? "";
        _photos = _catalog.ScopedImages.ToList();

        var page = new LinearLayout(this) { Orientation = Orientation.Vertical };
        page.SetBackgroundColor(Design.Bg);
        SetContentView(page);
        page.SetOnApplyWindowInsetsListener(new InsetsPadder());
        page.RequestApplyInsets();

        page.AddView(Design.HeaderBar(this, "‹", Finish, "Move to a folder",
            $"{_photos.Count:N0} photos · {FormatBytes(_photos.Sum(p => p.FileSize))}"));

        var scroll = new ScrollView(this);
        scroll.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f);
        _body = new LinearLayout(this) { Orientation = Orientation.Vertical };
        scroll.AddView(_body, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        page.AddView(scroll);

        Render();
    }

    void Render()
    {
        _body.RemoveAllViews();
        var pad = Design.Dp(this, 16);

        if (_result is not null)
        {
            var done = Design.Callout(this);
            done.AddView(Design.Body(this, _result, 15f, bold: true));
            _body.AddView(done);
            var close = Design.Button(this, "Done", Design.Btn.Primary, 48f);
            close.LayoutParameters = Design.Wide();
            close.Click += (_, _) => { SetResult(Result.Ok); Finish(); };
            _body.AddView(Pad(close));
            return;
        }

        // ---- destination ----
        Section("Destination");
        _body.AddView(Design.ListRow(this,
            _destination.Length == 0 ? "Not chosen yet" : _destination,
            _destination.Length == 0
                ? "Pick a folder outside the one you are cleaning."
                : null,
            null, "📁"));

        var browse = Design.Button(this,
            _destination.Length == 0 ? "Choose a destination…" : "Change destination…",
            Design.Btn.Secondary, 48f);
        browse.LayoutParameters = Design.Wide();
        browse.Click += (_, _) =>
        {
            var i = new Intent(this, typeof(DirectoryPickerActivity));
            i.PutExtra(DirectoryPickerActivity.ExtraPath,
                _destination.Length > 0 ? _destination : _sourceRoot);
            StartActivityForResult(i, RequestDestination);
        };
        _body.AddView(Pad(browse));

        // ---- structure ----
        Section("Folder structure");
        foreach (var (s, label, note) in new[]
        {
            (ExportStructure.Date,   "By date",       "Year / year-month folders, from each photo's capture date."),
            (ExportStructure.Mirror, "Mirror source", "The same folder tree you scanned."),
            (ExportStructure.Flat,   "Flat",          "Everything in one folder, renamed on collision."),
        })
        {
            var on = _structure == s;
            var row = Design.ListRow(this, label, note, on ? "✓" : null, null, highlight: on);
            row.Click += (_, _) => { _structure = s; _preflight = null; Render(); };
            _body.AddView(row);
        }

        // ---- preflight ----
        // The action stays unarmed until this passes, and any change above clears it, so the button
        // can never be pressed against a destination nothing verified — the same rule the desktop's
        // dialog documents.
        Section("Check the destination", "Free space is verified here, not guessed at.");

        if (_preflight is { } p)
        {
            _body.AddView(Design.ListRow(this, "To write",
                null, $"{p.KeeperCount:N0} · {FormatBytes(p.TotalBytes)}"));
            _body.AddView(Design.ListRow(this, "Free at destination", null,
                p.DestinationFreeBytes is { } free ? FormatBytes(free) : "could not be read"));

            if (!p.EnoughSpace)
                _body.AddView(Warning("Not enough room at the destination. Free some space or pick "
                                    + "somewhere else."));
            else if (p.DestinationFreeBytes is null)
                _body.AddView(Warning("Free space could not be read, so nothing confirmed this will "
                                    + "fit. The move is not blocked."));

            if (p.SampleDestinations.Count > 0)
                _body.AddView(Pad(Design.Note(this, $"First file goes to {p.SampleDestinations[0]}")));
        }

        var check = Design.Button(this,
            _preflight is null ? "Check destination" : "Check again", Design.Btn.Secondary, 48f);
        check.LayoutParameters = Design.Wide();
        check.Enabled = _destination.Length > 0;
        check.Alpha = _destination.Length > 0 ? 1f : 0.45f;
        check.Click += (_, _) => RunPreflight();
        _body.AddView(Pad(check));

        if (_preflight is { EnoughSpace: true })
        {
            var go = Design.Button(this,
                _running ? "Moving…" : $"Move {_photos.Count:N0} photos", Design.Btn.Primary, 52f);
            go.LayoutParameters = Design.Wide();
            go.Enabled = !_running;
            go.Click += (_, _) => AskAboutOriginals();
            _body.AddView(Pad(go));
        }

        _body.AddView(Design.Gap(this, 24));
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != RequestDestination || resultCode != Result.Ok) return;

        var chosen = data?.GetStringExtra(DirectoryPickerActivity.ExtraPath);
        if (string.IsNullOrEmpty(chosen)) return;

        // Refused rather than warned about: writing the clean copy back inside the folder being
        // cleaned means the next scan picks the copies up as duplicates of their own originals.
        if (chosen.StartsWith(_sourceRoot.TrimEnd('/') + "/", StringComparison.Ordinal) ||
            chosen.TrimEnd('/') == _sourceRoot.TrimEnd('/'))
        {
            new AlertDialog.Builder(this)
                .SetTitle("Pick somewhere else")!
                .SetMessage("That folder is inside the one you are cleaning. The copies would end up "
                          + "in the next scan as duplicates of the photos they came from.")!
                .SetPositiveButton("OK", (EventHandler<DialogClickEventArgs>?)null)!
                .Show();
            return;
        }

        _destination = chosen!;
        _preflight = null;   // a new destination is a new question
        Render();
    }

    void RunPreflight()
    {
        try
        {
            _preflight = NewEngine().Plan(BuildRequest(TransferMode.Copy));
        }
        catch (Exception ex)
        {
            Toast.MakeText(this, $"Could not check: {ex.Message}", ToastLength.Long)!.Show();
            Android.Util.Log.Error("SnapZap", ex.ToString());
        }
        Render();
    }

    /// <summary>
    /// The originals question, asked at commit time.
    /// </summary>
    /// <remarks>
    /// Deliberately the same as the desktop's: one action, and the only genuine choice is whether
    /// the source survives it. Both answers are explicit buttons rather than a default and a
    /// variant — "keep" and "remove" are two different intentions.
    /// </remarks>
    void AskAboutOriginals()
    {
        new AlertDialog.Builder(this)
            .SetTitle("Remove the originals?")!
            .SetMessage($"{_photos.Count:N0} photos will be written to the destination and checked "
                      + "file by file.\n\nRemoving happens only after every one is verified, and goes "
                      + "to SnapZap's trash — undo it from History.")!
            .SetNeutralButton("Cancel", (EventHandler<DialogClickEventArgs>?)null)!
            .SetNegativeButton("Keep originals", (_, _) => Run(TransferMode.Copy))!
            .SetPositiveButton("Remove originals", (_, _) => Run(TransferMode.Move))!
            .Show();
    }

    async void Run(TransferMode mode)
    {
        if (_running) return;
        _running = true;
        Render();
        WorkService.Start(this, "Moving photos");

        try
        {
            var progress = new Progress<ExportProgress>(p =>
                WorkService.Report(this, "Moving photos",
                    $"{p.Done:N0} of {p.Total:N0}", p.Done, p.Total));

            var result = await Task.Run(() => NewEngine().RunAsync(BuildRequest(mode), progress));

            _result = mode == TransferMode.Move
                ? $"Moved {result.Exported:N0} to {result.Destination}. The originals are in History "
                  + "if you need them back."
                : $"Copied {result.Exported:N0} to {result.Destination}. Your originals are untouched.";
            if (result.Failed > 0) _result += $" {result.Failed:N0} could not be written.";
        }
        catch (Exception ex)
        {
            Toast.MakeText(this, $"Move failed: {ex.Message}", ToastLength.Long)!.Show();
            Android.Util.Log.Error("SnapZap", ex.ToString());
        }
        finally
        {
            WorkService.Stop(this);
            _running = false;
            Render();
        }
    }

    ExportEngine NewEngine() =>
        new(_catalog.Db, new SnapZap.Core.Platform.PortableLinkStub(), _catalog.Trash);

    ExportRequest BuildRequest(TransferMode mode) => new()
    {
        Destination = _destination,
        SourceRoot = _sourceRoot,
        Mode = mode,
        Structure = _structure,
        // Nothing is recycled beyond the originals themselves. The desktop's "also recycle what I
        // didn't pick" sweeps the whole catalogue, which needs a selection model this screen does
        // not have — offering it here would arm a much larger delete than the screen describes.
        RejectAction = RejectAction.Leave,
        KeeperIds = _photos.Select(p => p.Id).ToList(),
    };

    void Section(string eyebrow, string? note = null)
    {
        var wrap = new LinearLayout(this) { Orientation = Orientation.Vertical };
        wrap.SetPadding(Design.Dp(this, 16), Design.Dp(this, 16), Design.Dp(this, 16), Design.Dp(this, 8));
        wrap.AddView(Design.Eyebrow(this, eyebrow));
        if (note is not null) { wrap.AddView(Design.Gap(this, 5)); wrap.AddView(Design.Note(this, note)); }
        _body.AddView(wrap);
        _body.AddView(Design.Rule(this, 2f, Design.Divider));
    }

    View Warning(string text)
    {
        var callout = Design.Callout(this);
        callout.AddView(Design.Note(this, text, Design.Accent700));
        return callout;
    }

    View Pad(View child)
    {
        var wrap = new LinearLayout(this) { Orientation = Orientation.Vertical };
        wrap.SetPadding(Design.Dp(this, 16), Design.Dp(this, 12), Design.Dp(this, 16), Design.Dp(this, 12));
        wrap.AddView(child);
        return wrap;
    }

    static string FormatBytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):0.##} MB",
        >= 1L << 10 => $"{b / (double)(1L << 10):0.##} KB",
        _ => $"{b} B",
    };

    public override void OnBackPressed() => Finish();

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
        _back?.Detach();
        _catalog.Dispose();
        base.OnDestroy();
    }
}
