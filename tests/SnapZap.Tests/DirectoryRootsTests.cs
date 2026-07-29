using SnapZap.Core.Platform;
using Xunit;

namespace SnapZap.Tests;

/// <summary>
/// Pins DirectoryPickerDialog.razor's "where does the picker start, what roots does it offer"
/// behaviour (extracted to SnapZap.Core.Platform.DirectoryRoots per docs/ANDROID-PORT-PLAN.md
/// §3 item 5) for all three platforms it now answers for, including Android — which nothing on
/// this dev machine can actually run (OperatingSystem.IsAndroid() is always false here), but
/// DirectoryRoots takes the platform as a value so the Android answer is reachable anyway.
/// </summary>
public class DirectoryRootsTests
{
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
        IReadOnlyList<string> fakeDrives = ["C:\\", "D:\\"];
        var roots = DirectoryRoots.Roots(DirectoryPlatform.Windows, () => fakeDrives);
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
        var roots = DirectoryRoots.Roots(DirectoryPlatform.Other);
        Assert.Equal(
            [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "/"],
            roots);
    }

    // --- Android: new branch, AC-5.2 ---

    [Fact]
    public void Android_default_start_is_primary_shared_storage()
    {
        Assert.Equal("/storage/emulated/0", DirectoryRoots.DefaultStart(DirectoryPlatform.Android));
        Assert.Equal(DirectoryRoots.AndroidPrimaryStorage, DirectoryRoots.DefaultStart(DirectoryPlatform.Android));
    }

    [Fact]
    public void Android_roots_are_exactly_the_single_primary_storage_root()
    {
        // v1 has no removable-volume/SD-card enumeration (docs/ANDROID-PORT-PLAN.md §3 item 5)
        // — the primary storage mount is the sole root, not one of several.
        var roots = DirectoryRoots.Roots(DirectoryPlatform.Android);
        Assert.Equal([DirectoryRoots.AndroidPrimaryStorage], roots);
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
