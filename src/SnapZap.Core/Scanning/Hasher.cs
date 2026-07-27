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
    /// <summary>1 MiB buffer, <c>SequentialScan</c> hint: the OS read-ahead this enables matters
    /// more than the buffer size itself for a hash that reads the whole file once, start to end.</summary>
    static FileStream OpenSequential(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);

    public static string HashFile(string path)
    {
        using var stream = OpenSequential(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static string HashFile(string path, out long length)
    {
        using var stream = OpenSequential(path);
        length = stream.Length;
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
