using Android.App;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace SnapZap.Droid;

/// <summary>
/// Stage 2 of the port. Not a UI — a harness that runs <see cref="CoreSelfTest"/> on the device
/// and shows the result, because the questions it answers (does SkiaSharp load, is the perceptual
/// hash bit-identical to the desktop's, does SQLite open) gate every design decision above them
/// and cannot be answered on the host.
///
/// Results go to both the screen and logcat — logcat so a run can be captured as evidence per
/// docs/ANDROID-VERIFY.md, the screen so a device with no cable attached still reports.
/// </summary>
[Activity(Label = "SnapZap Core self-test", MainLauncher = true)]
public sealed class MainActivity : Activity
{
    const string Tag = "SnapZapSelfTest";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var text = new TextView(this)
        {
            Text = "Running SnapZap.Core self-test…",
            TextSize = 12f,
        };
        text.SetPadding(24, 24, 24, 24);
        text.SetTextIsSelectable(true);

        var scroll = new ScrollView(this);
        scroll.AddView(text, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        SetContentView(scroll);

        // Off the UI thread: the self-test does real decoding, hashing and file IO, and Android
        // will ANR an Activity that does that in OnCreate.
        _ = Task.Run(() =>
        {
            string report;
            bool allPassed;
            try
            {
                // GetExternalFilesDir(null) is the app's own external directory — writable with no
                // permission at all, even under scoped storage. The real storage model
                // (MANAGE_EXTERNAL_STORAGE, docs/ANDROID-PORT-PLAN.md §3) is a later step; this
                // only needs somewhere to put a PNG and a SQLite file.
                var work = Path.Combine(
                    GetExternalFilesDir(null)?.AbsolutePath ?? CacheDir!.AbsolutePath,
                    "selftest");

                var results = CoreSelfTest.Run(work);
                allPassed = results.All(r => r.Pass);
                report = CoreSelfTest.Format(results);
            }
            catch (Exception ex)
            {
                allPassed = false;
                report = $"[FAIL] harness crashed before reporting\n{ex}";
            }

            var header = allPassed ? "ALL CHECKS PASSED\n\n" : "SOME CHECKS FAILED\n\n";
            foreach (var line in (header + report).Split('\n'))
                Android.Util.Log.Info(Tag, line);

            RunOnUiThread(() => text.Text = header + report);
        });
    }
}
