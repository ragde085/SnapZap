namespace SnapZap.Core.Platform;

/// <summary>
/// <see cref="ILinkService"/> for platforms with no hardlink support to offer — Android's
/// scoped-storage model chief among them. No SDK-specific type is needed to implement this one
/// (unlike <see cref="ITrashService"/>'s portable counterpart, there's no "supply a directory"
/// step at all — this stub simply says no), so unlike <see cref="Delete.FolderTrashService"/> it
/// isn't even parameterised; it's registered once for any host that has nothing better to offer.
/// </summary>
public sealed class PortableLinkStub : ILinkService
{
    /// <summary>Always false. <see cref="Export.ExportEngine"/> reads this to decide whether
    /// hardlink mode is even offered, and falls back to <see cref="Export.TransferMode.Copy"/>
    /// mid-run if a hardlink was requested anyway (see ExportEngine.RunAsync's mode-downgrade
    /// check) — so returning false here is what makes hardlink export silently degrade to a
    /// slower-but-working copy instead of the caller ever reaching
    /// <see cref="CreateHardLink"/>. v1 of the Android port doesn't expose export at all
    /// (docs/ANDROID-PORT-PLAN.md §1), so this path isn't exercised yet — but the fallback has to
    /// hold the moment it is, since a stub that throws where <see cref="ExportEngine"/> expects a
    /// boolean would crash the preflight check, not just the transfer.</summary>
    public bool SameVolume(string a, string b) => false;

    /// <summary>Never reached in practice — <see cref="SameVolume"/> always being false means
    /// <see cref="Export.ExportEngine"/> never calls this. Implemented anyway, and as a throw
    /// rather than a silent no-op, so a future caller that skips the <see cref="SameVolume"/>
    /// check (the interface's own contract requires checking it first) fails loudly instead of
    /// writing nothing and reporting success.</summary>
    public void CreateHardLink(string linkPath, string targetPath) =>
        throw new NotSupportedException($"Hardlinks are not supported: cannot link {linkPath} -> {targetPath}.");
}
