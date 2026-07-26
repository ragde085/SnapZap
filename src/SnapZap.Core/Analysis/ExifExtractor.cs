using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using Directory = MetadataExtractor.Directory;

namespace SnapZap.Core.Analysis;

public sealed record ExifInfo(long? TakenUnixUtc, string? Camera);

/// <summary>
/// Pulls capture date and camera from EXIF via MetadataExtractor (Apache-2.0). Best-effort:
/// missing or malformed metadata yields nulls (the app groups those under "undated").
/// </summary>
public static class ExifExtractor
{
    public static ExifInfo Read(string imagePath)
    {
        try
        {
            var dirs = ImageMetadataReader.ReadMetadata(imagePath);
            return new ExifInfo(ReadTakenUtc(dirs), ReadCamera(dirs));
        }
        catch
        {
            return new ExifInfo(null, null);
        }
    }

    static long? ReadTakenUtc(IReadOnlyList<Directory> dirs)
    {
        var subIfd = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        // DateTimeOriginal is the capture time; fall back to DateTimeDigitized.
        if (subIfd is not null)
        {
            if (subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dto))
                return new DateTimeOffset(DateTime.SpecifyKind(dto, DateTimeKind.Utc)).ToUnixTimeSeconds();
            if (subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeDigitized, out var dtd))
                return new DateTimeOffset(DateTime.SpecifyKind(dtd, DateTimeKind.Utc)).ToUnixTimeSeconds();
        }
        // Last resort: the IFD0 DateTime (often file-modified within the camera).
        var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
        if (ifd0 is not null && ifd0.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var dt))
            return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)).ToUnixTimeSeconds();
        return null;
    }

    static string? ReadCamera(IReadOnlyList<Directory> dirs)
    {
        var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
        if (ifd0 is null) return null;
        var make = ifd0.GetDescription(ExifDirectoryBase.TagMake)?.Trim();
        var model = ifd0.GetDescription(ExifDirectoryBase.TagModel)?.Trim();
        if (string.IsNullOrEmpty(make) && string.IsNullOrEmpty(model)) return null;
        // Avoid "Canon Canon EOS…" duplication when model already contains the make.
        if (!string.IsNullOrEmpty(make) && !string.IsNullOrEmpty(model) &&
            model!.StartsWith(make!, StringComparison.OrdinalIgnoreCase))
            return model;
        return string.Join(' ', new[] { make, model }.Where(s => !string.IsNullOrEmpty(s)));
    }
}
