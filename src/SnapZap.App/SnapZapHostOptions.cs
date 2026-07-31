namespace SnapZap.App;

/// <summary>
/// Everything a caller of <see cref="SnapZapWebHost.Build"/> must supply that the shared
/// composition can't decide for itself — today, that's just the process args and which
/// platform-service implementations (DESIGN.md §9's <c>ITrashService</c>/<c>ILinkService</c>
/// seam) to register.
///
/// <see cref="SnapZapWebHost"/> is deliberately agnostic about *which* platform this is: the
/// desktop entry point (<c>Program.cs</c>) supplies the same Windows-or-macOS branch that used
/// to be inline in <c>WebApplication</c> construction, and a future Android host would supply
/// its own <see cref="ConfigurePlatformServices"/> instead — the shared method never needs to
/// know Android exists.
/// </summary>
public sealed class SnapZapHostOptions
{
    /// <summary>
    /// Process command-line arguments, passed straight through to
    /// <c>WebApplication.CreateBuilder</c>.
    /// </summary>
    public required string[] Args { get; init; }

    /// <summary>
    /// Registers this platform's <c>ITrashService</c>/<c>ILinkService</c> against the host's
    /// service collection. Called after the app's own services are registered but before
    /// <c>WebApplicationBuilder.Build()</c>, so it can add singletons exactly like the inline
    /// <c>if</c>/<c>else</c> this replaced used to.
    /// </summary>
    public required Action<IServiceCollection> ConfigurePlatformServices { get; init; }
}
