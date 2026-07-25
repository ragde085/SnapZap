using SnapZap.Core.Scanning;

namespace SnapZap.Core.Export;

/// <summary>
/// Computes the destination path for an image under the chosen structure, and resolves
/// collisions (DESIGN §6). Pure and file-system-aware only through two injected probes, so
/// the collision logic is unit-testable without touching disk.
/// </summary>
public sealed class DestinationPlanner(
    string destinationRoot,
    ExportStructure structure,
    string sourceRoot,
    Func<string, bool> exists,
    Func<string, string> hashOf)
{
    /// <summary>The directory (no filename) an image belongs in under the chosen structure.</summary>
    public string DirectoryFor(ImageRecord img)
    {
        switch (structure)
        {
            case ExportStructure.Flat:
                return destinationRoot;

            case ExportStructure.Mirror:
                var rel = RelativeDir(img.Path, sourceRoot);
                return Path.Combine(destinationRoot, rel);

            case ExportStructure.Date:
                if (img.ExifTaken is { } t)
                {
                    var d = DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime;
                    return Path.Combine(destinationRoot, d.ToString("yyyy"), d.ToString("yyyy-MM"));
                }
                // No usable capture date → explicit 'undated' rather than guessing from mtime.
                return Path.Combine(destinationRoot, "undated");

            default:
                return destinationRoot;
        }
    }

    /// <summary>
    /// Final collision-resolved path for an image, plus whether it should be skipped because
    /// an identical file (same content hash) already sits at the intended path.
    /// </summary>
    public (string path, bool skipIdentical) Resolve(ImageRecord img)
    {
        var dir = DirectoryFor(img);
        var name = Path.GetFileNameWithoutExtension(img.Path);
        var ext = Path.GetExtension(img.Path);
        var candidate = Path.Combine(dir, name + ext);

        if (!exists(candidate))
            return (candidate, false);

        // Something is already there. If it's the same content, this is dedup working — skip.
        if (hashOf(candidate) == img.ContentHash)
            return (candidate, true);

        // Different content: never overwrite. Find "name (n).ext", reusing a slot that already
        // holds an identical copy (skip) and stopping at the first free slot.
        for (int n = 2; n < 10000; n++)
        {
            var alt = Path.Combine(dir, $"{name} ({n}){ext}");
            if (!exists(alt)) return (alt, false);
            if (hashOf(alt) == img.ContentHash) return (alt, true);
        }
        throw new IOException($"could not resolve a non-colliding name for {img.Path}");
    }

    static string RelativeDir(string filePath, string root)
    {
        var full = Path.GetFullPath(filePath);
        var baseDir = Path.GetFullPath(root);
        var relFile = Path.GetRelativePath(baseDir, full);
        // If the file isn't actually under root, fall back to just its parent name.
        if (relFile.StartsWith("..") || Path.IsPathRooted(relFile))
            return "";
        return Path.GetDirectoryName(relFile) ?? "";
    }
}
