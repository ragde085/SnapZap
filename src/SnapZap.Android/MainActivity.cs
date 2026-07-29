using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using SnapZap.Core;
using SnapZap.Core.Dedup;
using SnapZap.Core.Scanning;

namespace SnapZap.Droid;

/// <summary>
/// The first end-to-end slice of the native Android head: grant all-files access, point it at a
/// folder, scan it with the unmodified <c>SnapZap.Core</c> pipeline, and show the results in a
/// native grid.
///
/// Views are built in code rather than from layout XML — there is one screen with three states and
/// no designer in this workflow, so a layout file would be indirection without a reader.
/// </summary>
// NoActionBar is deliberate. Targeting API 36 means Android draws the window edge-to-edge: the
// content FrameLayout is laid out at [0,0] — the full window — and the action bar is drawn over
// the top of it rather than pushing it down. Verified with `uiautomator dump`, which showed the
// heading at y=24..111 sitting underneath an action_bar_container occupying y=0..210, i.e. the
// screen opened with its own title invisible. The app renders its own header anyway, so the
// platform action bar was only ever costing 210px of overlap.
[Activity(Label = "SnapZap", MainLauncher = true,
          Theme = "@android:style/Theme.Material.Light.NoActionBar")]
public sealed class MainActivity : Activity
{
    const string Tag = "SnapZap";

    AndroidCatalog? _catalog;
    LinearLayout _root = null!;
    bool _scanning;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _root.SetPadding(24, 24, 24, 24);

        // FocusableInTouchMode on the container, and NOT wrapped in a ScrollView. Two bugs this
        // avoids, both seen on the emulator:
        //   1. A ScrollView scrolls to its first focusable child on layout, so the screen opened
        //      already scrolled past its own heading to reveal the button.
        //   2. A GridView inside a ScrollView is a scroll container inside a scroll container —
        //      the child gets an unbounded height offer and the two fight over touch handling.
        // The grid gets the leftover space by weight instead, which is what makes it scroll on
        // its own and recycle its views properly.
        _root.FocusableInTouchMode = true;

        SetContentView(_root);

        // Edge-to-edge means the window also extends under the status and navigation bars, so the
        // padding has to come from the insets rather than being a guessed constant — cutouts and
        // gesture bars differ per device, and the two test handsets differ from each other.
        _root.SetOnApplyWindowInsetsListener(new InsetsPadder());
        _root.RequestApplyInsets();

        Render();
    }

    // Re-checked on every resume because the grant happens in the system Settings app, not in a
    // dialog we get a result callback from — the user leaves, toggles, and comes back.
    protected override void OnResume()
    {
        base.OnResume();
        if (!_scanning) Render();
    }

    /// <summary>
    /// All-files access, the permission the whole storage model rests on. Blocks rather than
    /// degrades (AC-4.3): without it a scan would silently see nothing, which reads as "SnapZap
    /// found no photos" rather than as a permission problem.
    /// </summary>
    static bool HasAllFilesAccess =>
        !OperatingSystem.IsAndroidVersionAtLeast(30) || Android.OS.Environment.IsExternalStorageManager;

    void Render()
    {
        _root.RemoveAllViews();
        if (!HasAllFilesAccess) RenderPermissionGate();
        else RenderScanScreen();
    }

    void RenderPermissionGate()
    {
        _root.AddView(Text("Storage access required", 20f, bold: true));
        _root.AddView(Text(
            "SnapZap reads photos from any folder you choose, including folders the system Photos "
            + "app does not index. Android calls this \"All files access\" and only lets you grant "
            + "it from Settings.\n\nSnapZap has no internet permission — nothing it reads can leave "
            + "this device.", 14f));

        _root.AddView(Button("Open Settings to grant access", () =>
        {
            try
            {
                var intent = new Intent(Settings.ActionManageAppAllFilesAccessPermission,
                    Android.Net.Uri.Parse("package:" + PackageName));
                StartActivity(intent);
            }
            catch (Exception ex)
            {
                // Some OEM builds do not honour the per-package intent; the global list always exists.
                Android.Util.Log.Warn(Tag, $"per-package intent failed: {ex.Message}");
                StartActivity(new Intent(Settings.ActionManageAllFilesAccessPermission));
            }
        }));
    }

    void RenderScanScreen()
    {
        _catalog ??= new AndroidCatalog();

        _root.AddView(Text("SnapZap", 22f, bold: true));
        _root.AddView(Text($"Catalog: {_catalog.AppDataDir}", 11f));

        // /storage/emulated/0 is the conventional primary shared-storage mount and the same
        // default the desktop picker's Android branch uses (SnapZap.Core DirectoryRoots,
        // AC-5.2). Note UserProfile is NOT usable here: on-device it resolves to app-private
        // storage, not to anything the user would recognise (AC-0.5).
        var pathBox = new EditText(this)
        {
            Text = SnapZap.Core.Platform.DirectoryRoots.DefaultStart(
                SnapZap.Core.Platform.DirectoryPlatform.Android),
        };
        pathBox.SetTextSize(Android.Util.ComplexUnitType.Sp, 14f);
        _root.AddView(pathBox);

        var status = Text("", 13f);
        var scanBtn = Button("Scan", null);
        // Spacing is set through the setters, not initialisers: GridView exposes
        // VerticalSpacing/HorizontalSpacing as read-only properties in the binding.
        // GridView sizes its columns from ColumnWidth, NOT from the child's LayoutParams width —
        // setting only the latter produced one full-width cell per row. StretchColumnWidth then
        // distributes the rounding remainder instead of leaving a ragged right edge.
        var cell = (Resources!.DisplayMetrics!.WidthPixels - 48 - 8) / 3;
        var grid = new GridView(this) { NumColumns = 3, StretchMode = StretchMode.StretchColumnWidth };
        grid.SetColumnWidth(cell);
        grid.SetVerticalSpacing(4);
        grid.SetHorizontalSpacing(4);

        var reviewBtn = Button("Review duplicates", () =>
            StartActivity(new Intent(this, typeof(ReviewActivity))));
        reviewBtn.Enabled = false;

        scanBtn.Click += async (_, _) =>
        {
            if (_scanning) return;
            var root = pathBox.Text?.Trim() ?? "";
            if (!Directory.Exists(root)) { status.Text = $"Not a folder: {root}"; return; }

            _scanning = true;
            scanBtn.Enabled = false;
            status.Text = "Scanning…";

            try
            {
                var progress = new Progress<ScanProgress>(p =>
                    status.Text = $"Scanning… {p.Analyzed + p.Cached}/{(p.Total > 0 ? p.Total : p.Seen)}"
                                + (p.Failed > 0 ? $" · {p.Failed} failed" : ""));

                var result = await Task.Run(() => _catalog.NewScanner().ScanAsync(root, progress));
                var items = await Task.Run(() => _catalog.Images.All().ToList());

                status.Text =
                    $"{result.Total} photos · {result.Analyzed} new · {result.Cached} cached"
                    + (result.Failed > 0 ? $" · {result.Failed} failed" : "")
                    + (result.UnsupportedTotal > 0 ? $" · {result.UnsupportedTotal} unsupported" : "")
                    + $" · {result.ElapsedMs} ms";

                Android.Util.Log.Info(Tag, status.Text);
                grid.Adapter = new ThumbAdapter(this, items, cell, _catalog.ThumbDir);

                // Dedup runs as part of the scan rather than behind its own button. On desktop
                // these are two actions because a 40k-photo detection pass is worth deferring;
                // here the scan the user just waited for is worthless without it, since finding
                // duplicates is the entire reason to point SnapZap at a folder on a phone.
                status.Text += " · finding duplicates…";
                var report = await Task.Run(() =>
                    new DuplicateService(_catalog.Db, _catalog.Imaging, _catalog.ThumbDir)
                        .DetectAsync(root));

                status.Text =
                    $"{result.Total} photos · {report.ExactGroups} exact · {report.VariantGroups} variant"
                    + $" · {report.BurstGroups} burst · {result.ElapsedMs} ms";
                Android.Util.Log.Info(Tag, status.Text);

                // Exact + Variant only. Burst groups exist and are counted, but they are not
                // reviewable — see ReviewActivity's remarks for why that gate is safety-critical.
                reviewBtn.Enabled = report.ExactGroups + report.VariantGroups > 0;
            }
            catch (Exception ex)
            {
                status.Text = $"Scan failed: {ex.Message}";
                Android.Util.Log.Error(Tag, ex.ToString());
            }
            finally
            {
                _scanning = false;
                scanBtn.Enabled = true;
            }
        };

        _root.AddView(scanBtn);
        _root.AddView(status);
        _root.AddView(reviewBtn);

        // Height 0 + weight 1: the grid takes whatever vertical space the controls above and the
        // button below do not, and scrolls within it. This is the whole reason the native head
        // needs no equivalent of interop.js's scroll-windowing maths.
        grid.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f);
        _root.AddView(grid);

        _root.AddView(Button("Run Core self-test", async () =>
        {
            status.Text = "Running self-test…";
            var report = await Task.Run(() =>
            {
                var ext = GetExternalFilesDir(null)?.AbsolutePath ?? CacheDir!.AbsolutePath;
                return CoreSelfTest.Format(CoreSelfTest.Run(
                    Path.Combine(ext, "selftest"), Path.Combine(ext, "models", "nsfw.onnx")));
            });
            foreach (var line in report.Split('\n')) Android.Util.Log.Info("SnapZapSelfTest", line);
            status.Text = report;
        }));
    }

    /// <summary>Pads the content in by whatever the system bars actually occupy on this device.</summary>
    sealed class InsetsPadder : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
        {
            var bars = insets.GetInsets(WindowInsets.Type.SystemBars());
            v.SetPadding(24 + bars.Left, 24 + bars.Top, 24 + bars.Right, 24 + bars.Bottom);
            return insets;
        }
    }

    // ---- tiny view helpers, so the screen code above reads as layout rather than as plumbing ----

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
}
