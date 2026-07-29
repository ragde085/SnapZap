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
    /// still do.</summary>
    /// <param name="exists">
    /// How to test whether a candidate folder is really there. Defaults to
    /// <see cref="Directory.Exists(string)"/>; overridable so a test can pin the Android answer on
    /// a machine that has no <c>/storage/emulated/0</c>.
    /// </param>
    /// <remarks>
    /// <para><b>Android no longer starts at the whole of internal storage.</b> It used to return
    /// <see cref="AndroidPrimaryStorage"/> flat, which made "scan everything on this phone" the
    /// pre-filled, path-of-least-resistance choice on first run — sweeping in <c>Android/data</c>,
    /// app caches, sticker packs and messaging-app media. The cost is not just a slow first scan:
    /// every duplicate group and the whole review queue is then drawn from that junk, before the
    /// user has made a single decision. <c>DCIM/Camera</c> is where a phone actually puts photos,
    /// so that is where this starts, with primary storage as the fallback when it does not exist.
    /// Scanning everything is still one tap away via <see cref="PhotoFolders"/> — it is now a
    /// choice rather than the default.</para>
    ///
    /// <para>Desktop is deliberately left on <c>UserProfile</c>. The equivalent complaint does not
    /// apply — a home folder is not "the entire drive" — and quietly moving where an existing
    /// installation's picker opens is a behaviour change nobody asked for. Both heads do get the
    /// same new quick-jumps, through <see cref="Roots"/>.</para>
    /// </remarks>
    public static string DefaultStart(DirectoryPlatform platform, Func<string, bool>? exists = null)
    {
        if (platform != DirectoryPlatform.Android)
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var probe = exists ?? Directory.Exists;
        return PhotoFolders(platform, probe).FirstOrDefault() ?? AndroidPrimaryStorage;
    }

    /// <summary>
    /// Well-known photo locations for the platform, most-likely first, filtered to the ones that
    /// actually exist. Offered as quick-jumps beside the storage roots so reaching a photo folder
    /// does not mean drilling down to it — or, on Android, typing a path on a phone keyboard.
    /// </summary>
    /// <remarks>
    /// Ordering is the point, not completeness: <c>DCIM/Camera</c> before <c>DCIM</c> before
    /// <c>Pictures</c> is descending confidence that what is in there was taken by this phone's
    /// camera. <see cref="DefaultStart"/> takes the first, so a change to this order changes what a
    /// first scan reads — which is why the order is stated here rather than left implicit in a
    /// literal array somewhere.
    /// </remarks>
    public static IReadOnlyList<string> PhotoFolders(DirectoryPlatform platform, Func<string, bool>? exists = null)
    {
        var probe = exists ?? Directory.Exists;
        return PhotoCandidates(platform)
            .Where(p => !string.IsNullOrEmpty(p) && probe(p))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    static IEnumerable<string> PhotoCandidates(DirectoryPlatform platform)
    {
        if (platform == DirectoryPlatform.Android)
        {
            // "Download" singular is the real folder name on Android (Environment.DIRECTORY_DOWNLOADS),
            // not the "Downloads" every desktop OS uses. Getting this wrong fails silently — the
            // candidate simply never exists and never appears.
            yield return Path.Combine(AndroidPrimaryStorage, "DCIM", "Camera");
            yield return Path.Combine(AndroidPrimaryStorage, "DCIM");
            yield return Path.Combine(AndroidPrimaryStorage, "Pictures");
            yield return Path.Combine(AndroidPrimaryStorage, "Download");
            yield break;
        }

        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home)) yield return Path.Combine(home, "Downloads");
    }

    /// <summary>
    /// Quick-jump buttons: the platform's <see cref="PhotoFolders"/> first, then the storage roots
    /// — every ready drive on Windows, home + filesystem root on macOS/Linux (there's no single
    /// "root" on Windows and no drive letters to enumerate elsewhere), primary storage on Android
    /// (see <see cref="AndroidPrimaryStorage"/>).
    /// </summary>
    /// <remarks>
    /// Photo folders lead because they are what someone opening this dialog is nearly always
    /// after; the roots stay because "somewhere else entirely" has to remain reachable in one tap.
    /// This is the half of the scan-target fix that is genuinely shared — Android's default start
    /// moved (see <see cref="DefaultStart"/>) and the desktop's deliberately did not, but both get
    /// the same shortcuts here.
    /// </remarks>
    /// <param name="platform">Which answer to give — see <see cref="DirectoryPlatform"/>.</param>
    /// <param name="windowsDrives">
    /// How to enumerate ready drives when <paramref name="platform"/> is
    /// <see cref="DirectoryPlatform.Windows"/>. Defaults to the real <see cref="DriveInfo"/>
    /// enumeration the dialog has always used; overridable so a test can pin the Windows branch
    /// deterministically without needing actual Windows drive letters to exist on the machine
    /// running the test (macOS has none).
    /// </param>
    /// <param name="exists">Folder-existence probe for the photo folders; see <see cref="PhotoFolders"/>.</param>
    public static IReadOnlyList<string> Roots(DirectoryPlatform platform,
                                              Func<IReadOnlyList<string>>? windowsDrives = null,
                                              Func<string, bool>? exists = null)
    {
        IReadOnlyList<string> storage = platform switch
        {
            DirectoryPlatform.Windows => (windowsDrives ?? RealWindowsDrives)(),
            DirectoryPlatform.Android => [AndroidPrimaryStorage],
            _ => [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "/"],
        };

        return PhotoFolders(platform, exists)
            .Concat(storage)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    static IReadOnlyList<string> RealWindowsDrives() =>
        DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName).ToList();
}
