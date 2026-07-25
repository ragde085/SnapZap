using System.Runtime.InteropServices;
using SnapZap.App;
using SnapZap.App.Services;
using SnapZap.Core.Data;
using SnapZap.Core.Platform;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<CatalogService>();
builder.Services.AddSingleton<DependencyChecker>();
builder.Services.AddScoped<AppState>();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// Platform services (DESIGN.md §9): resolve the OS-specific concerns behind interfaces.
// Windows implementations replace these at publish time; on the dev Mac we bind the
// macOS versions so the export/delete flows are exercisable locally.
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    builder.Services.AddSingleton<ITrashService, WindowsTrashService>();
    builder.Services.AddSingleton<ILinkService, WindowsLinkService>();
}
else
{
    builder.Services.AddSingleton<ITrashService, MacOsTrashService>();
    builder.Services.AddSingleton<ILinkService, MacOsLinkService>();
}

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    os = RuntimeInformation.OSDescription,
    arch = RuntimeInformation.OSArchitecture.ToString(),
}));

// Serve a cached thumbnail by hash (paths kept server-side, never exposed as file:// URLs).
app.MapGet("/api/thumb/{hash}", (string hash, CatalogService catalog) =>
{
    if (hash.Length < 2 || !hash.All(Uri.IsHexDigit)) return Results.BadRequest();
    var path = Path.Combine(catalog.ThumbDir, hash[..2], hash + ".jpg");
    return File.Exists(path) ? Results.File(path, "image/jpeg") : Results.NotFound();
});

// Full-resolution preview by image id. Guard: only ever serve a path present in the catalog —
// never an arbitrary path supplied by the client.
app.MapGet("/api/full/{id:long}", (long id, CatalogService catalog) =>
{
    var img = new ImageRepository(catalog.Db).ByIds([id]).FirstOrDefault();
    if (img is null || !File.Exists(img.Path)) return Results.NotFound();
    var contentType = Path.GetExtension(img.Path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        _ => "application/octet-stream",
    };
    return Results.File(img.Path, contentType);
});

app.MapRazorComponents<SnapZap.App.Components.App>().AddInteractiveServerRenderMode();

// Double-clickable UX: once the server is listening, open the app in the default browser.
// Suppressed by PC_NO_BROWSER (used by automated tests) and when a debugger/dev URL is set.
if (Environment.GetEnvironmentVariable("PC_NO_BROWSER") is null)
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var url = app.Urls.FirstOrDefault() ?? "http://localhost:5099";
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true };
            System.Diagnostics.Process.Start(psi);
        }
        catch { /* headless or no default browser — the URL is printed to the console anyway */ }
    });
}

app.Run();

// Exposed so the test project can reference the host entry point later.
public partial class Program;
