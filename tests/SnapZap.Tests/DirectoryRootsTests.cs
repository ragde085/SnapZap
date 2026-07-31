using SnapZap.Core.Platform;
using Xunit;

namespace SnapZap.Tests;

/// <summary>
/// Pins DirectoryPickerDialog.razor's "where does the picker start, what roots does it offer"
/// behaviour (extracted to SnapZap.Core.Platform.DirectoryRoots per docs/ANDROID-PORT-PLAN.md
/// §3 item 5) for all three platforms it now answers for, including Android — which nothing on
/// this dev machine can actually run (OperatingSystem.IsAndroid() is always false here), but
/// DirectoryRoots takes the platform as a value so the Android answer is reachable anyway.
///
/// The same argument now applies to folder existence: <c>/storage/emulated/0/DCIM/Camera</c> never
/// exists on a Mac, so every Android answer that depends on it would silently collapse to the
/// fallback branch and pin nothing. The <c>exists</c> probe is the seam that makes both branches
/// reachable — tests that omit it are asserting the fallback on purpose.
/// </summary>
public class DirectoryRootsTests
{
    /// <summary>A probe that says yes to exactly these paths and no to everything else.</summary>
    static Func<string, bool> Only(params string[] present) =>
        p => present.Contains(p, StringComparer.Ordinal);

    static readonly Func<string, bool> NothingExists = _ => false;

    // --- Windows: same answers DirectoryPickerDialog.razor gave before the extraction ---

    [Fact]
    public void Windows_default_start_is_the_user_profile_folder()
    {
        Assert.Equal(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            DirectoryRoots.DefaultStart(DirectoryPlatform.Windows));
    }

    [Fact]
    public void Windows_roots_are_exactly_the_ready_drives_the_platform_reports()
    {
        // The real DriveInfo.GetDrives() enumeration can't be pinned deterministically from a
        // macOS dev box (no Windows drive letters exist here), so the drive source is
        // injectable. This test pins what matters: on the Windows branch, DirectoryRoots
        // defers entirely to that source and passes its answer through unmodified — the same
        // behaviour DirectoryPickerDialog.razor's inline
        // `DriveInfo.GetDrives().Where(d => d.IsReady).Select(...)` had before extraction.
        //
        // Photo folders are suppressed here rather than accounted for: this test is about the
        // drive enumeration, and the shortcuts have their own tests below.
        IReadOnlyList<string> fakeDrives = ["C:\\", "D:\\"];
        var roots = DirectoryRoots.Roots(DirectoryPlatform.Windows, () => fakeDrives, NothingExists);
        Assert.Equal(fakeDrives, roots);
    }

    [Fact]
    public void Windows_roots_use_the_real_drive_enumeration_when_no_override_is_supplied()
    {
        // No override passed — this exercises the exact code path DirectoryPickerDialog.razor
        // uses in production. On this (non-Windows) dev machine the call must still succeed
        // and simply return no drives, rather than throwing.
        var roots = DirectoryRoots.Roots(DirectoryPlatform.Windows);
        Assert.NotNull(roots);
    }

    // --- macOS / Linux ("Other"): same answers DirectoryPickerDialog.razor gave before ---

    [Fact]
    public void Other_default_start_is_the_user_profile_folder()
    {
        Assert.Equal(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            DirectoryRoots.DefaultStart(DirectoryPlatform.Other));
    }

    [Fact]
    public void Other_roots_are_home_and_filesystem_root()
    {
        var roots = DirectoryRoots.Roots(DirectoryPlatform.Other, exists: NothingExists);
        Assert.Equal(
            [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "/"],
            roots);
    }

    // --- Photo-folder shortcuts: shared by both heads ---

    [Fact]
    public void Photo_folders_lead_the_roots_and_the_storage_roots_still_follow()
    {
        // Order is the contract: someone opening this dialog is nearly always after photos, but
        // "somewhere else entirely" has to stay one tap away.
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        var roots = DirectoryRoots.Roots(DirectoryPlatform.Other, exists: Only(pictures));

        Assert.Equal(pictures, roots[0]);
        Assert.Contains(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), roots);
        Assert.Contains("/", roots);
    }

    [Fact]
    public void A_photo_folder_that_does_not_exist_is_not_offered()
    {
        Assert.Empty(DirectoryRoots.PhotoFolders(DirectoryPlatform.Android, NothingExists));
        Assert.Empty(DirectoryRoots.PhotoFolders(DirectoryPlatform.Other, NothingExists));
    }

    [Fact]
    public void Android_photo_folders_are_offered_camera_first()
    {
        // Descending confidence that what is in there came from this phone's camera. DefaultStart
        // takes the first, so this ordering decides what a first scan reads.
        var all = DirectoryRoots.PhotoFolders(DirectoryPlatform.Android, Only(
            "/storage/emulated/0/Pictures",
            "/storage/emulated/0/DCIM",
            "/storage/emulated/0/DCIM/Camera",
            "/storage/emulated/0/Download"));

        Assert.Equal(
            ["/storage/emulated/0/DCIM/Camera", "/storage/emulated/0/DCIM",
             "/storage/emulated/0/Pictures", "/storage/emulated/0/Download"],
            all);
    }

    // --- Android: new branch, AC-5.2 ---

    [Fact]
    public void Android_default_start_is_the_camera_folder_when_it_exists()
    {
        // The fix for the scan-target finding: internal storage as the pre-filled default made
        // "scan every file on this phone" — Android/data, caches, messaging-app media — the path
        // of least resistance on first run.
        Assert.Equal(
            "/storage/emulated/0/DCIM/Camera",
            DirectoryRoots.DefaultStart(DirectoryPlatform.Android, Only("/storage/emulated/0/DCIM/Camera")));
    }

    [Fact]
    public void Android_default_start_falls_back_through_DCIM_then_Pictures()
    {
        Assert.Equal(
            "/storage/emulated/0/DCIM",
            DirectoryRoots.DefaultStart(DirectoryPlatform.Android, Only("/storage/emulated/0/DCIM")));

        Assert.Equal(
            "/storage/emulated/0/Pictures",
            DirectoryRoots.DefaultStart(DirectoryPlatform.Android, Only("/storage/emulated/0/Pictures")));
    }

    [Fact]
    public void Android_default_start_is_primary_shared_storage_when_no_photo_folder_exists()
    {
        // A phone with none of the well-known folders still has to land somewhere it can browse
        // from, and scanning everything remains available — as a choice, not as the default.
        Assert.Equal("/storage/emulated/0",
            DirectoryRoots.DefaultStart(DirectoryPlatform.Android, NothingExists));
        Assert.Equal(DirectoryRoots.AndroidPrimaryStorage,
            DirectoryRoots.DefaultStart(DirectoryPlatform.Android, NothingExists));
    }

    [Fact]
    public void Android_roots_are_exactly_the_single_primary_storage_root()
    {
        // v1 has no removable-volume/SD-card enumeration (docs/ANDROID-PORT-PLAN.md §3 item 5)
        // — the primary storage mount is the sole storage root, not one of several.
        var roots = DirectoryRoots.Roots(DirectoryPlatform.Android, exists: NothingExists);
        Assert.Equal([DirectoryRoots.AndroidPrimaryStorage], roots);
    }

    [Fact]
    public void Android_roots_put_the_camera_folder_first_and_keep_primary_storage_reachable()
    {
        var roots = DirectoryRoots.Roots(DirectoryPlatform.Android,
            exists: Only("/storage/emulated/0/DCIM/Camera"));

        Assert.Equal("/storage/emulated/0/DCIM/Camera", roots[0]);
        Assert.Contains(DirectoryRoots.AndroidPrimaryStorage, roots);
    }

    // --- CurrentPlatform: sanity check on whatever OS actually runs the test suite ---

    [Fact]
    public void CurrentPlatform_matches_this_test_runner()
    {
        // These tests run on macOS/Linux dev machines and CI, never Windows or Android.
        Assert.Equal(
            OperatingSystem.IsWindows() ? DirectoryPlatform.Windows : DirectoryPlatform.Other,
            DirectoryRoots.CurrentPlatform);
    }
}
