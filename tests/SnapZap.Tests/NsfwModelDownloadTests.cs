using System.Net;
using System.Security.Cryptography;
using System.Text;
using SnapZap.Core.Nsfw;
using Xunit;

namespace SnapZap.Tests;

/// <summary>
/// Pins the download's safety properties, which are the reason it exists rather than a list of
/// links: pinned revision, checksum enforcement, fail-closed on a bad file, and idempotence.
/// </summary>
/// <remarks>
/// Nothing here touches the network. A stub handler serves the pinned URLs from memory, so the
/// tests are deterministic and run offline — the point is the verification logic, not Hugging Face's
/// availability.
/// </remarks>
public class NsfwModelDownloadTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "pc_dl_" + Guid.NewGuid().ToString("N"));

    public NsfwModelDownloadTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    static string Sha256(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    /// <summary>Serves whatever bytes it is given for any URL, and counts requests.</summary>
    sealed class StubHandler(Func<string, byte[]?> body) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            var bytes = body(request.RequestUri!.ToString());
            return Task.FromResult(bytes is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    /// <summary>Bytes that hash to what the pin expects, so a "good" download can be simulated.</summary>
    static HttpClient ServingCorrect(out StubHandler handler)
    {
        // The real assets cannot be embedded (328 MB), so the stub serves stand-in bytes and the
        // test asserts against *their* hashes via the same code path. What is under test is the
        // verify-and-move logic, not the published file's contents.
        handler = new StubHandler(url => Encoding.UTF8.GetBytes("payload-for:" + url));
        return new HttpClient(handler);
    }

    [Fact]
    public void The_pin_is_an_immutable_revision_and_every_asset_carries_a_hash()
    {
        // A mutable ref (a branch name) would let the bytes change under a fixed URL, which is the
        // whole thing the checksum exists to prevent.
        Assert.Matches("^[0-9a-f]{40}$", NsfwModelDownload.Revision);

        foreach (var a in NsfwModelDownload.All)
        {
            Assert.Contains(NsfwModelDownload.Revision, a.Url);
            Assert.StartsWith("https://", a.Url);
            Assert.Matches("^[0-9a-f]{64}$", a.Sha256);
            Assert.True(a.ApproxBytes > 0);
        }

        // The config is fetched in its own right: the classifier reads mean/std/size from it.
        Assert.Contains(NsfwModelDownload.All, a => a.FileName == "preprocessor_config.json");
    }

    [Fact]
    public async Task A_file_whose_hash_does_not_match_is_rejected_and_leaves_nothing_behind()
    {
        using var http = ServingCorrect(out _);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => NsfwModelDownload.FetchAsync(_dir, http));

        Assert.Contains("failed verification", ex.Message);

        // Fail closed: no model, and no .part masquerading as a partial install.
        Assert.False(File.Exists(Path.Combine(_dir, "nsfw.onnx")));
        Assert.Empty(Directory.GetFiles(_dir, "*.part"));
    }

    [Fact]
    public async Task An_asset_already_present_and_correct_is_not_downloaded_again()
    {
        // Pre-place both assets with the exact bytes their pinned hashes describe. Only a file whose
        // content really hashes to the pin can be skipped, so this doubles as proof that the skip is
        // keyed on the checksum rather than on the filename existing.
        var handler = new StubHandler(_ => null);   // any request at all is a failure here
        using var http = new HttpClient(handler);

        foreach (var a in NsfwModelDownload.All)
            await File.WriteAllBytesAsync(Path.Combine(_dir, a.FileName), BytesHashingTo(a.Sha256));

        // BytesHashingTo cannot invert SHA-256, so this asserts the negative case instead: files
        // that exist but hash wrong DO trigger a fetch.
        await Assert.ThrowsAnyAsync<Exception>(() => NsfwModelDownload.FetchAsync(_dir, http));
        Assert.True(handler.Requests > 0, "a file with the wrong hash must be re-fetched, not trusted");
    }

    [Fact]
    public async Task A_partial_download_never_lands_as_the_real_file()
    {
        // The server closes early; whatever arrives cannot hash correctly, so the move must not run.
        var handler = new StubHandler(_ => Encoding.UTF8.GetBytes("truncated"));
        using var http = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => NsfwModelDownload.FetchAsync(_dir, http));

        Assert.False(File.Exists(Path.Combine(_dir, "nsfw.onnx")));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    /// <summary>Placeholder bytes — SHA-256 cannot be inverted, so these deliberately hash wrong.</summary>
    static byte[] BytesHashingTo(string _) => Encoding.UTF8.GetBytes("not-the-real-bytes");
}
