using SnapZap.Core.Platform;
using Xunit;

namespace SnapZap.Tests;

public class LinkServiceTests
{
    // These run on the dev Mac; the equivalent Windows behaviour is verified on hardware
    // per DESIGN.md §9. The point here is that the SameVolume gate is sane.
    static readonly bool OnMac = OperatingSystem.IsMacOS();

    [SkippableFact]
    public void SameVolume_true_for_two_paths_in_temp()
    {
        Skip.IfNot(OnMac, "macOS-only dev implementation");
        var svc = new MacOsLinkService();
        var a = Path.Combine(Path.GetTempPath(), "pc_a.tmp");
        var b = Path.Combine(Path.GetTempPath(), "pc_b_does_not_exist_yet.tmp");
        File.WriteAllText(a, "x");
        try
        {
            Assert.True(svc.SameVolume(a, b), "two temp paths should share a volume");
        }
        finally { File.Delete(a); }
    }

    [SkippableFact]
    public void CreateHardLink_makes_a_second_path_to_same_bytes()
    {
        Skip.IfNot(OnMac, "macOS-only dev implementation");
        var svc = new MacOsLinkService();
        var target = Path.Combine(Path.GetTempPath(), $"pc_target_{Guid.NewGuid():N}.tmp");
        var linkPath = Path.Combine(Path.GetTempPath(), $"pc_link_{Guid.NewGuid():N}.tmp");
        File.WriteAllText(target, "hello");
        try
        {
            svc.CreateHardLink(linkPath, target);
            Assert.Equal("hello", File.ReadAllText(linkPath));
            // Editing through one link is visible through the other — same inode.
            File.WriteAllText(target, "changed");
            Assert.Equal("changed", File.ReadAllText(linkPath));
        }
        finally
        {
            File.Delete(linkPath);
            File.Delete(target);
        }
    }
}
