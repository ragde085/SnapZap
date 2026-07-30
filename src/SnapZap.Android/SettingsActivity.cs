using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using SnapZap.Core.Dedup;
using SnapZap.Core.Scanning;

namespace SnapZap.Droid;

/// <summary>
/// What the app is configured to do, and where to change it.
/// </summary>
/// <remarks>
/// <para><b>Deliberately the same surface as the desktop's <c>SetupDialog</c></b>, control for
/// control: skip-hidden, variant detection on/off, rotation matching, how different two photos may
/// look, the burst switch and the burst window. Exact detection has no switch on either head, and
/// <c>BurstMaxBits</c> is exposed on neither — a loose gate that only stops a camera left running
/// across two scenes from merging them is not a number a user has any way to reason about. Ranges and
/// step sizes match the desktop's sliders so neither head can express a setting the other cannot.</para>
///
/// <para><b>Why these live here and not in app preferences.</b> <c>DedupSettings</c> is stored in the
/// catalogue's <c>meta</c> table, because <c>images.dupe_checked_kinds</c> only means anything
/// against the settings that produced it and both must reset with <c>catalog.db</c> (CLAUDE.md).
/// The practical consequence is worth stating on screen, and is: a catalogue carries its settings,
/// so a folder tuned on the desktop behaves the same way when the phone opens it.</para>
///
/// <para><b>Changing a setting here does not change any result on its own</b>, and the two kinds of
/// setting need different follow-up. Unlike the NSFW bands — which re-judge scores already in hand —
/// the dedup settings decide what detection *finds*, so this screen tracks a dirty flag and offers to
/// re-run detection itself. The scan settings decide which rows exist at all, which detection cannot
/// fix, so those say plainly that the folder needs scanning again. Both exist because "turning the
/// setting on appears to do nothing" is the failure this codebase keeps designing out.</para>
/// </remarks>
[Activity(Label = "Settings", Theme = "@android:style/Theme.Material.Light.NoActionBar")]
public sealed class SettingsActivity : Activity
{
    AndroidCatalog? _catalog;
    BackHandler? _back;
    DedupSettings _dedup = new();
    ScanSettings _scan = new();

    LinearLayout _body = null!;

    /// <summary>Settings have changed since detection last ran, so the groups on display are stale.</summary>
    bool _stale;

    /// <summary>
    /// A scan option changed. Deliberately a separate flag from <see cref="_stale"/>: this one
    /// cannot be resolved from here. Re-running detection would do nothing for it, because the
    /// setting governs which rows the <i>scan</i> writes, and the scan lives on the Plan tab.
    /// </summary>
    bool _scanStale;
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

        page.AddView(Design.HeaderBar(this, "‹", Finish, "Settings", "What gets scanned, and what counts as a duplicate"));

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
            _scan = ScanSettings.Load(_catalog.Db);
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

    /// <summary>
    /// Saves a scan option. Same save-immediately rule as <see cref="Set"/>, but it marks
    /// <see cref="_scanStale"/> rather than <see cref="_stale"/> — see that field.
    /// </summary>
    void SetScan(ScanSettings next)
    {
        _scan = next;
        try { _scan.Save(_catalog!.Db); }
        catch (Exception ex)
        {
            Toast.MakeText(this, $"Could not save: {ex.Message}", ToastLength.Long)!.Show();
            Android.Util.Log.Error("SnapZap", ex.ToString());
            return;
        }
        _scanStale = true;
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

        Section("Scanning");

        // Its own section above duplicate detection, because it is a different kind of setting: this
        // one decides which photos exist in the catalogue at all, so changing it needs a re-scan
        // rather than the re-detect the dedup settings below need.
        _body.AddView(Design.ToggleRow(this, "Skip hidden folders",
            _scan.SkipHidden
                ? "Folders whose name starts with a dot — .thumbnails, .trashed and the like. They "
                  + "hold caches, not photos."
                : "Off — hidden folders are scanned, including caches like .thumbnails.",
            _scan.SkipHidden,
            on => SetScan(_scan with { SkipHidden = on })));

        if (_scanStale)
            _body.AddView(Warning(
                "Scan the folder again for this to take effect — re-running duplicates is not enough. "
                + "Anything already indexed from a hidden folder drops out on the next scan."));

        Section("Duplicate detection",
            "Stored in this catalogue, not in app preferences — so these are the same settings the "
            + "desktop uses when it opens it, and they reset when it is deleted.");

        // Exact has no switch on either head: a duplicate finder that cannot find identical files
        // is not one.
        _body.AddView(Design.ListRow(this, "Identical files", "Always on. Byte-for-byte copies.", "On"));

        _body.AddView(Design.ToggleRow(this, "Bursts — the same scene, seconds apart",
            _dedup.BurstEnabled
                ? "Grouped for review and held back from “Select all”, because they are different "
                  + "photographs rather than copies."
                : "Off — burst frames are treated as copies and can be swept into a bulk delete.",
            _dedup.BurstEnabled,
            on => Set(_dedup with { BurstEnabled = on })));

        // Stated at the point of use, not in a help page. Turning this off does not leave bursts
        // alone — it moves them into a group that "Select all" will take. See
        // DedupSettings.BurstEnabled; the old version of this setting defaulted to off and the
        // protection was inert in the shipped configuration because of precisely this.
        if (!_dedup.BurstEnabled)
            _body.AddView(Warning(
                "All but one frame of every burst will be offered to a bulk delete. Turn this back "
                + "on and find duplicates again to restore the protection — nothing needs re-scanning."));

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

        // Gated on the switch above, like the variant thresholds are on theirs: a window that
        // nothing consults is a control that lies about having an effect.
        if (_dedup.BurstEnabled)
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
        BuildCatalogue();
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

    /// <summary>
    /// The desktop's "Forget everything", which Android had no equivalent of.
    /// </summary>
    /// <remarks>
    /// Without it, the only way to get a clean catalogue on a phone was to uninstall the app — the
    /// desktop can at least have its <c>catalog.db</c> deleted by hand, and app-private storage on
    /// Android cannot. A stale catalogue is not cosmetic either: it keeps every folder ever scanned,
    /// so old rows stay behind the folder scope and turn up again the moment the scope widens.
    /// </remarks>
    void BuildCatalogue()
    {
        var (photos, bytes) = _catalog!.Footprint();

        Section("Catalogue",
            "What SnapZap has worked out about your photos. Forgetting it never touches the photos "
            + "themselves — it just means the next scan starts from scratch.");

        _body.AddView(Design.ListRow(this, "Analysed", null, $"{photos:N0} photos"));
        _body.AddView(Design.ListRow(this, "Held", null, Format(bytes)));

        var forget = Design.Button(this, "Forget everything", Design.Btn.Secondary, 48f);
        forget.LayoutParameters = Design.Wide();
        forget.SetTextColor(Design.Accent700);
        forget.Click += (_, _) => ConfirmForget(photos);
        _body.AddView(Pad(forget));
    }

    /// <summary>
    /// Runs <see cref="CoreSelfTest"/> and shows the report.
    /// </summary>
    /// <remarks>
    /// <para>Off the UI thread: the NSFW branch constructs a 327.5 MiB ONNX session and runs eleven
    /// inferences, which is seconds at best and would otherwise trip the ANR watchdog.</para>
    ///
    /// <para>The model is looked for beside the catalogue — <c>&lt;app files&gt;/SnapZap/nsfw.onnx</c>
    /// — because the app has no network permission and no in-app route to fetch it, so the only way
    /// it gets there is <c>adb push</c>. Absent, the check reports a skip rather than a failure,
    /// matching how a missing model behaves everywhere else.</para>
    ///
    /// <para>Results go to the clipboard as well as the screen: the interesting part is a wall of
    /// numbers to paste back, not something to read on a phone.</para>
    /// </remarks>
    async void RunSelfTest(Button trigger)
    {
        trigger.Enabled = false;
        trigger.Text = "Running…";
        try
        {
            var work = Path.Combine(_catalog!.AppDataDir, "selftest");
            var model = FindNsfwModel();
            var report = await Task.Run(() => CoreSelfTest.Format(CoreSelfTest.Run(work, model)));

            Android.Util.Log.Info("SnapZap", "self-test\n" + report);
            try
            {
                var cb = (ClipboardManager?)GetSystemService(ClipboardService);
                cb?.PrimaryClip = ClipData.NewPlainText("SnapZap self-test", report);
            }
            catch (Exception) { /* a clipboard that refuses is not worth failing the run over */ }

            new AlertDialog.Builder(this)
                .SetTitle("Self-test")!
                .SetMessage(report)!
                .SetPositiveButton("Close", (EventHandler<DialogClickEventArgs>?)null)!
                .Show();
        }
        catch (Exception ex)
        {
            Toast.MakeText(this, $"Self-test failed: {ex.Message}", ToastLength.Long)!.Show();
            Android.Util.Log.Error("SnapZap", ex.ToString());
        }
        finally
        {
            trigger.Enabled = true;
            trigger.Text = "Run self-test";
        }
    }

    /// <summary>
    /// Where <c>nsfw.onnx</c> might be on this device, most convenient first.
    /// </summary>
    /// <remarks>
    /// <para><b>Shared storage comes first on purpose, and app-private storage is nearly useless
    /// here.</b> There is no in-app route to the model — no network permission — so it arrives by
    /// <c>adb push</c> or by the user's own browser download. Neither can write into
    /// <c>/data/user/0/…/files</c>: <c>adb push</c> has no access, and <c>run-as</c> only works on a
    /// debuggable build, which a release APK is not. Picking the app-private path as the only
    /// location would have meant the check could never run on the phone it was written for.</para>
    ///
    /// <para><c>Download/</c> leads because it is where both routes naturally land. The app holds
    /// <c>MANAGE_EXTERNAL_STORAGE</c>, so it can read all of these.</para>
    /// </remarks>
    string? FindNsfwModel()
    {
        var storage = SnapZap.Core.Platform.DirectoryRoots.AndroidPrimaryStorage;
        foreach (var candidate in new[]
        {
            Path.Combine(storage, "Download", "nsfw.onnx"),
            Path.Combine(storage, "SnapZap", "nsfw.onnx"),
            Path.Combine(_catalog!.AppDataDir, "nsfw.onnx"),
        })
        {
            try { if (File.Exists(candidate)) return candidate; }
            catch (Exception) { /* an unreadable candidate is just not the one */ }
        }
        return null;
    }

    void ConfirmForget(int photos)
    {
        new AlertDialog.Builder(this)
            .SetTitle("Forget everything?")!
            // Two facts the user needs and would otherwise have to infer: their photos are safe, and
            // History loses its previews because those *are* the thumbnail cache.
            .SetMessage($"{photos:N0} analysed photos, every duplicate group and the thumbnail cache "
                      + "will be cleared.\n\nYour photos are not touched — nothing is deleted from "
                      + "your device, and anything already in the trash can still be restored. Past "
                      + "History entries will lose their previews, because those come from the "
                      + "thumbnails being cleared.")!
            .SetNegativeButton("Cancel", (EventHandler<DialogClickEventArgs>?)null)!
            .SetPositiveButton("Forget everything", (_, _) =>
            {
                try
                {
                    _catalog!.Forget();
                    _stale = false;
                    _scanStale = false;
                    Toast.MakeText(this, "Catalogue cleared. Scan a folder to start again.",
                        ToastLength.Long)!.Show();
                }
                catch (Exception ex)
                {
                    Toast.MakeText(this, $"Could not clear: {ex.Message}", ToastLength.Long)!.Show();
                    Android.Util.Log.Error("SnapZap", ex.ToString());
                }
                Render();
            })!
            .Show();
    }

    void BuildAbout()
    {
        Section("About");
        var pkg = PackageManager?.GetPackageInfo(PackageName!, 0);
        _body.AddView(Design.ListRow(this, "Version", null, pkg?.VersionName ?? "—"));
        _body.AddView(Design.ListRow(this, "Network access",
            "The Android build declares no INTERNET permission at all, which the system enforces "
            + "regardless of what the app does.", "None"));

        // CoreSelfTest had no caller anywhere — it validated Core on-device during the port and was
        // only ever run by hand from a debugger. That made its results unreachable on a real phone,
        // which is precisely where they matter: it is the one thing that reports how long an NSFW
        // inference actually takes on this hardware, and that number decides whether the feature is
        // viable at all (ViT-base, ten inferences per photo — see the check's own remarks).
        var selfTest = Design.Button(this, "Run self-test", Design.Btn.Secondary, 44f);
        selfTest.LayoutParameters = Design.Wide();
        selfTest.Click += (_, _) => RunSelfTest(selfTest);
        _body.AddView(Pad(selfTest));
        _body.AddView(Pad(Design.Note(this,
            "Checks the shared engine on this device — hashing, perceptual signatures, SQLite, and "
            + "NSFW timing if nsfw.onnx is in Download/. Results copy "
            + "to the clipboard.")));
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
