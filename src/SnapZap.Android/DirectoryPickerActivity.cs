using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace SnapZap.Droid;

/// <summary>
/// Browses the real filesystem to choose a folder to scan.
/// </summary>
/// <remarks>
/// <para><b>Not the same thing as <see cref="FoldersActivity"/>, and the difference is the whole
/// reason this exists.</b> That screen derives its tree from photos already in the catalogue, so
/// it can only ever walk inside what has been scanned — perfect for narrowing the library, useless
/// for reaching somewhere new. Picking a <i>scan target</i> needs the actual filesystem, which is
/// what this reads with <c>Directory.GetDirectories</c>.</para>
///
/// <para>The desktop has had this all along as <c>DirectoryPickerDialog</c>; Android was making do
/// with a typed path, which is a poor thing to ask of a phone keyboard and gave no way to discover
/// where the photos actually are.</para>
///
/// <para>Hidden folders are skipped, matching the desktop picker: a photo library is not in
/// <c>.thumbnails</c>, and listing them buries the folders that matter.</para>
/// </remarks>
[Activity(Label = "Choose folder", Theme = "@android:style/Theme.Material.Light.NoActionBar")]
public sealed class DirectoryPickerActivity : Activity
{
    /// <summary>Input: where to start browsing. Output: the folder the user chose.</summary>
    public const string ExtraPath = "snapzap.picker.path";

    string _current = "";

    /// <summary>
    /// The ceiling for browsing, and the folder labelled "Internal storage" — <b>not</b> the
    /// default start.
    /// </summary>
    /// <remarks>
    /// These were the same value until <c>DefaultStart</c> learned to prefer <c>DCIM/Camera</c>,
    /// at which point using it here silently made DCIM the top of the tree: Back stopped there,
    /// every folder outside it became unreachable, and the header labelled DCIM "Internal storage".
    /// Where a scan starts and how far a browser may walk up are different questions, and this one
    /// is always primary storage.
    /// </remarks>
    readonly string _root = SnapZap.Core.Platform.DirectoryRoots.AndroidPrimaryStorage;

    LinearLayout _body = null!;
    TextView _header = null!, _crumb = null!;
    BackHandler? _back;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _back = BackHandler.Attach(this, HandleBack);

        var start = Intent?.GetStringExtra(ExtraPath);
        _current = !string.IsNullOrEmpty(start) && Directory.Exists(start) ? start! : _root;

        var page = new LinearLayout(this) { Orientation = Orientation.Vertical };
        page.SetBackgroundColor(Design.Bg);
        page.FocusableInTouchMode = true;
        SetContentView(page);
        page.SetOnApplyWindowInsetsListener(new InsetsPadder());
        page.RequestApplyInsets();

        // header
        var head = new LinearLayout(this) { Orientation = Orientation.Vertical };
        head.SetPadding(Design.Dp(this, 16), Design.Dp(this, 10), Design.Dp(this, 16), Design.Dp(this, 12));
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        var up = Design.IconButton(this, "‹", "Up one folder");
        up.LayoutParameters = new LinearLayout.LayoutParams(Design.Dp(this, 40), Design.Dp(this, 40))
        { RightMargin = Design.Dp(this, 10) };
        up.Click += (_, _) => HandleBack();
        row.AddView(up);
        _header = Design.Title(this, "", 20f);
        _header.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        row.AddView(_header);
        head.AddView(row);
        _crumb = Design.Note(this, "");
        head.AddView(_crumb);
        page.AddView(head);
        page.AddView(Design.Rule(this, 2f, Design.Divider));

        // list
        var scroll = new ScrollView(this);
        scroll.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f);
        _body = new LinearLayout(this) { Orientation = Orientation.Vertical };
        scroll.AddView(_body, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        page.AddView(scroll);

        // the commit action names the folder it will use, so there is no doubt which level applies
        var bar = Design.ActionBar(this);
        var body = Design.ActionBarBody(bar);
        var use = Design.Button(this, "Scan this folder", Design.Btn.Primary, 52f);
        use.LayoutParameters = Design.Wide();
        use.Click += (_, _) => Choose(_current);
        body.AddView(use);
        page.AddView(bar);
        _useButton = use;

        Show();
    }

    Button _useButton = null!;

    string Label(string path)
    {
        if (path.TrimEnd('/') == _root.TrimEnd('/')) return "Internal storage";
        var name = System.IO.Path.GetFileName(path.TrimEnd('/'));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    void Show()
    {
        // "/storage/emulated/0" is the primary shared-storage mount, and its last path segment is
        // literally "0" — a correct filename and a meaningless heading. Name it what the user's
        // phone calls it instead.
        _header.Text = Label(_current);
        _crumb.Text = _current;
        _useButton.Text = $"Scan “{Label(_current)}”";

        _body.RemoveAllViews();

        List<string> dirs;
        try
        {
            dirs = Directory.GetDirectories(_current)
                .Where(d => System.IO.Path.GetFileName(d) is { Length: > 0 } name && name[0] != '.')
                .OrderBy(d => System.IO.Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // A folder Android will not let us list is a normal outcome on shared storage, not a
            // failure worth a dialog — say so in place and let the user go back up.
            var err = Design.Note(this, $"Cannot open this folder: {ex.Message}");
            err.SetPadding(Design.Dp(this, 16), Design.Dp(this, 16), Design.Dp(this, 16), Design.Dp(this, 16));
            _body.AddView(err);
            return;
        }

        if (dirs.Count == 0)
        {
            var none = Design.Note(this, "No subfolders here. Scan this folder, or go back up.");
            none.SetPadding(Design.Dp(this, 16), Design.Dp(this, 16), Design.Dp(this, 16), Design.Dp(this, 16));
            _body.AddView(none);
            return;
        }

        foreach (var d in dirs)
        {
            var row = Design.ListRow(this, System.IO.Path.GetFileName(d), CountHint(d), "›", "📁");
            row.Click += (_, _) => { _current = d; Show(); };
            _body.AddView(row);
        }
    }

    /// <summary>
    /// A cheap "is there anything in here" hint. Deliberately shallow — one level, capped — because
    /// this runs per row on the UI thread and a recursive count over shared storage would stall the
    /// list. It answers "is this worth opening", not "how many photos will the scan find".
    /// </summary>
    static string? CountHint(string dir)
    {
        try
        {
            var files = Directory.EnumerateFiles(dir).Take(51).Count();
            var subs = Directory.EnumerateDirectories(dir).Take(2).Count();
            return (files, subs) switch
            {
                (0, 0) => "empty",
                (0, _) => "subfolders",
                (> 50, _) => "50+ files",
                _ => $"{files} file(s)",
            };
        }
        catch (Exception) { return null; }   // unreadable is not worth a row that shouts about it
    }

    void HandleBack()
    {
        var parent = System.IO.Path.GetDirectoryName(_current.TrimEnd('/'));
        if (_current == _root || string.IsNullOrEmpty(parent)) { SetResult(Result.Canceled); Finish(); return; }
        _current = parent!;
        Show();
    }

    public override void OnBackPressed() => HandleBack();

    void Choose(string folder)
    {
        var data = new Intent();
        data.PutExtra(ExtraPath, folder);
        SetResult(Result.Ok, data);
        Finish();
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
        _back?.Detach();
        base.OnDestroy();
    }
}
