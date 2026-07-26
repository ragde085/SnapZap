using System.Security.Cryptography;

namespace SnapZap.Core.Scanning;

/// <summary>
/// Content hashing for exact-duplicate identity and export verification.
///
/// DESIGN.md originally named BLAKE3; we use SHA-256 instead — it is built into .NET (no
/// native dependency to complicate the Mac→Windows cross-compile), hardware-accelerated on
/// modern CPUs, and isolated behind this single type so it can be swapped later without
/// touching callers.
/// </summary>
public static class Hasher
{
    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexStringLower(hash);
    }

    public static string HashFile(string path, out long length)
    {
        using var stream = File.OpenRead(path);
        length = stream.Length;
        using var sha = SHA256.Create();
        return Convert.ToHexStringLower(sha.ComputeHash(stream));
    }
}
