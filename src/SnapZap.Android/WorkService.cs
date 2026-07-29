using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace SnapZap.Droid;

/// <summary>
/// Keeps a long-running photo operation alive when the app is not in front, and shows its progress
/// in the notification shade.
/// </summary>
/// <remarks>
/// <para><b>Why this has to exist.</b> Scanning a real library is minutes of continuous decoding,
/// hashing and thumbnail writing. Android will freeze or kill a backgrounded process doing that —
/// the moment the user switches apps or the screen sleeps — and from the user's side the app simply
/// "randomly stops". A foreground service with an ongoing notification is the only supported way to
/// tell the platform this work was asked for and should be allowed to finish.</para>
///
/// <para><b>What it does not do.</b> It does not perform the work: the operation still runs on a
/// background task owned by the activity, and this service exists to hold the process and publish
/// progress. That keeps the scanning code identical to the desktop's, with no Android service
/// plumbing threaded through <c>SnapZap.Core</c>.</para>
///
/// <para><b>Interruption is survivable either way.</b> The catalogue commits rows as it goes, so a
/// kill mid-scan loses no completed work and a re-scan resumes from the two-tier cache. This service
/// makes the interruption unlikely; it is not what makes it safe.</para>
/// </remarks>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class WorkService : Service
{
    public const string ChannelId = "snapzap.work";
    const int NotificationId = 1001;

    public const string ExtraTitle = "snapzap.work.title";

    static WorkService? _instance;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        _instance = this;
        var title = intent?.GetStringExtra(ExtraTitle) ?? "Working…";
        StartForeground(NotificationId, Build(title, null, 0, 0));

        // NotSticky: if the platform kills us anyway, do not resurrect the service on its own —
        // there would be no operation behind it, so the user would see a progress notification for
        // work that is not happening. The catalogue is already safe; a re-scan resumes from cache.
        return StartCommandResult.NotSticky;
    }

    Notification Build(string title, string? detail, int done, int total)
    {
        var open = PendingIntent.GetActivity(this, 0,
            new Intent(this, typeof(MainActivity)).AddFlags(ActivityFlags.SingleTop),
            PendingIntentFlags.Immutable);

        var b = new Notification.Builder(this, ChannelId)
            .SetContentTitle(title)!
            .SetSmallIcon(Android.Resource.Drawable.StatSysDownload)!
            .SetOngoing(true)!            // not swipe-dismissable: dismissing it would not stop the work
            .SetOnlyAlertOnce(true)!
            .SetContentIntent(open)!;

        if (detail is not null) b.SetContentText(detail);
        if (total > 0) b.SetProgress(total, done, false);
        else b.SetProgress(0, 0, true);   // indeterminate until a total is known

        return b.Build();
    }

    /// <summary>Updates the ongoing notification in place. No-op when the service is not running.</summary>
    public static void Report(Context context, string title, string? detail, int done, int total)
    {
        if (_instance is null) return;
        var mgr = (NotificationManager)context.GetSystemService(NotificationService)!;
        mgr.Notify(NotificationId, _instance.Build(title, detail, done, total));
    }

    public override void OnDestroy()
    {
        _instance = null;
        base.OnDestroy();
    }

    // ---- lifecycle helpers used by the activities -------------------------------------------

    /// <summary>
    /// Creates the notification channel. Idempotent, and safe to call on every launch — creating an
    /// existing channel is a documented no-op, and the user's own changes to it are preserved.
    /// </summary>
    public static void EnsureChannel(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        var mgr = (NotificationManager)context.GetSystemService(NotificationService)!;
        var channel = new NotificationChannel(ChannelId, "Photo operations", NotificationImportance.Low)
        {
            Description = "Progress for scanning, duplicate detection, deleting and restoring.",
        };
        // Low importance and no sound: this is a progress readout, not an alert. A library scan
        // that pinged would be worse than no notification at all.
        channel.SetSound(null, null);
        channel.EnableVibration(false);
        mgr.CreateNotificationChannel(channel);
    }

    public static void Start(Context context, string title)
    {
        var i = new Intent(context, typeof(WorkService));
        i.PutExtra(ExtraTitle, title);
        if (OperatingSystem.IsAndroidVersionAtLeast(26)) context.StartForegroundService(i);
        else context.StartService(i);
    }

    public static void Stop(Context context) => context.StopService(new Intent(context, typeof(WorkService)));
}
