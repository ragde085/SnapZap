using System.Buffers.Binary;
using System.Security.Cryptography;
using SnapZap.Core.Resources;

namespace SnapZap.Core.Stego;

/// <summary>
/// AES-GCM encryption of the zipped photo payload, keyed from a user passphrase via PBKDF2.
/// Static and isolated behind this one type — same style as <see cref="Scanning.Hasher"/> — and
/// knows nothing about the SNZ1 LSB frame; <see cref="StegoEngine"/> treats its output as opaque
/// bytes.
/// </summary>
public static class PayloadCrypto
{
    const byte FormatVersion = 0x01;
    const int SaltLength = 16;
    const int NonceLength = 12;
    const int TagLength = 16;
    const int KeyLength = 32;

    /// <summary>Sanity ceiling on the iteration count read back out of a blob. <see cref="Decrypt"/>
    /// processes carriers from untrusted sources ("a PNG someone gave you"), so a corrupted or
    /// deliberately hostile blob could otherwise set this near <see cref="uint.MaxValue"/> (a
    /// multi-hour PBKDF2 hang) or to <c>0</c> (an uncaught <see cref="ArgumentOutOfRangeException"/>
    /// from <see cref="Rfc2898DeriveBytes"/>). Comfortably above any plausible legitimate default,
    /// with headroom for the default to rise in a future release, while still bounding worst-case
    /// latency to a few seconds.</summary>
    const int MaxPlausibleIterations = 10_000_000;

    /// <summary>Blob layout: 1-byte version + 4-byte BE iteration count + 16-byte salt +
    /// 12-byte nonce + 16-byte tag + ciphertext. The iteration count travels with the blob so a
    /// future default can change without breaking previously-hidden images (Wire Format).</summary>
    public static byte[] Encrypt(byte[] plaintext, string passphrase, int iterations, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var salt = new byte[SaltLength];
        var nonce = new byte[NonceLength];
        RandomNumberGenerator.Fill(salt);
        RandomNumberGenerator.Fill(nonce);

        var key = DeriveKey(passphrase, salt, iterations);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];

        using (var aes = new AesGcm(key, TagLength))
            aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var blob = new byte[1 + 4 + SaltLength + NonceLength + TagLength + ciphertext.Length];
        var span = blob.AsSpan();
        span[0] = FormatVersion;
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(1, 4), (uint)iterations);
        salt.CopyTo(span.Slice(5, SaltLength));
        nonce.CopyTo(span.Slice(5 + SaltLength, NonceLength));
        tag.CopyTo(span.Slice(5 + SaltLength + NonceLength, TagLength));
        ciphertext.CopyTo(span[(5 + SaltLength + NonceLength + TagLength)..]);
        return blob;
    }

    /// <summary>Reads the version and iteration count from <paramref name="blob"/> itself — never
    /// a caller-supplied or hardcoded iteration count — then derives the key and decrypts. Lets
    /// <see cref="AuthenticationTagMismatchException"/> propagate on a wrong passphrase or
    /// tampered data; the caller catches that separately from <see cref="StegoException"/>.</summary>
    public static byte[] Decrypt(byte[] blob, string passphrase, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        const int headerLength = 1 + 4 + SaltLength + NonceLength + TagLength;
        if (blob.Length < headerLength)
            throw new StegoException(CoreStrings.Get("HiddenDataCorrupted"));

        var span = blob.AsSpan();
        if (span[0] != FormatVersion)
            throw new StegoException(CoreStrings.Get("UnrecognizedPayloadVersion", span[0]));

        var rawIterations = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(1, 4));
        if (rawIterations == 0 || rawIterations > MaxPlausibleIterations)
            throw new StegoException(CoreStrings.Get("HiddenDataCorrupted"));
        var iterations = (int)rawIterations;

        var salt = span.Slice(5, SaltLength).ToArray();
        var nonce = span.Slice(5 + SaltLength, NonceLength).ToArray();
        var tag = span.Slice(5 + SaltLength + NonceLength, TagLength).ToArray();
        var ciphertext = span[headerLength..].ToArray();

        var key = DeriveKey(passphrase, salt, iterations);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagLength);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    static byte[] DeriveKey(string passphrase, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            System.Text.Encoding.UTF8.GetBytes(passphrase), salt, iterations,
            HashAlgorithmName.SHA256, KeyLength);
}
