namespace SnapZap.Core.Platform;

/// <summary>
/// The platform kinds <see cref="DirectoryRoots"/> answers differently for. Deliberately its
/// own enum rather than branching call sites on <c>OperatingSystem.IsWindows()</c>/
/// <c>IsAndroid()</c> directly: <c>OperatingSystem.IsAndroid()</c> always reports false on the
/// dev machine (there is no Android target in this solution yet — see
/// docs/ANDROID-PORT-PLAN.md), so a test running on macOS could never reach "the Android
/// answer" if the type only exposed a parameterless, auto-detecting entry point. Taking the
/// platform as a value lets a test ask for any answer regardless of what OS it's actually
/// running on.
/// </summary>
public enum DirectoryPlatform
{
    Windows,
    Android,

    /// <summary>Everything else this app runs on today — macOS and Linux. Not "Unix", because
    /// nothing here depends on POSIX specifics, just "not Windows, not Android".</summary>
    Other,
}

/// <summary>
/// Where the folder picker (<c>DirectoryPickerDialog.razor</c>) should start browsing, and
/// which quick-jump root buttons to offer, per platform. Pulled out of the Razor component —
/// which has no unit test seam of its own — so this logic can be pinned with plain xUnit tests
/// (<c>DirectoryRootsTests</c>) instead of only being exercised by hand.
///
/// Lives in SnapZap.Core, not SnapZap.App: every answer here is portable string logic
/// (<see cref="Environment.SpecialFolder"/>, <see cref="DriveInfo"/>) with no Android SDK types
/// involved and no new package reference, so there is no reason to keep it behind the App/Core
/// boundary. This is a deliberately different call than docs/ANDROID-PORT-PLAN.md §2 makes for
/// the Android <c>IPlatformServices</c> implementations (trash, hardlinks) — those need real
/// <c>Android.Content.Context</c>/<c>Android.Webkit</c> types and so belong in a future Android
/// host project instead, kept out of Core to avoid pulling an Android workload dependency into
/// a project that otherwise stays a plain portable .NET library.
/// </summary>
public static class DirectoryRoots
{
    /// <summary>
    /// The conventional primary shared-storage mount point on Android (what
    /// <c>Environment.getExternalStorageDirectory()</c> resolves to on every device tested
    /// against so far). Used as both the default start path and the sole quick-jump root.
    ///
    /// Follow-up, not v1 (docs/ANDROID-PORT-PLAN.md §3 item 5): enumerate additional
    /// volumes (an inserted SD card) the way <see cref="Roots"/> enumerates drive letters on
    /// Windows. Deliberately not built here — the two v1 test devices' removable-storage
    /// behaviour hasn't been characterized yet, and a "nice to have" root list built blind
    /// against nothing is more likely to be wrong than absent.
    /// </summary>
    public const string AndroidPrimaryStorage = "/storage/emulated/0";

    /// <summary>
    /// The real platform this process is running on, as a <see cref="DirectoryPlatform"/>.
    /// This is what <c>DirectoryPickerDialog.razor</c> passes in production; tests instead
    /// pass a literal <see cref="DirectoryPlatform"/> to reach an answer this machine can't
    /// naturally produce (e.g. the Android answer, on a macOS dev box).
    /// </summary>
    public static DirectoryPlatform CurrentPlatform =>
        OperatingSystem.IsWindows() ? DirectoryPlatform.Windows :
        OperatingSystem.IsAndroid() ? DirectoryPlatform.Android :
        DirectoryPlatform.Other;

    /// <summary>Where the picker starts when the caller has no existing path to anchor to
    /// (no <c>StartPath</c>, or one that no longer exists). Unchanged from the pre-seam
    /// behaviour for Windows and Other — both used <c>UserProfile</c> before this existed, and
    /// still do; only Android is new.</summary>
    public static string DefaultStart(DirectoryPlatform platform) => platform switch
    {
        DirectoryPlatform.Android => AndroidPrimaryStorage,
        _ => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    };

    /// <summary>
    /// Quick-jump buttons: every ready drive on Windows, home + filesystem root on macOS/Linux
    /// (there's no single "root" on Windows and no drive letters to enumerate elsewhere), the
    /// one primary-storage root on Android (see <see cref="AndroidPrimaryStorage"/>).
    /// </summary>
    /// <param name="platform">Which answer to give — see <see cref="DirectoryPlatform"/>.</param>
    /// <param name="windowsDrives">
    /// How to enumerate ready drives when <paramref name="platform"/> is
    /// <see cref="DirectoryPlatform.Windows"/>. Defaults to the real <see cref="DriveInfo"/>
    /// enumeration the dialog has always used; overridable so a test can pin the Windows branch
    /// deterministically without needing actual Windows drive letters to exist on the machine
    /// running the test (macOS has none).
    /// </param>
    public static IReadOnlyList<string> Roots(DirectoryPlatform platform, Func<IReadOnlyList<string>>? windowsDrives = null) =>
        platform switch
        {
            DirectoryPlatform.Windows => (windowsDrives ?? RealWindowsDrives)(),
            DirectoryPlatform.Android => [AndroidPrimaryStorage],
            _ => [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "/"],
        };

    static IReadOnlyList<string> RealWindowsDrives() =>
        DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName).ToList();
}
