using SnapZap.Core.Data;
using SnapZap.Core.Dedup;
using SnapZap.Core.Imaging;
using SnapZap.Core.Scanning;
using Xunit;

namespace SnapZap.Tests;

/// <summary>
/// Baseline measurements for the "CPU Performance Optimisation" tech-spec (Task 1), against which
/// the AC 14-20 performance floors are supposed to be judged.
/// </summary>
/// <remarks>
/// <b>These numbers are not the spec's baseline.</b> The spec calls for measurement on a Windows
/// x64 test machine against a real library of at least 20,000 mixed-format photos and a real
/// <c>catalog.db</c> copy. This is a macOS dev sandbox with neither available, so this class
/// exercises the same code paths and reports the same metrics against a small synthetic fixture
/// set instead. Treat the numbers this prints as smoke-test sanity (the pipeline still runs, still
/// allocates roughly what's expected) — not as the AC 14-20 reference figures. Those require the
/// Windows run the spec asks for and remain outstanding; see Task 28.
///
/// Skipped by default, following the <c>NsfwModelValidation</c> precedent for measurements that
/// aren't correctness checks:
///
///   PC_RUN_PERF_BASELINE=1 dotnet test --filter Category=Perf
/// </remarks>
[Trait("Category", "Perf")]
public class PerfBaselineTests : IDisposable
{
    static bool RunPerf => Environment.GetEnvironmentVariable("PC_RUN_PERF_BASELINE") == "1";
    const string SkipReason =
        "Set PC_RUN_PERF_BASELINE=1 to run. Even then, this is a synthetic fixture, not the " +
        "Windows/20k-photo baseline the tech-spec's performance ACs require.";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "snapzap_perf_" + Guid.NewGuid().ToString("N"));
    readonly string _appData;

    public PerfBaselineTests()
    {
        Directory.CreateDirectory(_dir);
        _appData = Path.Combine(_dir, "appdata");
        Directory.CreateDirectory(_appData);
        for (int i = 0; i < 40; i++)
        {
            using var bmp = GoldenValueTests.Scene(800 + i, 600 + (i % 5) * 10);
            using var image = SkiaSharp.SKImage.FromBitmap(bmp);
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 90);
            using var fs = File.Create(Path.Combine(_dir, $"photo_{i:D3}.jpg"));
            data.SaveTo(fs);
        }
    }

    [SkippableFact]
    public async Task Fresh_scan_cpu_and_allocation_smoke_baseline()
    {
        Skip.If(!RunPerf, SkipReason);

        using var db = new Database(Path.Combine(_appData, "catalog.db"));
        var imaging = new SkiaImageService();
        var scanner = new Scanner(db, imaging, Path.Combine(_appData, "thumbs"));

        var before = GC.GetTotalAllocatedBytes(precise: true);
        var start = Environment.TickCount64;
        var result = await scanner.ScanAsync(_dir);
        var elapsedMs = Environment.TickCount64 - start;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.Equal(40, result.Analyzed);
        Console.WriteLine(
            $"[PerfBaseline] fresh scan: {result.Analyzed} images, {elapsedMs} ms wall, " +
            $"{elapsedMs / (double)result.Analyzed:F2} ms/image, {allocated:N0} bytes allocated " +
            $"({allocated / (double)result.Analyzed:N0} bytes/image) — synthetic fixture, NOT the Windows/20k-photo baseline.");
    }

    [SkippableFact]
    public async Task Cached_rescan_wall_clock_baseline()
    {
        Skip.If(!RunPerf, SkipReason);

        using var db = new Database(Path.Combine(_appData, "catalog.db"));
        var imaging = new SkiaImageService();
        var scanner = new Scanner(db, imaging, Path.Combine(_appData, "thumbs"));
        await scanner.ScanAsync(_dir); // warm the cache

        var start = Environment.TickCount64;
        var result = await scanner.ScanAsync(_dir);
        var elapsedMs = Environment.TickCount64 - start;

        Assert.Equal(40, result.Cached);
        Console.WriteLine(
            $"[PerfBaseline] cached re-scan: {result.Cached} images, {elapsedMs} ms wall — " +
            "synthetic fixture, NOT the Windows/20k-photo baseline.");
    }

    [SkippableFact]
    public async Task Duplicate_detection_elapsed_baseline()
    {
        Skip.If(!RunPerf, SkipReason);

        using var db = new Database(Path.Combine(_appData, "catalog.db"));
        var imaging = new SkiaImageService();
        var scanner = new Scanner(db, imaging, Path.Combine(_appData, "thumbs"));
        await scanner.ScanAsync(_dir);

        var start = Environment.TickCount64;
        var report = await new DuplicateService(db).DetectAsync(_dir);
        var elapsedMs = Environment.TickCount64 - start;

        Console.WriteLine(
            $"[PerfBaseline] duplicate detection: {elapsedMs} ms wall over 40 signatures — " +
            $"exact={report.ExactGroups} variant={report.VariantGroups} burst={report.BurstGroups} — " +
            "synthetic fixture, NOT the Windows/20k-photo baseline (real signature count 3+ orders " +
            "of magnitude smaller than the spec's crossover measurements).");
    }

    /// <summary><c>ByIds</c> at two catalogue sizes (AC 17 asks for 1k vs full size; this fixture
    /// cannot reach either, so it only confirms the code path runs and reports its own timings).</summary>
    [SkippableFact]
    public async Task ByIds_elapsed_baseline()
    {
        Skip.If(!RunPerf, SkipReason);

        using var db = new Database(Path.Combine(_appData, "catalog.db"));
        var imaging = new SkiaImageService();
        var scanner = new Scanner(db, imaging, Path.Combine(_appData, "thumbs"));
        await scanner.ScanAsync(_dir);

        var repo = new ImageRepository(db);
        var allIds = repo.All().Select(i => i.Id).ToList();

        var start = Environment.TickCount64;
        _ = repo.ByIds([allIds[0]]);
        var oneMs = Environment.TickCount64 - start;

        start = Environment.TickCount64;
        _ = repo.ByIds(allIds);
        var allMs = Environment.TickCount64 - start;

        Console.WriteLine(
            $"[PerfBaseline] ByIds([1 id])={oneMs} ms, ByIds([all {allIds.Count} ids])={allMs} ms — " +
            "synthetic fixture, NOT the Windows/full-catalogue baseline.");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }
}
