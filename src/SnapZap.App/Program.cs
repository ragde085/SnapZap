using System.Runtime.InteropServices;
using SnapZap.App;
using SnapZap.Core.Platform;
using SnapZap.Core.Scanning;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<CatalogService>();

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

// Larger request-body limit is unnecessary; selections are id lists. Defaults are fine.

var app = builder.Build();

// Serve the SPA (added in step 5). For now the wwwroot placeholder confirms hosting works.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    os = RuntimeInformation.OSDescription,
    arch = RuntimeInformation.OSArchitecture.ToString(),
}));

// Run a scan, streaming progress as server-sent events; final event carries the summary.
app.MapGet("/api/scan", async (string folder, CatalogService catalog, HttpContext ctx, CancellationToken ct) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";

    async Task Send(string ev, object data)
    {
        await ctx.Response.WriteAsync($"event: {ev}\ndata: {System.Text.Json.JsonSerializer.Serialize(data, SnapZap.App.JsonCamel.Options)}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }

    var progress = new Progress<ScanProgress>();
    ScanProgress? last = null;
    progress.ProgressChanged += (_, p) => last = p; // throttled below

    try
    {
        // Poll the latest progress on a cadence rather than emitting every file.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pump = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                if (last is { } p) await Send("progress", p);
                try { await Task.Delay(150, cts.Token); } catch { break; }
            }
        }, cts.Token);

        var result = await catalog.ScanAsync(folder, progress, ct);
        cts.Cancel();
        try { await pump; } catch { }
        await Send("done", result);
    }
    catch (DirectoryNotFoundException)
    {
        await Send("error", new { message = $"Folder not found: {folder}" });
    }
});

// List analyzed images with a thumbnail URL keyed by content hash.
app.MapGet("/api/images", (CatalogService catalog) =>
{
    var items = catalog.Images.All().Select(i => new
    {
        i.Id, i.Path, i.ContentHash, i.FileSize, i.Width, i.Height, i.Format,
        i.NsfwScore, i.BlurScore, i.ExifTaken, i.ExifCamera,
        thumbUrl = $"/api/thumb/{i.ContentHash}",
    });
    return Results.Ok(items);
});

// Run duplicate detection: exact (always) + similar (czkawka if present).
app.MapPost("/api/dedup", async (string folder, CatalogService catalog, CancellationToken ct) =>
{
    var report = await new SnapZap.Core.Dedup.DuplicateService(catalog.Db).DetectAsync(folder, ct);
    return Results.Ok(report);
});

// Score all un-scored images for NSFW (streams progress; graceful if model missing).
app.MapGet("/api/nsfw-scan", async (CatalogService catalog, HttpContext ctx, CancellationToken ct) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    async Task Send(string ev, object data)
    {
        await ctx.Response.WriteAsync($"event: {ev}\ndata: {System.Text.Json.JsonSerializer.Serialize(data, SnapZap.App.JsonCamel.Options)}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }

    var scorer = new SnapZap.Core.Nsfw.NsfwScorer(catalog.Db, catalog.NsfwModelPath);
    var progress = new Progress<SnapZap.Core.Nsfw.NsfwProgress>();
    SnapZap.Core.Nsfw.NsfwProgress? last = null;
    progress.ProgressChanged += (_, p) => last = p;

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    var pump = Task.Run(async () =>
    {
        while (!cts.IsCancellationRequested)
        {
            if (last is { } p) await Send("progress", p);
            try { await Task.Delay(200, cts.Token); } catch { break; }
        }
    }, cts.Token);

    var result = await scorer.ScoreAllAsync(progress, ct: ct);
    cts.Cancel();
    try { await pump; } catch { }
    await Send("done", result);
});

// List duplicate groups (exact + similar) with member image ids and keeper flags.
app.MapGet("/api/dupe-groups", (CatalogService catalog) =>
{
    var groups = new SnapZap.Core.Data.DupeRepository(catalog.Db).Groups()
        .Select(g => new
        {
            g.Id,
            kind = g.Kind.ToString().ToLowerInvariant(),
            g.Similarity,
            members = g.Members.Select(m => new { m.ImageId, m.IsKeeper }),
        });
    return Results.Ok(groups);
});

// Pre-flight an export: counts, size, free space, hardlink availability — no bytes moved.
app.MapPost("/api/export/preflight", (ExportRequestDto dto, CatalogService catalog,
    ILinkService links, ITrashService trash) =>
{
    var req = dto.ToRequest(catalog.LastScannedFolder ?? "");
    var engine = new SnapZap.Core.Export.ExportEngine(catalog.Db, links, trash);
    return Results.Ok(engine.Plan(req));
});

// Run an export, streaming progress; writes a manifest and returns the result + manifest paths.
app.MapPost("/api/export/run", async (ExportRequestDto dto, CatalogService catalog,
    ILinkService links, ITrashService trash, HttpContext ctx, CancellationToken ct) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    async Task Send(string ev, object data)
    {
        await ctx.Response.WriteAsync($"event: {ev}\ndata: {System.Text.Json.JsonSerializer.Serialize(data, SnapZap.App.JsonCamel.Options)}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }

    var req = dto.ToRequest(catalog.LastScannedFolder ?? "");
    var engine = new SnapZap.Core.Export.ExportEngine(catalog.Db, links, trash);
    var progress = new Progress<SnapZap.Core.Export.ExportProgress>();
    SnapZap.Core.Export.ExportProgress? last = null;
    progress.ProgressChanged += (_, p) => last = p;

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    var pump = Task.Run(async () =>
    {
        while (!cts.IsCancellationRequested)
        {
            if (last is { } p) await Send("progress", p);
            try { await Task.Delay(150, cts.Token); } catch { break; }
        }
    }, cts.Token);

    try
    {
        var result = await engine.RunAsync(req, progress, ct);
        cts.Cancel(); try { await pump; } catch { }
        var (csv, json) = SnapZap.Core.Export.ManifestWriter.Write(catalog.Db, result, catalog.ManifestDir);
        await Send("done", new { result, manifest = new { csv, json } });
    }
    catch (Exception ex)
    {
        cts.Cancel();
        await Send("error", new { message = ex.Message });
    }
});

// In-place delete: recycle the selected images (reversible), logging an undo batch.
app.MapPost("/api/delete", async (DeleteDto dto, CatalogService catalog, ITrashService trash, CancellationToken ct) =>
{
    var svc = new SnapZap.Core.Delete.DeleteService(catalog.Db, trash);
    var result = await svc.RecycleAsync(dto.ImageIds, ct);
    return Results.Ok(result);
});

// List undo batches (delete + export-recycle) for the undo panel.
app.MapGet("/api/undo/batches", (CatalogService catalog, ITrashService trash) =>
    Results.Ok(new SnapZap.Core.Delete.DeleteService(catalog.Db, trash).Batches()));

// Restore a batch from the trash back to original paths.
app.MapPost("/api/undo/{batchId}", async (string batchId, CatalogService catalog, ITrashService trash, CancellationToken ct) =>
{
    var result = await new SnapZap.Core.Delete.DeleteService(catalog.Db, trash).RestoreAsync(batchId, ct);
    return Results.Ok(result);
});

// Serve a cached thumbnail by hash (paths kept server-side, never exposed as file:// URLs).
app.MapGet("/api/thumb/{hash}", (string hash, CatalogService catalog) =>
{
    if (hash.Length < 2 || !hash.All(Uri.IsHexDigit)) return Results.BadRequest();
    var path = Path.Combine(catalog.ThumbDir, hash[..2], hash + ".jpg");
    return File.Exists(path) ? Results.File(path, "image/jpeg") : Results.NotFound();
});

// Double-clickable UX: once the server is listening, open the SPA in the default browser.
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
