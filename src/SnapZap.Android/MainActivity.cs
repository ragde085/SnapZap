using Android.App;
using Android.OS;
using Android.Widget;

namespace SnapZap.Droid;

/// <summary>
/// Stage 1 of the §0 spike (docs/ANDROID-PORT-PLAN.md): the smallest thing that proves the
/// Android toolchain produces an APK that installs and runs. No Kestrel, no WebView, no
/// SnapZap.Core reference yet — each of those is added in its own step so a failure names its
/// own cause instead of arriving as one undifferentiated "the Android build doesn't work".
/// </summary>
[Activity(Label = "SnapZap", MainLauncher = true)]
public sealed class MainActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(new TextView(this) { Text = "SnapZap Android head — stage 1 alive" });
    }
}
