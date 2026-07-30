using System.Security.Cryptography;
using System.Text;
using SkiaSharp;
using SnapZap.Core.Analysis;
using SnapZap.Core.Data;
using SnapZap.Core.Dedup;
using SnapZap.Core.Imaging;
using SnapZap.Core.Nsfw;

namespace SnapZap.Droid;

/// <summary>
/// AC-2.3 / AC-2.5 / AC-2.8: does <c>SnapZap.Core</c> actually <i>work</i> on an Android device,
/// as opposed to merely compiling for one?
///
/// Restore and compile were cleared on the host (docs/ANDROID-PORT-ACS.md §1.1), but neither says
/// anything about whether <c>libSkiaSharp.so</c> loads in this process, whether SQLite's native
/// bundle initialises without an explicit <c>Batteries_V2.Init()</c>, or — the one that would fail
/// silently and matter most — whether a perceptual hash computed here is <b>bit-identical</b> to
/// the value the dev Mac produces. A platform-dependent hash would not crash; it would quietly
/// stop duplicates matching across desktop and phone, which is the failure this class exists to
/// catch before any UI is built on top of it.
///
/// Reference values are emitted by the same code paths on macOS (arm64, .NET 10.0.302). Blur is
/// compared with tolerance because it is a floating-point reduction whose last couple of digits
/// legitimately drift between platforms — the committed <c>GoldenValueTests.GoldenBlurScore</c>
/// already differs from a fresh macOS run at ~1e-10, which is why that test compares to six
/// decimals. The perceptual hash is compared exactly, because it is the thing that must not drift:
/// its bits come from threshold comparisons, so float noise near a tie flips a bit and changes the
/// hash.
/// </summary>
public static class CoreSelfTest
{
    // --- desktop reference values (macOS arm64) -------------------------------------------
    const string RefPngSha256 = "1710c31c5c1a45affecbe7f2f73b5a2489a76b1c5209e8e8b7ff1588ebafee2b";
    const string RefPhashSha256 = "b945670bc6fa3b8f3813b1f87ef42cadaa1dc762224d596ee9b5450bd1b07500";
    const double RefBlurScore = 291.10588906865104;
    const double BlurTolerance = 1e-6;

    // NSFW inference on the same fixture. Tolerance is far looser than the hash comparisons on
    // purpose: this is float matmul through ONNX Runtime, where a different build, kernel or
    // thread count legitimately shifts the last few digits. Nothing in the app compares scores
    // for equality — NsfwSettings thresholds them — so agreement to ~1e-3 is what "the same
    // model behaving the same way" actually means here.
    const double RefNsfwWhole = 0.0010935845;
    const double RefNsfwTileMean = 0.0022139877;
    const double NsfwTolerance = 1e-3;

    /// <summary>
    /// The deterministic fixture generator copied verbatim from <c>GoldenValueTests.Scene</c>.
    /// Duplicated rather than shared because the test project is not referenced from here and
    /// must not be — but it has to stay byte-identical to that method or every comparison below
    /// is meaningless. If one changes, change both.
    /// </summary>
    static SKBitmap Scene(int w, int h)
    {
        var bmp = new SKBitmap(w, h);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(new SKColor(30, 30, 40));
        using var paint = new SKPaint { IsAntialias = true };
        paint.Color = new SKColor(220, 200, 120);
        canvas.DrawRect(new SKRect(w * 0.05f, h * 0.05f, w * 0.45f, h * 0.30f), paint);
        paint.Color = new SKColor(90, 160, 220);
        canvas.DrawCircle(w * 0.70f, h * 0.35f, MathF.Min(w, h) * 0.18f, paint);
        paint.Color = new SKColor(200, 90, 90);
        canvas.DrawRect(new SKRect(w * 0.15f, h * 0.55f, w * 0.85f, h * 0.95f), paint);
        return bmp;
    }

    public sealed record Result(string Name, bool Pass, string Detail);

    /// <param name="modelPath">
    /// Where <c>nsfw.onnx</c> was pushed on this device. Optional in exactly the way it is on
    /// desktop: a missing model disables NSFW scoring and nothing else, so its absence is
    /// reported as a skip rather than a failure.
    /// </param>
    public static List<Result> Run(string workDir, string? modelPath = null)
    {
        var results = new List<Result>();
        void Check(string name, Func<string> body)
        {
            try { results.Add(new Result(name, true, body())); }
            catch (Exception ex) { results.Add(new Result(name, false, $"{ex.GetType().Name}: {ex.Message}")); }
        }

        Directory.CreateDirectory(workDir);
        var pngPath = Path.Combine(workDir, "selftest_scene.png");

        // 1. SkiaSharp loads at all. This is the one that decides whether v1 is viable — every
        //    decode, thumbnail, blur score and perceptual hash in the app goes through it.
        Check("skia_load", () =>
        {
            using var bmp = Scene(16, 16);
            return $"libSkiaSharp OK, SKBitmap {bmp.Width}x{bmp.Height}";
        });

        // 2. Encode + file IO into app-private storage, and the encoder is byte-for-byte the
        //    same as the desktop's (a different libpng/Skia build would show up right here).
        Check("png_encode_sha256", () =>
        {
            using var scene = Scene(256, 256);
            using (var img = SKImage.FromBitmap(scene))
            using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
            using (var fs = File.Create(pngPath)) data.SaveTo(fs);

            var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(pngPath)));
            return sha == RefPngSha256 ? $"match {sha[..16]}…" : $"DIFFERS device={sha} desktop={RefPngSha256}";
        });

        (float[] gray, int w, int h)? decoded = null;

        // 3. Decode-to-grayscale, the shared entry point Scanner.Analyze feeds to both the blur
        //    detector and the perceptual hash.
        Check("decode_gray", () =>
        {
            decoded = new SkiaImageService().DecodeGray(pngPath, 512);
            if (decoded is null) throw new InvalidOperationException("DecodeGray returned null");
            return $"{decoded.Value.w}x{decoded.Value.h}";
        });

        Check("blur_score", () =>
        {
            if (decoded is null) throw new InvalidOperationException("skipped — decode_gray failed");
            var score = BlurDetector.ScoreFrom(decoded.Value.gray, decoded.Value.w, decoded.Value.h)
                        ?? throw new InvalidOperationException("ScoreFrom returned null");
            var delta = Math.Abs(score - RefBlurScore);
            return delta <= BlurTolerance
                ? $"{score:R} (Δ{delta:E2})"
                : $"DIFFERS device={score:R} desktop={RefBlurScore:R} Δ{delta:E2}";
        });

        // 4. The one that must be bit-exact. See the class remarks.
        Check("phash_bit_exact", () =>
        {
            if (decoded is null) throw new InvalidOperationException("skipped — decode_gray failed");
            var ph = PerceptualHash.FromGray(decoded.Value.gray, decoded.Value.w, decoded.Value.h);
            var bytes = ph.ToBytes();
            if (bytes.Length != PerceptualHash.ByteLength)
                throw new InvalidOperationException($"expected {PerceptualHash.ByteLength} bytes, got {bytes.Length}");
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            return sha == RefPhashSha256
                ? $"BIT-EXACT vs desktop ({sha[..16]}…)"
                : $"DIFFERS — dedup would break across platforms. device={sha} desktop={RefPhashSha256}";
        });

        // 5. SQLite: AC-2.5, and AC-2.8's open question — nothing in src/ calls
        //    SQLitePCL.Batteries_V2.Init(), so this is where we find out whether the bundle's
        //    module initialiser fires under this TFM. It would fail at first open, not at startup.
        Check("sqlite_open_and_roundtrip", () =>
        {
            var dbPath = Path.Combine(workDir, "selftest.db");
            File.Delete(dbPath);
            using var db = new Database(dbPath);
            using var cmd = db.Writer.CreateCommand();
            cmd.CommandText = "select count(*) from images";
            var n = Convert.ToInt64(cmd.ExecuteScalar());
            return $"schema created, images row count = {n}, file {new FileInfo(dbPath).Length} bytes";
        });

        // 6. ONNX Runtime (AC-2.4) — the last native unknown, and the one with a live upstream
        //    bug matching this project's exact topology: microsoft/onnxruntime#29270, where a
        //    plain-net10.0 library ProjectReference'd from a net10.0-android head and built on a
        //    macOS host gets the wrong Linux/glibc native library bundled and dies at launch.
        //    Constructing the session is what would trip that, so this check is meaningful even
        //    before it looks at the score.
        //
        //    A failure here costs NSFW scoring and nothing else — exactly the graceful
        //    degradation a missing model already produces on desktop — so it is not a v1 blocker.
        if (modelPath is null || !File.Exists(modelPath))
        {
            results.Add(new Result("onnx_nsfw", true,
                $"SKIPPED — no model at '{modelPath ?? "(null)"}'. Push nsfw.onnx to run this check."));
        }
        else
        {
            Check("onnx_nsfw", () =>
            {
                // Timed, because throughput is the open question about NSFW on Android — not
                // correctness, which this check already settled. The model is ViT-base: ~86M fp32
                // parameters, and NsfwDepth.Tiled runs it ten times per photo (the whole frame plus
                // NsfwDecision.Tiles). Whether the feature is usable at all is a straight
                // multiplication from the per-inference cost, and that number cannot be taken from
                // the emulator — it is x86_64 and says nothing about an arm64 phone. So it is
                // measured here, on the device, and reported whether or not the scores match.
                //
                // Session construction is timed separately: 327.5 MiB of weights has to be read and
                // laid out before the first inference, and that cost is paid once per scan rather
                // than once per photo, so averaging it into the per-inference figure would flatter
                // a long run and slander a short one.
                var swLoad = System.Diagnostics.Stopwatch.StartNew();
                using var clf = new OnnxNsfwClassifier(modelPath);
                swLoad.Stop();

                // One untimed pass first. The first inference pays for lazy allocation and JIT
                // inside the runtime, so timing it would measure warm-up rather than steady state.
                _ = clf.ScoreFile(pngPath);

                var swWhole = System.Diagnostics.Stopwatch.StartNew();
                var whole = clf.ScoreFile(pngPath);
                swWhole.Stop();

                var swTiled = System.Diagnostics.Stopwatch.StartNew();
                var verdict = clf.ScoreFile(pngPath, NsfwDepth.Tiled);
                swTiled.Stop();

                var dw = Math.Abs(whole - RefNsfwWhole);
                var dt = verdict.TileMean is { } tm ? Math.Abs(tm - RefNsfwTileMean) : double.NaN;
                var ok = dw <= NsfwTolerance && (double.IsNaN(dt) || dt <= NsfwTolerance);

                // Projected to a library-sized run, because "412 ms" invites a shrug and
                // "1.1 hours for 10,000 photos" is a decision.
                var perTiled = swTiled.Elapsed.TotalMilliseconds;
                string Project(int photos) =>
                    $"{photos:N0}→{TimeSpan.FromMilliseconds(perTiled * photos):d\\d\\ hh\\h\\ mm\\m}";

                var timing =
                    $"load={swLoad.ElapsedMilliseconds}ms whole={swWhole.Elapsed.TotalMilliseconds:F0}ms "
                    + $"tiled(x{NsfwDecision.Tiles.Count + 1})={perTiled:F0}ms "
                    + $"· tiled projection {Project(10_000)}, {Project(40_000)}";

                return ok
                    ? $"whole={whole:F7} (Δ{dw:E2}) tileMean={verdict.TileMean:F7} (Δ{dt:E2}), "
                      + $"config_from_file={clf.ConfigFromFile} · {timing}"
                    : $"DIFFERS whole={whole:R} (desktop {RefNsfwWhole:R}, Δ{dw:E2}) tileMean={verdict.TileMean} "
                      + $"(desktop {RefNsfwTileMean:R}, Δ{dt:E2}) · {timing}";
            });
        }

        // 7. Where LocalApplicationData actually resolves on this device (AC-0.5 / AC-5.4). The
        //    plan assumes app-private internal storage; a wrong answer fails quietly by writing
        //    the catalogue somewhere unexpected rather than by erroring.
        Check("special_folders", () =>
        {
            var lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return $"LocalApplicationData='{lad}' UserProfile='{up}'";
        });

        return results;
    }

    public static string Format(IEnumerable<Result> results)
    {
        var sb = new StringBuilder();
        foreach (var r in results) sb.AppendLine($"[{(r.Pass ? "PASS" : "FAIL")}] {r.Name}: {r.Detail}");
        return sb.ToString();
    }
}
