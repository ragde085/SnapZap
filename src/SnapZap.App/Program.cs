using System.Runtime.InteropServices;
using SnapZap.App;
using SnapZap.App.Services;
using SnapZap.Core.Platform;

var options = new SnapZapHostOptions
{
    Args = args,

    // Platform services (DESIGN.md §9): resolve the OS-specific concerns behind interfaces.
    // Windows implementations replace these at publish time; on the dev Mac we bind the
    // macOS versions so the export/delete flows are exercisable locally.
    //
    // This is the desktop entry point's choice, not SnapZapWebHost.Build's — a future Android
    // host supplies its own AndroidTrashService/AndroidLinkService here instead, and the shared
    // builder never needs to know Android exists.
    ConfigurePlatformServices = services =>
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            services.AddSingleton<ITrashService, WindowsTrashService>();
            services.AddSingleton<ILinkService, WindowsLinkService>();
        }
        else
        {
            services.AddSingleton<ITrashService, MacOsTrashService>();
            services.AddSingleton<ILinkService, MacOsLinkService>();
        }
    },
};

var app = SnapZapWebHost.Build(options);

// PC_NO_BROWSER=1 runs the server and nothing else: no window, no browser, no foreground host.
// This is the mode the automated tests drive.
if (Environment.GetEnvironmentVariable("PC_NO_BROWSER") is not null)
{
    app.Run();
    return;
}

// Double-clickable UX. Start the server first so the host can be handed the URL it actually
// bound to, then give the foreground to the host for the rest of the session: an embedded
// window on Windows, the default browser elsewhere. When it returns, the user is done.
try
{
    app.StartAsync().GetAwaiter().GetResult();

    var url = app.Urls.FirstOrDefault()
        ?? throw new InvalidOperationException("The server started without binding an address.");

    AppHostFactory.Create(app.Lifetime, app.Services.GetRequiredService<ILoggerFactory>()).Run(url);

    app.StopAsync().GetAwaiter().GetResult();
}
catch (Exception ex)
{
    // A windowed build has no console to fail into, so an unhandled startup exception would
    // otherwise be a process that exits with nothing shown at all.
    NativeDialog.Error($"SnapZap could not start.\n\n{ex.Message}");
    throw;
}

// Exposed so the test project can reference the host entry point later.
public partial class Program;
