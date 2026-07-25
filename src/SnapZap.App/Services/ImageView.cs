using SnapZap.Core;

namespace SnapZap.App.Services;

/// <summary>Resolved duplicate-group membership for one image (mirrors the JS `dupeOf` map entry).</summary>
public sealed record DupeInfo(long GroupId, DupeKind Kind, bool IsKeeper);

/// <summary>An <see cref="ImageRecord"/> plus the view-only fields components need: thumb/full
/// URLs, resolved dupe info, and folder/year computed from the path/EXIF.</summary>
public sealed record ImageView(ImageRecord Record, DupeInfo? Dupe)
{
    /// <summary>Laplacian variance below this reads as soft enough to flag on the thumbnail.</summary>
    public const double BlurFlagThreshold = 100;

    public long Id => Record.Id;
    public string Path => Record.Path;
    public long FileSize => Record.FileSize;
    public int? Width => Record.Width;
    public int? Height => Record.Height;
    public double? NsfwScore => Record.NsfwScore;
    public double? BlurScore => Record.BlurScore;
    public long? ExifTaken => Record.ExifTaken;
    public string? ExifCamera => Record.ExifCamera;

    public string ThumbUrl => $"/api/thumb/{Record.ContentHash}";
    public string FullUrl => $"/api/full/{Id}";

    public string FileName => System.IO.Path.GetFileName(Path);
    public string Folder => FolderOf(Path);

    public int? Year => ExifTaken is { } t
        ? DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime.Year
        : null;

    public bool IsBlurry => BlurScore is { } b && b <= BlurFlagThreshold;

    public string Dimensions => Width is { } w && Height is { } h ? $"{w} × {h}" : "unknown";

    public string SizeLabel => FileSize switch
    {
        >= 1_000_000_000 => $"{FileSize / 1e9:F1} GB",
        >= 1_000_000 => $"{FileSize / 1e6:F1} MB",
        _ => $"{FileSize / 1e3:F0} KB",
    };

    public string CapturedLabel => ExifTaken is { } t
        ? DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime.ToString("yyyy-MM-dd")
        : "no date in EXIF";

    public static string FolderOf(string path)
    {
        var norm = path.Replace('\\', '/');
        var i = norm.LastIndexOf('/');
        return i < 0 ? "" : norm[..i];
    }

    /// <summary>Last two path segments — enough to identify a folder without the full prefix.</summary>
    public static string ShortFolder(string folder)
    {
        var parts = folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 2 ? folder : ".../" + string.Join('/', parts[^2..]);
    }
}
