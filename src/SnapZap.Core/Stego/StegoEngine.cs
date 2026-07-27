using System.Buffers.Binary;
using SnapZap.Core.Imaging;
using SnapZap.Core.Scanning;

namespace SnapZap.Core.Stego;

/// <summary>
/// Hides photos inside a carrier image by concatenation (a "polyglot" file): the carrier's own
/// bytes, unmodified, followed by the payload (a zip, optionally AES-GCM encrypted — see
/// <see cref="PayloadCrypto"/>/<see cref="PayloadZipper"/>), followed by a small fixed-size footer
/// recording whether the payload is encrypted and how long it is. Image decoders stop reading at
/// the image's own end-of-data marker and never see the appended bytes, so the output opens
/// exactly like the original carrier; when the payload is unencrypted, any generic zip tool can
/// also open the same file directly, since zip readers locate their central directory by scanning
/// backward from the end of the file rather than reading from byte 0.
/// </summary>
/// <remarks>
/// This engine is deliberately ignorant of encryption and zip internals — it treats the payload as
/// opaque bytes and only records the <c>encrypted</c> flag it's given, exactly as before under the
/// LSB approach this replaced. Static, called directly (same non-DI convention as
/// <see cref="Scanning.Hasher"/>).
/// </remarks>
public static class StegoEngine
{
    static readonly byte[] FooterMagic = "SNZC"u8.ToArray();

    // magic (4) + encrypted flag (1) + payload length, BE UInt64 (8)
    const int FooterLength = 4 + 1 + 8;

    public static async Task<HideResult> EmbedAsync(
        string carrierPath, string outputPath, byte[] payload, bool encrypted,
        IProgress<HideProgress>? progress, CancellationToken ct) =>
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            if (!File.Exists(carrierPath))
                throw new StegoException("Carrier file doesn't exist.");
            if (new SkiaImageService().Probe(carrierPath) is null)
                throw new StegoException("Carrier must be a readable image file.");

            // Refuse to clobber an unrelated pre-existing file. The one path a user can take
            // deliberately is pointing outputPath at the same bytes as the carrier (including
            // literally the same file) — anything else at that path is left alone (AC8).
            if (File.Exists(outputPath) && !FilesByteIdentical(outputPath, carrierPath))
                throw new StegoException($"A different file already exists at {outputPath}.");

            progress?.Report(new HideProgress(HidePhase.Writing, 0, payload.Length));
            ct.ThrowIfCancellationRequested();

            WriteConcatenatedAtomic(carrierPath, payload, encrypted, outputPath);

            progress?.Report(new HideProgress(HidePhase.Writing, payload.Length, payload.Length));
            return new HideResult(outputPath, payload.Length, encrypted);
        }, ct);

    /// <summary>Reads the appended payload back out of a carrier and reports whether it was
    /// encrypted — the caller (the Extract dialog) decides whether to run it through
    /// <see cref="PayloadCrypto.Decrypt"/> or hand it straight to <see cref="PayloadZipper.ExtractZip"/>.</summary>
    public static async Task<(byte[] Payload, bool Encrypted)> ExtractAsync(
        string carrierPath, IProgress<ExtractProgress>? progress, CancellationToken ct) =>
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ExtractProgress(ExtractPhase.Reading, 0, 0));

            if (!File.Exists(carrierPath))
                throw new StegoException("That file doesn't exist.");

            using var stream = File.OpenRead(carrierPath);
            if (stream.Length < FooterLength)
                throw new StegoException("This image doesn't appear to contain hidden data.");

            var footer = new byte[FooterLength];
            stream.Seek(-FooterLength, SeekOrigin.End);
            ReadExact(stream, footer);

            if (!footer.AsSpan(0, 4).SequenceEqual(FooterMagic))
                throw new StegoException("This image doesn't appear to contain hidden data.");

            var encrypted = footer[4] != 0;
            var payloadLength = (long)BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(5, 8));
            var payloadStart = stream.Length - FooterLength - payloadLength;
            if (payloadLength < 0 || payloadStart < 0)
                throw new StegoException("Hidden data is corrupted or incomplete.");

            var payload = new byte[payloadLength];
            stream.Seek(payloadStart, SeekOrigin.Begin);
            ReadExact(stream, payload);

            progress?.Report(new ExtractProgress(ExtractPhase.Reading, payloadLength, payloadLength));
            return (payload, encrypted);
        }, ct);

    static void ReadExact(Stream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0) throw new StegoException("Hidden data is corrupted or incomplete.");
            total += read;
        }
    }

    static void WriteConcatenatedAtomic(string carrierPath, byte[] payload, bool encrypted, string outputPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Same-path overwrite (AC8) is a supported, explicit case — write to a temp file first
        // and move into place, so a mid-write failure never leaves a truncated carrier/output,
        // and reading carrierPath fully completes before outputPath is ever touched even when
        // outputPath == carrierPath.
        var tmp = Path.Combine(dir ?? ".", Path.GetRandomFileName());
        try
        {
            using (var outStream = File.Create(tmp))
            {
                using (var carrierStream = File.OpenRead(carrierPath))
                    carrierStream.CopyTo(outStream);

                outStream.Write(payload, 0, payload.Length);

                Span<byte> footer = stackalloc byte[FooterLength];
                FooterMagic.CopyTo(footer);
                footer[4] = (byte)(encrypted ? 1 : 0);
                BinaryPrimitives.WriteUInt64BigEndian(footer.Slice(5, 8), (ulong)payload.Length);
                outStream.Write(footer);
            }
            File.Move(tmp, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) try { File.Delete(tmp); } catch { /* best-effort cleanup */ }
        }
    }

    static bool FilesByteIdentical(string a, string b)
    {
        if (!File.Exists(a) || !File.Exists(b)) return false;
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        return Hasher.HashFile(a) == Hasher.HashFile(b);
    }
}
