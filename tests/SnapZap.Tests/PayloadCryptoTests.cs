using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SnapZap.Core.Stego;

namespace SnapZap.Tests;

public class PayloadCryptoTests
{
    // Correctness only here — PBKDF2 strength is an implementation-time OWASP lookup, not
    // something these tests need to pay the real cost of on every run.
    const int TestIterations = 100;

    static byte[] Plaintext(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Encrypt_then_decrypt_round_trip_returns_original_plaintext()
    {
        var plaintext = Plaintext("the quick brown fox jumps over the lazy dog");
        var blob = PayloadCrypto.Encrypt(plaintext, "correct horse battery staple", TestIterations);

        var recovered = PayloadCrypto.Decrypt(blob, "correct horse battery staple");

        Assert.Equal(plaintext, recovered);
    }

    [Fact]
    public void Decrypt_with_wrong_passphrase_throws_AuthenticationTagMismatchException()
    {
        var blob = PayloadCrypto.Encrypt(Plaintext("secret payload"), "right passphrase", TestIterations);

        Assert.Throws<AuthenticationTagMismatchException>(() =>
            PayloadCrypto.Decrypt(blob, "wrong passphrase"));
    }

    [Fact]
    public void Decrypt_of_tampered_ciphertext_throws()
    {
        var blob = PayloadCrypto.Encrypt(Plaintext("secret payload"), "passphrase", TestIterations);
        blob[^1] ^= 0xFF; // flip the last byte of the ciphertext

        Assert.Throws<AuthenticationTagMismatchException>(() =>
            PayloadCrypto.Decrypt(blob, "passphrase"));
    }

    [Fact]
    public void Encrypt_twice_with_identical_inputs_produces_different_blobs()
    {
        var plaintext = Plaintext("identical plaintext");

        var blob1 = PayloadCrypto.Encrypt(plaintext, "passphrase", TestIterations);
        var blob2 = PayloadCrypto.Encrypt(plaintext, "passphrase", TestIterations);

        Assert.NotEqual(blob1, blob2); // random salt + nonce, not a fingerprint leak
    }

    [Fact]
    public void Decrypt_uses_the_iteration_count_stored_in_the_blob_not_a_caller_default()
    {
        // Encrypt at one iteration count...
        var blob = PayloadCrypto.Encrypt(Plaintext("payload"), "passphrase", iterations: 137);

        // ...Decrypt takes no iteration parameter at all — it must read 137 back out of the blob
        // itself, never assume whatever a future default happens to be, to prove the stored-count
        // design actually works rather than only appearing to because caller and stored value
        // happened to match.
        var recovered = PayloadCrypto.Decrypt(blob, "passphrase");

        Assert.Equal(Plaintext("payload"), recovered);
    }

    [Fact]
    public void Decrypt_throws_StegoException_on_unrecognized_version_byte()
    {
        var blob = PayloadCrypto.Encrypt(Plaintext("payload"), "passphrase", TestIterations);
        blob[0] = 0x99; // not the 0x01 format version this code emits

        Assert.Throws<StegoException>(() => PayloadCrypto.Decrypt(blob, "passphrase"));
    }

    [Fact]
    public void Decrypt_throws_StegoException_when_stored_iteration_count_is_implausibly_large()
    {
        var blob = PayloadCrypto.Encrypt(Plaintext("payload"), "passphrase", TestIterations);
        // A corrupted or deliberately hostile carrier (Extract explicitly processes "a PNG
        // someone gave you") could otherwise turn this into a multi-hour PBKDF2 hang instead of
        // a clean, fast rejection.
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(1, 4), uint.MaxValue);

        Assert.Throws<StegoException>(() => PayloadCrypto.Decrypt(blob, "passphrase"));
    }

    [Fact]
    public void Decrypt_throws_StegoException_when_stored_iteration_count_is_zero()
    {
        var blob = PayloadCrypto.Encrypt(Plaintext("payload"), "passphrase", TestIterations);
        // Rfc2898DeriveBytes.Pbkdf2 itself throws ArgumentOutOfRangeException for 0 iterations —
        // this must be caught and surfaced as StegoException, not an unhandled crash.
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(1, 4), 0);

        Assert.Throws<StegoException>(() => PayloadCrypto.Decrypt(blob, "passphrase"));
    }
}
