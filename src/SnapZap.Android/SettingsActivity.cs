using Android.App;
using Android.OS;
using Android.Views;
using Android.Widget;
using SnapZap.Core.Dedup;

namespace SnapZap.Droid;

/// <summary>
/// What the app is currently configured to do, and where those numbers came from.
/// </summary>
/// <remarks>
/// <para><b>Read-only, and that is the whole point of the first version.</b> The desktop's Setup
/// panel can edit these; Android could not even see them, which mattered because
/// <c>DedupSettings</c> lives in the catalogue's <c>meta</c> table (CLAUDE.md) rather than in
/// per-app preferences. A folder deduplicated on the desktop with a loosened
/// <c>VariantMaxBits</c> and then opened on the phone behaves differently from a phone-only
/// catalogue, with nothing on screen to explain why. Showing the numbers closes that; editing them
/// is a separate decision, because a threshold change invalidates <c>images.dupe_checked_kinds</c>
/// and forces a re-detect, and that is a conversation this screen does not have yet.</para>
///
/// <para>The storage read-out is the other half. App-private data — catalogue, thumbnails, trash —
/// is invisible to a phone's file manager, so "SnapZap is using 2.1 GB" was unanswerable from
/// inside the app.</para>
/// </remarks>
[Activity(Label = "Settings", Theme = "@android:style/Theme.Material.Light.NoActionBar")]
public sealed class SettingsActivity : Activity
{
    AndroidCatalog? _catalog;
    BackHandler? _back;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _back = BackHandler.Attach(this, Finish);

        var page = new LinearLayout(this) { Orientation = Orientation.Vertical };
        page.SetBackgroundColor(Design.Bg);
        SetContentView(page);
        page.SetOnApplyWindowInsetsListener(new InsetsPadder());
        page.RequestApplyInsets();

        page.AddView(Design.HeaderBar(this, "‹", Finish, "Settings", "What this build is set to do"));

        var body = new LinearLayout(this) { Orientation = Orientation.Vertical };
        var pad = Design.Dp(this, 16);
        body.SetPadding(pad, Design.Dp(this, 12), pad, Design.Dp(this, 24));

        try
        {
            _catalog = new AndroidCatalog();
            BuildDedup(body, DedupSettings.Load(_catalog.Db));
            BuildContent(body);
            BuildStorage(body);
        }
        catch (Exception ex)
        {
            body.AddView(Design.Note(this, $"Could not read the catalogue: {ex.Message}"));
            Android.Util.Log.Warn("SnapZap", ex.ToString());
        }

        BuildAbout(body);

        var scroll = new ScrollView(this);
        scroll.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f);
        scroll.AddView(body, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        page.AddView(scroll);
    }

    void Section(LinearLayout body, string eyebrow, string? note = null)
    {
        body.AddView(Design.Gap(this, 14));
        body.AddView(Design.Eyebrow(this, eyebrow));
        if (note is not null)
        {
            body.AddView(Design.Gap(this, 4));
            body.AddView(Design.Note(this, note));
        }
        body.AddView(Design.Gap(this, 6));
    }

    void BuildDedup(LinearLayout body, DedupSettings s)
    {
        Section(body, "Duplicate detection",
            "Stored in this catalogue, not in app preferences — so these are the same numbers the "
            + "desktop app uses when it opens the same catalogue, and they reset when it is deleted.");

        // Exact and Burst are shown as always-on rather than omitted: their absence from a settings
        // list is the kind of thing that reads as "not running". Both are load-bearing safety
        // behaviour and neither has a switch, by design (CLAUDE.md).
        body.AddView(Design.KeyValue(this, "Identical files", "Always on"));
        body.AddView(Design.KeyValue(this, "Burst frames", "Always on"));
        body.AddView(Design.KeyValue(this, "Same shot, re-encoded", s.VariantEnabled ? "On" : "Off"));
        body.AddView(Design.KeyValue(this, "Match strictness",
            $"{s.VariantMaxBits} of {PerceptualHash.Bits} bits"));
        body.AddView(Design.KeyValue(this, "Match rotations", s.VariantRotations ? "Yes" : "No"));
        body.AddView(Design.KeyValue(this, "Burst window", $"{s.BurstWindowSeconds}s"));
        body.AddView(Design.KeyValue(this, "Burst distance",
            $"{s.BurstMaxBits} of {PerceptualHash.Bits} bits"));

        body.AddView(Design.Gap(this, 8));
        body.AddView(Design.Note(this,
            "Burst frames are never swept into a bulk delete — they are different photographs taken "
            + "seconds apart, not copies. Change these on the desktop; a change re-runs detection."));
    }

    void BuildContent(LinearLayout body)
    {
        Section(body, "Content review");
        body.AddView(Design.KeyValue(this, "NSFW scoring", "Not available"));
        body.AddView(Design.Gap(this, 8));
        body.AddView(Design.Note(this,
            "SnapZap holds no internet permission, so it cannot fetch the 328 MB scoring model — "
            + "which is the trade being made deliberately. Nothing this app reads can leave the "
            + "device. Score on the desktop instead; the results travel in the catalogue."));
    }

    void BuildStorage(LinearLayout body)
    {
        Section(body, "Storage",
            "App-private and invisible to your file manager. Uninstalling removes all of it.");

        body.AddView(Design.KeyValue(this, "Catalogue", CatalogSize(_catalog!.AppDataDir)));
        body.AddView(Design.KeyValue(this, "Thumbnails", DirSize(_catalog.ThumbDir)));
        body.AddView(Design.KeyValue(this, "Trash", DirSize(_catalog.TrashDir)));

        body.AddView(Design.Gap(this, 8));
        var note = Design.Note(this,
            "Thumbnails are kept after a photo is deleted — History shows them, and losing them "
            + "would leave a restore with nothing to preview. Empty the trash from Trash & history.");
        body.AddView(note);
    }

    void BuildAbout(LinearLayout body)
    {
        Section(body, "About");
        var pkg = PackageManager?.GetPackageInfo(PackageName!, 0);
        body.AddView(Design.KeyValue(this, "Version", pkg?.VersionName ?? "—"));
        body.AddView(Design.KeyValue(this, "Network access", "None"));
        body.AddView(Design.Gap(this, 8));
        body.AddView(Design.Note(this,
            "The Android build declares no INTERNET permission at all, which the system enforces "
            + "regardless of what the app does."));
    }

    /// <summary>
    /// The catalogue plus its write-ahead log and shared-memory file.
    /// </summary>
    /// <remarks>
    /// <c>catalog.db</c> alone reported 4 KB for a catalogue holding a real library: SQLite runs in
    /// WAL mode here, so everything written since the last checkpoint lives in <c>catalog.db-wal</c>
    /// and lands in the main file only later. Reading just the one file gives a number that is
    /// wrong by most of the data and swings wildly for no reason the user can see.
    /// </remarks>
    static string CatalogSize(string appDataDir)
    {
        try
        {
            long total = 0;
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var f = Path.Combine(appDataDir, "catalog.db" + suffix);
                if (File.Exists(f)) total += new FileInfo(f).Length;
            }
            return total == 0 ? "—" : Format(total);
        }
        catch (Exception) { return "—"; }
    }

    /// <summary>
    /// Deliberately walks the whole tree rather than sampling: these directories hold thousands of
    /// small files, and a wrong number here is worse than a slow one. Runs on the UI thread because
    /// it is app-private internal storage with a few thousand entries, not the user's library —
    /// if that assumption ever stops holding this is the first thing to move off it.
    /// </summary>
    static string DirSize(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return "—";
            long total = 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; }
                catch (Exception) { /* a file that vanished mid-walk is not worth failing over */ }
            }
            return Format(total);
        }
        catch (Exception) { return "—"; }
    }

    static string Format(long b) => b switch
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
        _catalog?.Dispose();
        base.OnDestroy();
    }
}
