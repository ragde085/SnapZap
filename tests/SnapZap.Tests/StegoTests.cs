using System.IO.Compression;
using SkiaSharp;
using SnapZap.Core.Stego;

namespace SnapZap.Tests;

public class StegoTests : IDisposable
{
    readonly string _work = Path.Combine(Path.GetTempPath(), "pc_stego_" + Guid.NewGuid().ToString("N"));

    public StegoTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch { /* best-effort cleanup */ }
    }

    string PathFor(string name) => Path.Combine(_work, name);

    static void WritePng(string path, int w, int h, SKColor c)
    {
        using var bmp = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bmp)) canvas.Clear(c);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }

    static void WriteJpeg(string path, int w, int h, SKColor c)
    {
        using var bmp = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bmp)) canvas.Clear(c);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 90);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }

    static byte[] RandomPayload(int length)
    {
        var bytes = new byte[length];
        new Random(12345).NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public async Task Embed_then_extract_round_trip_recovers_byte_identical_payload_and_encrypted_flag()
    {
        var carrier = PathFor("carrier.png");
        WritePng(carrier, 50, 50, SKColors.CornflowerBlue);
        var output = PathFor("output.png");
        var payload = RandomPayload(200);

        await StegoEngine.EmbedAsync(carrier, output, payload, encrypted: true, null, CancellationToken.None);
        var (extracted, encrypted) = await StegoEngine.ExtractAsync(output, null, CancellationToken.None);

        Assert.Equal(payload, extracted);
        Assert.True(encrypted);
    }

    [Fact]
    public async Task Unencrypted_hide_round_trips_and_reports_not_encrypted()
    {
        var carrier = PathFor("carrier-plain.png");
        WritePng(carrier, 50, 50, SKColors.Green);
        var output = PathFor("output-plain.png");
        var payload = RandomPayload(150);

        var result = await StegoEngine.EmbedAsync(carrier, output, payload, encrypted: false, null, CancellationToken.None);
        var (extracted, encrypted) = await StegoEngine.ExtractAsync(output, null, CancellationToken.None);

        Assert.False(result.Encrypted);
        Assert.False(encrypted);
        Assert.Equal(payload, extracted);
    }

    [Fact]
    public async Task Output_file_still_decodes_as_a_valid_image_with_unchanged_dimensions()
    {
        var carrier = PathFor("carrier-img.png");
        WritePng(carrier, 64, 48, SKColors.Purple);
        var output = PathFor("output-img.png");

        await StegoEngine.EmbedAsync(carrier, output, RandomPayload(500), encrypted: false, null, CancellationToken.None);

        var info = new SnapZap.Core.Imaging.SkiaImageService().Probe(output);
        Assert.NotNull(info);
        Assert.Equal(64, info!.Width);
        Assert.Equal(48, info.Height);
    }

    [Fact]
    public async Task Unencrypted_payload_bytes_are_a_standards_conformant_zip()
    {
        // The appended payload is a genuine, unmodified zip. A tool that knows (or detects, the
        // way self-extracting archives conventionally do) where the carrier ends and isolates
        // just those bytes can open it directly — this is the mechanism the classic
        // "copy /b image.jpg + archive.zip out.jpg" trick relies on, and exactly what
        // StegoEngine.ExtractAsync itself does (it copies the payload into its own byte[] before
        // anyone touches it as a zip).
        //
        // Note this only works once the payload bytes are isolated into their own buffer/stream:
        // .NET's System.IO.Compression.ZipArchive computes its internal offsets against the whole
        // underlying stream's absolute Length, not the position it was constructed at — seeking
        // past the carrier in a shared stream and handing that stream straight to ZipArchive still
        // fails, verified while building this test. Many common tools (7-Zip, WinRAR, Windows
        // Explorer's zip-folder handling) do their own offset correction and tolerate the leading
        // bytes in place; .NET's own library does not, so a real generic-tool guarantee can't be
        // made here — only that the payload itself is valid, correctly-formed zip data.
        var sourceFile = PathFor("photo-to-hide.txt");
        File.WriteAllText(sourceFile, "the hidden content");
        var zipBytes = PayloadZipper.CreateZip([sourceFile]);

        var carrier = PathFor("carrier-zip.png");
        WritePng(carrier, 40, 40, SKColors.Yellow);
        var output = PathFor("output-zip.png");

        await StegoEngine.EmbedAsync(carrier, output, zipBytes, encrypted: false, null, CancellationToken.None);

        var carrierLength = new FileInfo(carrier).Length;
        var combined = File.ReadAllBytes(output);
        var isolatedZipBytes = combined[(int)carrierLength..^0];
        // Drop the SNZC footer this engine appends — an external tool wouldn't know about it,
        // only that trailing junk after the zip's own EOCD is harmless (verified separately).
        var footerLength = 4 + 1 + 8;
        isolatedZipBytes = isolatedZipBytes[..^footerLength];

        using var archive = new ZipArchive(new MemoryStream(isolatedZipBytes), ZipArchiveMode.Read);
        var entry = Assert.Single(archive.Entries);
        Assert.Equal("photo-to-hide.txt", entry.Name);
        using var reader = new StreamReader(entry.Open());
        Assert.Equal("the hidden content", reader.ReadToEnd());
    }

    [Fact]
    public async Task EmbedAsync_rejects_a_carrier_that_is_not_a_readable_image()
    {
        var notAnImage = PathFor("not-an-image.png");
        File.WriteAllText(notAnImage, "plain text, not image bytes");

        await Assert.ThrowsAsync<StegoException>(() =>
            StegoEngine.EmbedAsync(notAnImage, PathFor("out.png"), RandomPayload(10), false, null, CancellationToken.None));
    }

    [Fact]
    public async Task EmbedAsync_rejects_a_carrier_that_does_not_exist()
    {
        await Assert.ThrowsAsync<StegoException>(() =>
            StegoEngine.EmbedAsync(PathFor("does-not-exist.png"), PathFor("out.png"), RandomPayload(10), false, null, CancellationToken.None));
    }

    [Fact]
    public async Task EmbedAsync_works_with_a_jpeg_carrier()
    {
        // Concatenation doesn't touch pixels, so unlike the previous LSB approach, any image
        // format the app can decode works as a carrier — not just PNG.
        var carrier = PathFor("carrier.jpg");
        WriteJpeg(carrier, 80, 60, SKColors.Orange);
        var output = PathFor("output.jpg");
        var payload = RandomPayload(300);

        await StegoEngine.EmbedAsync(carrier, output, payload, encrypted: true, null, CancellationToken.None);
        var (extracted, encrypted) = await StegoEngine.ExtractAsync(output, null, CancellationToken.None);

        Assert.Equal(payload, extracted);
        Assert.True(encrypted);
        Assert.NotNull(new SnapZap.Core.Imaging.SkiaImageService().Probe(output));
    }

    [Fact]
    public async Task ExtractAsync_on_carrier_with_no_hidden_footer_throws_friendly_message()
    {
        var carrier = PathFor("plain.png");
        WritePng(carrier, 50, 50, SKColors.White);

        var ex = await Assert.ThrowsAsync<StegoException>(() =>
            StegoEngine.ExtractAsync(carrier, null, CancellationToken.None));

        Assert.Contains("doesn't appear to contain hidden data", ex.Message);
    }

    [Fact]
    public async Task ExtractAsync_throws_on_a_footer_claiming_more_payload_than_the_file_actually_has()
    {
        var carrier = PathFor("carrier-corrupt.png");
        WritePng(carrier, 40, 40, SKColors.Red);
        var output = PathFor("output-corrupt.png");
        await StegoEngine.EmbedAsync(carrier, output, RandomPayload(20), encrypted: false, null, CancellationToken.None);

        // Corrupt the footer's payload-length field (last 8 bytes before the very end) to claim a
        // much larger payload than actually precedes it.
        var bytes = File.ReadAllBytes(output);
        var lengthFieldStart = bytes.Length - 8;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(
            bytes.AsSpan(lengthFieldStart, 8), (ulong)(bytes.Length + 1_000_000));
        File.WriteAllBytes(output, bytes);

        await Assert.ThrowsAsync<StegoException>(() =>
            StegoEngine.ExtractAsync(output, null, CancellationToken.None));
    }

    [Fact]
    public async Task EmbedAsync_refuses_to_overwrite_an_unrelated_pre_existing_output_file()
    {
        var carrier = PathFor("carrier3.png");
        WritePng(carrier, 40, 40, SKColors.Orange);
        var output = PathFor("pre-existing.png");
        File.WriteAllText(output, "totally unrelated file content");

        await Assert.ThrowsAsync<StegoException>(() =>
            StegoEngine.EmbedAsync(carrier, output, RandomPayload(10), false, null, CancellationToken.None));

        Assert.Equal("totally unrelated file content", File.ReadAllText(output));
    }

    [Fact]
    public async Task EmbedAsync_allows_the_explicit_same_path_carrier_and_output_case()
    {
        // AC8: a user can explicitly type the same absolute path for both carrier and output —
        // the one case where writing over an existing file at outputPath is allowed.
        var carrierAndOutput = PathFor("overwrite-me.png");
        WritePng(carrierAndOutput, 40, 40, SKColors.Teal);
        var payload = RandomPayload(50);

        var result = await StegoEngine.EmbedAsync(
            carrierAndOutput, carrierAndOutput, payload, encrypted: false, null, CancellationToken.None);

        Assert.Equal(carrierAndOutput, result.OutputPath);
        var (extracted, _) = await StegoEngine.ExtractAsync(carrierAndOutput, null, CancellationToken.None);
        Assert.Equal(payload, extracted);
    }

    [Fact]
    public async Task EmbedAsync_with_a_pre_cancelled_token_writes_no_output()
    {
        var carrier = PathFor("carrier-cancel.png");
        WritePng(carrier, 40, 40, SKColors.Gray);
        var output = PathFor("should-not-exist.png");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Task.Run(Action, CancellationToken) short-circuits an already-cancelled token before
        // ever invoking the delegate, surfacing as TaskCanceledException (a subclass) rather than
        // a bare OperationCanceledException thrown from inside EmbedAsync itself.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            StegoEngine.EmbedAsync(carrier, output, RandomPayload(10), false, null, cts.Token));

        Assert.False(File.Exists(output));
    }
}
