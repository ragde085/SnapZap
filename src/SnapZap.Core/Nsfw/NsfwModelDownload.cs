using System.Security.Cryptography;

namespace SnapZap.Core.Nsfw;

/// <summary>Progress of a sidecar download: bytes so far, total when the server declares one.</summary>
public sealed record DownloadProgress(string File, long BytesReceived, long? TotalBytes)
{
    public double? Fraction => TotalBytes is > 0 ? Math.Clamp(BytesReceived / (double)TotalBytes, 0, 1) : null;
}

/// <summary>
/// Fetches the NSFW scoring model, pinned to an exact revision and verified by SHA-256.
/// </summary>
/// <remarks>
/// <para><b>Why this exists rather than the shell scripts.</b> <c>scripts/install-deps.sh</c> and
/// <c>.bat</c> already do this, and <c>DependencyChecker</c> already knows the URLs — but the
/// scripts are not reachable from a phone, and the desktop's own "download" is a list of links the
/// user clicks and then has to place by hand, with nothing checking what they placed. Neither is a
/// route an Android user has. This is the shared, verified one.</para>
///
/// <para><b>The revision pin and the checksum are the security boundary.</b> A model file is
/// executable content in every sense that matters — it decides what the app tells the user about
/// their photos — so it is fetched from one immutable revision and rejected unless the bytes hash
/// to the expected value. A partial or substituted download fails closed and leaves nothing behind:
/// the write goes to a temporary file and is only moved into place after the hash matches, so an
/// interrupted download can never be mistaken for a finished one.</para>
///
/// <para><b>The photos never go anywhere.</b> This is the only network call in the product, it
/// travels in one direction, and it fetches a fixed URL known at compile time. Inference runs
/// entirely on-device against the downloaded file.</para>
///
/// <para>⚠ The same revision and hashes appear in <c>scripts/install-deps.sh</c>,
/// <c>scripts/install-deps.bat</c> and <c>DependencyChecker</c>. Changing the pin means changing it
/// in all of them; they are not generated from here because two of them are shell.</para>
/// </remarks>
public static class NsfwModelDownload
{
    public const string Revision = "1ceb3c7fe1e9f3f2507e6df577437f23a9149fd5";

    const string Base =
        "https://huggingface.co/onnx-community/nsfw_image_detection-ONNX/resolve/" + Revision;

    /// <summary>One file to fetch: where from, what to call it, and what it must hash to.</summary>
    public sealed record Asset(string Url, string FileName, string Sha256, long ApproxBytes);

    public static readonly Asset Model =
        new($"{Base}/onnx/model.onnx", "nsfw.onnx",
            "a4316a4fb750169ac4fcabaabee1fcbd982b0ee8c0cc63fe3e944954bb9a7d9c", 343_401_688);

    /// <summary>
    /// Fetched as an asset in its own right, not a footnote: the classifier reads the real
    /// mean/std/size out of it, and a plausible-but-wrong score is worse than no score.
    /// </summary>
    public static readonly Asset PreprocessorConfig =
        new($"{Base}/preprocessor_config.json", "preprocessor_config.json",
            "ae9bb157b9629887cc74913a4e7c12c9308f374f0930e8072320e8f2e1583c5e", 353);

    public static IReadOnlyList<Asset> All => [Model, PreprocessorConfig];

    /// <summary>Total bytes to move, for a progress bar that does not jump between files.</summary>
    public static long TotalApproxBytes => All.Sum(a => a.ApproxBytes);

    /// <summary>
    /// Downloads every asset into <paramref name="destDir"/>, skipping any that is already present
    /// and correct. Returns the model's final path.
    /// </summary>
    /// <remarks>
    /// Idempotent by checksum rather than by existence: a half-written file left by a previous run
    /// hashes wrong and is fetched again, which is the behaviour <c>install-deps.sh</c> already has
    /// and the reason it can be re-run safely.
    /// </remarks>
    public static async Task<string> FetchAsync(
        string destDir, HttpClient http,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destDir);
        long completedBytes = 0;

        foreach (var asset in All)
        {
            var dest = Path.Combine(destDir, asset.FileName);
            if (File.Exists(dest) && await HashMatchesAsync(dest, asset.Sha256, ct))
            {
                completedBytes += asset.ApproxBytes;
                progress?.Report(new DownloadProgress(asset.FileName, completedBytes, TotalApproxBytes));
                continue;
            }

            var tmp = dest + ".part";
            try
            {
                await DownloadToAsync(asset, tmp, http, completedBytes, progress, ct);

                var actual = await Sha256OfAsync(tmp, ct);
                if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"{asset.FileName} failed verification — expected {asset.Sha256}, got {actual}. " +
                        "The file was discarded.");

                File.Move(tmp, dest, overwrite: true);
                completedBytes += asset.ApproxBytes;
            }
            finally
            {
                // Never leave a .part behind: it is not usable, and a stray one next to a real
                // model is the kind of thing that gets mistaken for a partial install.
                if (File.Exists(tmp)) { try { File.Delete(tmp); } catch (IOException) { } }
            }
        }

        return Path.Combine(destDir, Model.FileName);
    }

    static async Task DownloadToAsync(
        Asset asset, string tmpPath, HttpClient http, long alreadyDone,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        using var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var declared = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long received = 0;
        long lastReport = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            received += read;

            // Throttled to whole megabytes. A 328 MB file is ~4,000 buffer reads; reporting each
            // one would spend more time marshalling to the UI thread than moving bytes.
            if (received - lastReport < 1 << 20) continue;
            lastReport = received;
            progress?.Report(new DownloadProgress(
                asset.FileName, alreadyDone + received,
                declared is > 0 ? alreadyDone + declared : TotalApproxBytes));
        }
    }

    static async Task<bool> HashMatchesAsync(string path, string expected, CancellationToken ct)
    {
        try { return string.Equals(await Sha256OfAsync(path, ct), expected, StringComparison.OrdinalIgnoreCase); }
        catch (IOException) { return false; }
    }

    static async Task<string> Sha256OfAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexStringLower(hash);
    }
}
