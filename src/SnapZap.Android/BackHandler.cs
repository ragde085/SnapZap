using Android.App;
using Android.Window;

namespace SnapZap.Droid;

/// <summary>
/// Intercepts the Back gesture in a way that still works on API 36.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> Overriding <c>Activity.OnBackPressed</c> silently stops working
/// for apps targeting SDK 36: predictive back is on by default there, and the platform routes Back
/// through <see cref="OnBackInvokedDispatcher"/> instead — the legacy override is simply never
/// called. Nothing warns you. The activity just finishes.</para>
///
/// <para>Found in QA, and it had broken two behaviours that both looked like unrelated bugs: the
/// Folders screen never returned the folder you drilled to (so choosing a folder appeared to do
/// nothing), and Back in selection mode closed the app instead of cancelling the mode — losing the
/// selection and dropping the user on the launcher.</para>
///
/// <para>Registered per-activity and disposed with it. The pre-33 path keeps working through the
/// caller's own <c>OnBackPressed</c> override, so both eras are covered without either branch
/// second-guessing the other.</para>
/// </remarks>
public sealed class BackHandler : Java.Lang.Object, IOnBackInvokedCallback
{
    readonly Action _onBack;
    readonly Activity _activity;
    bool _registered;

    BackHandler(Activity activity, Action onBack)
    {
        _activity = activity;
        _onBack = onBack;
    }

    /// <summary>
    /// Registers <paramref name="onBack"/> to run instead of the default Back behaviour.
    /// Returns null on pre-33 devices, where the caller's <c>OnBackPressed</c> override still runs.
    /// </summary>
    public static BackHandler? Attach(Activity activity, Action onBack)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return null;

        var h = new BackHandler(activity, onBack);
        // The binding exposes the dispatcher as an interface, so the priority constant lives on
        // IOnBackInvokedDispatcher rather than on a class of that name.
        activity.OnBackInvokedDispatcher!.RegisterOnBackInvokedCallback(
            IOnBackInvokedDispatcher.PriorityDefault, h);
        h._registered = true;
        return h;
    }

    public void OnBackInvoked() => _onBack();

    /// <summary>Unregisters. Safe to call twice; must be called from the activity's OnDestroy.</summary>
    public void Detach()
    {
        if (!_registered || !OperatingSystem.IsAndroidVersionAtLeast(33)) return;
        _activity.OnBackInvokedDispatcher!.UnregisterOnBackInvokedCallback(this);
        _registered = false;
    }
}
