using System.IO.Compression;

namespace SnapZap.Core.Stego;

/// <summary>
/// Zips the selected photos before encryption and unzips the recovered payload after decryption.
/// Static, in-memory (<see cref="MemoryStream"/>-backed), same isolation style as
/// <see cref="Scanning.Hasher"/>. Cannot call <see cref="Export.DestinationPlanner.Resolve"/> for
/// collision-safe naming — that logic is coupled to <c>ImageRecord</c>/content-hash, which no
/// arbitrary zip entry has — so this replicates the same <c>"name (n).ext"</c> strategy locally.
/// </summary>
public static class PayloadZipper
{
    // Extract decrypts and unzips decoder-untrusted-origin data ("a PNG someone gave you") — a
    // small crafted entry could otherwise decompress to gigabytes and fill the disk with no way
    // to interrupt it mid-entry. These are generous sanity ceilings, not a real protocol limit:
    // well above anything a legitimate Hide (capped at 500 MB of source photos) could produce.
    const long MaxEntryBytes = 2_000_000_000;
    const long MaxTotalBytes = 4_000_000_000;
    const int CopyBufferSize = 81920; // matches Stream.CopyTo's own default buffer size

    public static byte[] CreateZip(IReadOnlyList<string> filePaths, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in filePaths)
            {
                ct.ThrowIfCancellationRequested();
                var entryName = UniqueName(Path.GetFileName(path), usedNames.Contains);
                usedNames.Add(entryName);
                var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                using var fileStream = File.OpenRead(path);
                fileStream.CopyTo(entryStream);
            }
        }
        return ms.ToArray();
    }

    /// <summary><paramref name="maxEntryBytes"/>/<paramref name="maxTotalBytes"/> default to
    /// generous sanity ceilings (see the class-level constants) — exposed as parameters so tests
    /// can verify the cap logic itself without needing to actually construct gigabytes of data.</summary>
    public static IReadOnlyList<string> ExtractZip(
        byte[] zipBytes, string outputDir, CancellationToken ct = default,
        long maxEntryBytes = MaxEntryBytes, long maxTotalBytes = MaxTotalBytes)
    {
        Directory.CreateDirectory(outputDir);
        var written = new List<string>();

        using var ms = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        long totalBytesWritten = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();

            // entry.Name (not FullName) already strips any directory portion, so this can't
            // escape outputDir — but a directory-only entry (FullName ends in '/') or a name with
            // characters invalid on this OS would otherwise reach File.Create and throw an
            // uncaught IOException/ArgumentException/UnauthorizedAccessException. Corrupted or
            // hostile zip content is decrypted, untrusted-origin data (extract explicitly accepts
            // "a PNG someone gave you"), so fail with a clear message instead.
            if (entry.Name.Length == 0 || entry.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new StegoException($"Hidden data is corrupted: invalid entry name \"{entry.Name}\".");

            var destName = UniqueName(entry.Name, name => File.Exists(Path.Combine(outputDir, name)));
            var destPath = Path.Combine(outputDir, destName);
            using (var entryStream = entry.Open())
            using (var fileStream = File.Create(destPath))
                totalBytesWritten += CopyWithLimits(entryStream, fileStream, totalBytesWritten, maxEntryBytes, maxTotalBytes, ct);
            written.Add(destPath);
        }
        return written;
    }

    /// <summary>Copies <paramref name="source"/> to <paramref name="destination"/> in bounded
    /// chunks, checking cancellation and both size ceilings after every chunk — not just once per
    /// whole entry, which for a hostile entry could mean an uninterruptible multi-gigabyte write.
    /// Returns the number of bytes actually copied for this entry.</summary>
    static long CopyWithLimits(
        Stream source, Stream destination, long bytesWrittenSoFar,
        long maxEntryBytes, long maxTotalBytes, CancellationToken ct)
    {
        var buffer = new byte[CopyBufferSize];
        long entryBytes = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            entryBytes += read;
            if (entryBytes > maxEntryBytes || bytesWrittenSoFar + entryBytes > maxTotalBytes)
                throw new StegoException("Hidden data is corrupted: an entry decompresses to an implausible size.");
            destination.Write(buffer, 0, read);
        }
        return entryBytes;
    }

    /// <summary>Same <c>"name (n).ext"</c> avoidance convention as
    /// <see cref="Export.DestinationPlanner.Resolve"/>, generalized behind an <paramref name="exists"/>
    /// probe so it works against either an in-memory name set (zip-side) or the filesystem
    /// (extract-side).</summary>
    static string UniqueName(string name, Func<string, bool> exists)
    {
        if (!exists(name)) return name;

        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int n = 2; n < 10000; n++)
        {
            var candidate = $"{stem} ({n}){ext}";
            if (!exists(candidate)) return candidate;
        }
        throw new IOException($"could not resolve a non-colliding name for {name}");
    }
}
