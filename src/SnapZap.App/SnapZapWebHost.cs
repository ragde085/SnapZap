using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Localization;
using SnapZap.App.Services;
using SnapZap.Core.Data;

namespace SnapZap.App;

/// <summary>
/// Builds the <see cref="WebApplication"/> that is "SnapZap": DI registration, localization,
/// static files, antiforgery, the three minimal API endpoints, and Razor Components — everything
/// that is the same regardless of what shows the resulting URL to the user.
///
/// This is intentionally the *only* thing in here. Picking a window host (Photino, a browser
/// tab, one day an Android <c>WebView</c>) and running it is a platform concern and stays out of
/// <see cref="Build"/> entirely (see <c>Program.cs</c>'s tail and
/// <c>Services/AppHost.cs</c>) — so that a future non-desktop head can call this method and get
/// the exact same app without dragging in Photino, Win32 P/Invoke, or a console window to hide.
/// </summary>
public static class SnapZapWebHost
{
    /// <summary>
    /// Composes and builds the <see cref="WebApplication"/>. Does not start it — the caller
    /// decides when (desktop starts it explicitly so it can hand the bound URL to a window host
    /// before giving up the foreground; a simple headless caller can just call
    /// <c>WebApplication.Run()</c>).
    /// </summary>
    public static WebApplication Build(SnapZapHostOptions options)
    {
        // Anchor the content root to the executable, not to whatever directory SnapZap happened to be
        // launched from.
        //
        // WebApplication.CreateBuilder defaults ContentRootPath to Directory.GetCurrentDirectory(), a
        // sound default for a server started by a script from its own directory and a trap for a
        // desktop app. Every static file — app.css, interop.js, blazor.web.js — is resolved under
        // <content root>/wwwroot, so launching SnapZap with any other working directory (from a
        // terminal, a shortcut with a different "Start in", a scheduler, another program) served 404
        // for all of them. The app still started, still returned 200 for the page itself, and rendered
        // prerendered markup with no styling and no interactivity. Double-clicking in Explorer happens
        // to set the working directory to the executable's folder, which is why this hid so well.
        //
        // Only overridden when wwwroot actually sits beside the binary — that is what a published
        // layout looks like. In development it does not: wwwroot stays in the project directory and is
        // served through the static-web-assets manifest, which the default content root is right for.
        var baseDir = AppContext.BaseDirectory;
        var publishedLayout = Directory.Exists(Path.Combine(baseDir, "wwwroot"));

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = options.Args,
            ContentRootPath = publishedLayout ? baseDir : null,
        });

        // Bind an ephemeral loopback port unless the environment asked for something specific.
        //
        // Kestrel's default is 5000, which on Windows is contested enough that a second SnapZap — or
        // any other dev server the user happens to run — took the app down at startup with an
        // AddressInUseException. Double-clicked, that is a window that flashes and disappears. Port 0
        // asks the OS for a free one and cannot collide; the URL it actually got is read back off
        // app.Urls after the server starts, so nothing downstream may assume a number.
        if (builder.Configuration["urls"] is null &&
            Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null)
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
        }

        builder.Services.AddSingleton<CatalogService>();
        builder.Services.AddSingleton<DependencyChecker>();
        builder.Services.AddSingleton<SessionStore>();
        builder.Services.AddScoped<AppState>();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        // No ResourcesPath option: every marker type already lives in the SnapZap.App.Resources
        // namespace, which matches the physical Resources/ folder 1:1. Setting ResourcesPath here too
        // double-prefixes the resource base name (SnapZap.App.Resources.Resources.X) and every lookup
        // silently falls back to the raw key — verified via IStringLocalizer's own ResourceNotFound flag.
        builder.Services.AddLocalization();

        // Platform services (DESIGN.md §9): resolve the OS-specific concerns behind interfaces.
        // Which implementations that means is entirely the caller's call — see
        // SnapZapHostOptions.ConfigurePlatformServices and, for the desktop entry point, the
        // Windows/macOS branch in Program.cs that replaces this comment's old home.
        options.ConfigurePlatformServices(builder.Services);

        var app = builder.Build();

        // Culture comes from settings.json (DependencyChecker.Language), not a cookie or
        // Accept-Language — see SettingsRequestCultureProvider. Must run before anything renders, so it
        // sits ahead of static files/antiforgery/routing like every other request-shaping middleware here.
        var localizationOptions = new RequestLocalizationOptions()
            .SetDefaultCulture(DependencyChecker.SupportedLanguages[0])
            .AddSupportedCultures([.. DependencyChecker.SupportedLanguages])
            .AddSupportedUICultures([.. DependencyChecker.SupportedLanguages]);
        localizationOptions.RequestCultureProviders = [new SettingsRequestCultureProvider()];
        app.UseRequestLocalization(localizationOptions);

        app.UseStaticFiles();
        app.UseAntiforgery();

        app.MapGet("/api/health", () => Results.Ok(new
        {
            status = "ok",
            os = RuntimeInformation.OSDescription,
            arch = RuntimeInformation.OSArchitecture.ToString(),
        }));

        // Serve a cached thumbnail by hash (paths kept server-side, never exposed as file:// URLs).
        app.MapGet("/api/thumb/{hash}", (string hash, CatalogService catalog, HttpContext ctx) =>
        {
            if (hash.Length < 2 || !hash.All(Uri.IsHexDigit)) return Results.BadRequest();
            var path = Path.Combine(catalog.ThumbDir, hash[..2], hash + ".jpg");
            if (!File.Exists(path)) return Results.NotFound();

            // Content-addressed by hash: different bytes mean a different URL, so a year-long immutable
            // cache can never point a client at stale content. The recipe-version query parameter
            // (ImageView.ThumbUrl) is what busts the cache when a thumbnail is regenerated in place —
            // without it a re-oriented thumbnail would sit behind this header forever (Task 16).
            ctx.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            return Results.File(path, "image/jpeg");
        });

        // Full-resolution preview by image id. Guard: only ever serve a path present in the catalog —
        // never an arbitrary path supplied by the client.
        //
        // Serves a cached ~1600px preview rather than the original: a 24 MP original is ~20 MB over the
        // wire plus a full-resolution browser decode for what is just a modal (Task 26). The preview is
        // generated and cached lazily on first request, keyed by content hash like the thumbnail cache,
        // and falls back to the original file when one cannot be produced (corrupt/unsupported source —
        // the same file that made the grid's thumbnail generation best-effort).
        app.MapGet("/api/full/{id:long}", async (long id, CatalogService catalog) =>
        {
            var img = new ImageRepository(catalog.Db).ByIds([id]).FirstOrDefault();
            if (img is null || !File.Exists(img.Path)) return Results.NotFound();

            var previewPath = Path.Combine(catalog.PreviewDir, img.ContentHash[..2], img.ContentHash + ".jpg");
            if (File.Exists(previewPath)) return Results.File(previewPath, "image/jpeg");

            var generated = await Task.Run(() => catalog.Imaging.WritePreview(img.Path, previewPath));
            if (generated) return Results.File(previewPath, "image/jpeg");

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

        return app;
    }
}
