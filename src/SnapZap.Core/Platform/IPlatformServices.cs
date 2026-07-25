namespace SnapZap.Core.Platform;

/// <summary>
/// The four platform-specific concerns from DESIGN.md §9. Everything else in Core is
/// portable. Windows implementations are the shipping target; the macOS implementations
/// exist so ~90% of the app is developable and testable on the dev machine.
/// </summary>

/// <summary>Reversible deletion. Never a hard delete.</summary>
public interface ITrashService
{
    /// <summary>Send a file to the OS trash / recycle bin. Returns the location it landed
    /// in when the platform can report one (used by the undo log to restore), else null.</summary>
    Task<string?> SendToTrashAsync(string path, CancellationToken ct = default);

    /// <summary>Restore a previously-trashed file back to <paramref name="originalPath"/>.
    /// <paramref name="trashedLocation"/> is what SendToTrashAsync returned (may be null on
    /// platforms that couldn't report it, in which case the impl does its best). Returns
    /// true if the file is back at its original path.</summary>
    Task<bool> RestoreAsync(string originalPath, string? trashedLocation, CancellationToken ct = default);
}

/// <summary>Hard-link creation, used by the zero-copy export mode.</summary>
public interface ILinkService
{
    /// <summary>True when both paths resolve to the same volume, so a hard link is possible.
    /// The export engine uses this to auto-offer hardlink mode and to fall back to copy.</summary>
    bool SameVolume(string a, string b);

    /// <summary>Create a hard link at <paramref name="linkPath"/> pointing at
    /// <paramref name="targetPath"/>. Caller must have checked <see cref="SameVolume"/>.</summary>
    void CreateHardLink(string linkPath, string targetPath);
}

public enum InferenceBackend { DirectML, CoreML, Cpu }

/// <summary>Selects the ONNX Runtime execution provider appropriate to the platform.</summary>
public interface IInferenceProvider
{
    InferenceBackend Backend { get; }

    /// <summary>Session options configured with the platform's best available EP.
    /// Returned as object so Core does not take a compile-time dependency on the ORT types
    /// until the inference step is actually built.</summary>
    object CreateSessionOptions();
}

/// <summary>Hosts the SPA — an embedded native window on the target, a plain browser tab in dev.</summary>
public interface IAppHost
{
    void Run(string url);
}
