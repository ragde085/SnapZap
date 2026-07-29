using Android.App;
using Android.OS;
using Android.Views;
using Android.Widget;
using SnapZap.Core.Dedup;

namespace SnapZap.Droid;

/// <summary>
/// What the app is configured to do, and where to change it.
/// </summary>
/// <remarks>
/// <para><b>Deliberately the same surface as the desktop's <c>SetupDialog</c></b>, control for
/// control: variant detection on/off, rotation matching, how different two photos may look, and the
/// burst window. Exact and Burst detection have no switch on either head, and <c>BurstMaxBits</c> is
/// exposed on neither — a loose gate that only stops a camera left running across two scenes from
/// merging them is not a number a user has any way to reason about. Ranges and step sizes match the
/// desktop's sliders so neither head can express a setting the other cannot.</para>
///
/// <para><b>Why these live here and not in app preferences.</b> <c>DedupSettings</c> is stored in the
/// catalogue's <c>meta</c> table, because <c>images.dupe_checked_kinds</c> only means anything
/// against the settings that produced it and both must reset with <c>catalog.db</c> (CLAUDE.md).
/// The practical consequence is worth stating on screen, and is: a catalogue carries its settings,
/// so a folder tuned on the desktop behaves the same way when the phone opens it.</para>
///
/// <para><b>Changing a threshold does not change any result on its own.</b> Unlike the NSFW bands —
/// which re-judge scores already in hand — these decide what detection *finds*, so nothing moves
/// until detection runs again. That is why this screen tracks a dirty flag and offers to re-run it
/// rather than quietly leaving the old groups on display, which is exactly the "turning the setting
/// on appears to do nothing" failure the codebase keeps designing out.</para>
/// </remarks>
[Activity(Label = "Settings", Theme = "@android:style/Theme.Material.Light.NoActionBar")]
public sealed class SettingsActivity : Activity
{
    AndroidCatalog? _catalog;
    BackHandler? _back;
    DedupSettings _dedup = new();

    LinearLayout _body = null!;

    /// <summary>Settings have changed since detection last ran, so the groups on display are stale.</summary>
    bool _stale;
    bool _rerunning;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _back = BackHandler.Attach(this, Finish);

        var page = new LinearLayout(this) { Orientation = Orientation.Vertical };
        page.SetBackgroundColor(Design.Bg);
        SetContentView(page);
        page.SetOnApplyWindowInsetsListener(new InsetsPadder());
        page.RequestApplyInsets();

        page.AddView(Design.HeaderBar(this, "‹", Finish, "Settings", "How duplicates are found"));

        var scroll = new ScrollView(this);
        scroll.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f);
        _body = new LinearLayout(this) { Orientation = Orientation.Vertical };
        scroll.AddView(_body, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        page.AddView(scroll);

        try
        {
            _catalog = new AndroidCatalog();
            _dedup = DedupSettings.Load(_catalog.Db);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("SnapZap", ex.ToString());
        }

        Render();
    }

    /// <summary>
    /// Persists immediately on every change rather than behind a Save button — the same call the
    /// desktop's <c>SetupDialog.Set</c> makes, and for the same reason: there is nothing to cancel,
    /// because settings only take effect on the next detection run, which the user starts
    /// explicitly. A Save button here would only be a way to lose a change by pressing Back.
    /// </summary>
    void Set(DedupSettings next)
    {
        _dedup = next;
        try { _dedup.Save(_catalog!.Db); }
        catch (Exception ex)
        {
            Toast.MakeText(this, $"Could not save: {ex.Message}", ToastLength.Long)!.Show();
            Android.Util.Log.Error("SnapZap", ex.ToString());
            return;
        }
        _stale = true;
        Render();
    }

    void Render()
    {
        _body.RemoveAllViews();
        if (_catalog is null)
        {
            _body.AddView(Pad(Design.Note(this, "Could not open the catalogue.")));
            return;
        }

        if (_stale) _body.AddView(StaleCallout());

        Section("Duplicate detection",
            "Stored in this catalogue, not in app preferences — so these are the same settings the "
            + "desktop uses when it opens it, and they reset when it is deleted.");

        // No switch on either of these, on either head. Exact because a duplicate finder that cannot
        // find identical files is not one; Burst because the rule that withholds different
        // photographs from a bulk delete is not something a checkbox should be able to turn off.
        _body.AddView(Design.ListRow(this, "Identical files", "Always on. Byte-for-byte copies.", "On"));
        _body.AddView(Design.ListRow(this, "Bursts — the same scene, seconds apart",
            "Always detected. These are different photographs, so they are grouped for review and "
            + "never picked up by “Select duplicate extras”.", "On"));

        _body.AddView(Design.ToggleRow(this, "The same shot at another size",
            "Resized, re-saved or converted copies of one photo. Safe to bulk-select.",
            _dedup.VariantEnabled,
            on => Set(_dedup with { VariantEnabled = on })));

        if (_dedup.VariantEnabled)
        {
            _body.AddView(Design.ToggleRow(this, "Also match rotated copies", null,
                _dedup.VariantRotations,
                on => Set(_dedup with { VariantRotations = on })));

            // 4..60 in twos, exactly the desktop's range. Above 60 the band prefilter's width
            // collapses and matching falls back to the brute-force sweep (docs/DEDUP-V2.md), which
            // is a reason not to offer it rather than a reason to warn about it.
            _body.AddView(Design.SliderRow(this, "How different they may look",
                "Lower is stricter. Raising it finds more, and eventually starts calling two "
                + "different photos the same one.",
                _dedup.VariantMaxBits, 4, 60, 2,
                v => $"{v} of {PerceptualHash.Bits}",
                v => Set(_dedup with { VariantMaxBits = v })));

            if (_dedup.VariantMaxBits > new DedupSettings().VariantMaxBits)
                _body.AddView(Warning(
                    $"Above the default of {new DedupSettings().VariantMaxBits}. This feeds a tool "
                    + "that deletes things, so a false match costs more than a miss."));
        }

        _body.AddView(Design.SliderRow(this, "Frames within",
            "Needs an EXIF capture time — photos without one are skipped, so a burst with no "
            + "timestamps is not protected from bulk selection.",
            _dedup.BurstWindowSeconds, 1, 30, 1,
            v => $"{v} s",
            v => Set(_dedup with { BurstWindowSeconds = v })));

        var reset = Design.Button(this, "Reset to defaults", Design.Btn.Ghost, 44f);
        reset.LayoutParameters = Design.Wide();
        reset.Click += (_, _) => Set(new DedupSettings());
        _body.AddView(Pad(reset));

        BuildContent();
        BuildStorage();
        BuildAbout();
    }

    /// <summary>
    /// The bridge between "I changed a number" and "the app shows something different".
    /// </summary>
    /// <remarks>
    /// These settings decide what detection finds, so nothing on any other screen moves until it
    /// runs again — the groups, the Review badge and the extras count are all still the previous
    /// run's answer. Saying so, and offering the re-run here rather than sending the user back to
    /// the Plan tab to work it out, is the difference between a setting that works and one that
    /// appears to do nothing.
    /// </remarks>
    View StaleCallout()
    {
        var callout = Design.Callout(this);
        callout.AddView(Design.Body(this, "Detection has not run with these settings", 15f, bold: true));
        callout.AddView(Design.Note(this,
            "Duplicate groups, the Review queue and the counts are still from the previous run.",
            Design.Accent700));
        callout.AddView(Design.Gap(this, 10));

        var run = Design.Button(this,
            _rerunning ? "Finding duplicates…" : "Find duplicates again", Design.Btn.Primary, 52f);
        run.LayoutParameters = Design.Wide();
        run.Enabled = !_rerunning;
        run.Click += (_, _) => Rerun();
        callout.AddView(run);
        return callout;
    }

    async void Rerun()
    {
        if (_rerunning) return;

        var root = _catalog!.ScanRoot;
        if (string.IsNullOrEmpty(root))
        {
            Toast.MakeText(this, "Nothing scanned yet — scan a folder first.", ToastLength.Long)!.Show();
            return;
        }

        _rerunning = true;
        Render();
        WorkService.Start(this, "Finding duplicates");

        try
        {
            var progress = new Progress<DedupProgress>(p =>
                WorkService.Report(this, "Finding duplicates", p.Detail, p.Done, p.Total));

            var report = await Task.Run(() =>
                new DuplicateService(_catalog.Db, _catalog.Imaging, _catalog.ThumbDir)
                    .DetectAsync(root!, progress));

            _stale = false;
            Toast.MakeText(this,
                $"{report.ExactGroups + report.VariantGroups + report.BurstGroups:N0} groups found",
                ToastLength.Short)!.Show();
        }
        catch (Exception ex)
        {
            Toast.MakeText(this, $"Detection failed: {ex.Message}", ToastLength.Long)!.Show();
            Android.Util.Log.Error("SnapZap", ex.ToString());
        }
        finally
        {
            WorkService.Stop(this);
            _rerunning = false;
            Render();
        }
    }

    // ---- the read-only remainder ---------------------------------------------------------------

    void Section(string eyebrow, string? note = null)
    {
        var wrap = new LinearLayout(this) { Orientation = Orientation.Vertical };
        wrap.SetPadding(Design.Dp(this, 16), Design.Dp(this, 16), Design.Dp(this, 16), Design.Dp(this, 8));
        wrap.AddView(Design.Eyebrow(this, eyebrow));
        if (note is not null)
        {
            wrap.AddView(Design.Gap(this, 5));
            wrap.AddView(Design.Note(this, note));
        }
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

    void BuildContent()
    {
        Section("Content review");
        _body.AddView(Design.ListRow(this, "NSFW scoring",
            "SnapZap holds no internet permission, so it cannot fetch the 328 MB scoring model — "
            + "which is the trade being made deliberately. Nothing this app reads can leave the "
            + "device. Score on the desktop instead; the results travel in the catalogue.",
            "Desktop only"));
    }

    void BuildStorage()
    {
        Section("Storage", "App-private and invisible to your file manager. Uninstalling removes all of it.");

        _body.AddView(Design.ListRow(this, "Catalogue", null, CatalogSize(_catalog!.AppDataDir)));
        _body.AddView(Design.ListRow(this, "Thumbnails", null, DirSize(_catalog.ThumbDir)));
        _body.AddView(Design.ListRow(this, "Trash", null, DirSize(_catalog.TrashDir)));

        _body.AddView(Pad(Design.Note(this,
            "Thumbnails are kept after a photo is deleted — History shows them, and losing them "
            + "would leave a restore with nothing to preview. Empty the trash from Trash & history.")));
    }

    void BuildAbout()
    {
        Section("About");
        var pkg = PackageManager?.GetPackageInfo(PackageName!, 0);
        _body.AddView(Design.ListRow(this, "Version", null, pkg?.VersionName ?? "—"));
        _body.AddView(Design.ListRow(this, "Network access",
            "The Android build declares no INTERNET permission at all, which the system enforces "
            + "regardless of what the app does.", "None"));
        _body.AddView(Design.Gap(this, 24));
    }

    /// <summary>
    /// The catalogue plus its write-ahead log and shared-memory file.
    /// </summary>
    /// <remarks>
    /// <c>catalog.db</c> alone reported 4 KB for a catalogue holding a real library: SQLite runs in
    /// WAL mode here, so everything written since the last checkpoint lives in <c>catalog.db-wal</c>
    /// and lands in the main file only later. Reading just the one file gives a number that is
    /// wrong by most of the data and swings for no reason the user can see.
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
