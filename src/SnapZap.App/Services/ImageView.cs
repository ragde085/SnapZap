using SnapZap.Core;

namespace SnapZap.App.Services;

/// <summary>Resolved duplicate-group membership for one image (mirrors the JS `dupeOf` map entry).</summary>
public sealed record DupeInfo(long GroupId, DupeKind Kind, bool IsKeeper);

/// <summary>An <see cref="ImageRecord"/> plus the view-only fields components need: thumb/full
/// URLs, resolved dupe info, and folder/year computed from the path/EXIF.</summary>
public sealed record ImageView(ImageRecord Record, DupeInfo? Dupe)
{
    /// <summary>Laplacian variance below this reads as soft enough to flag on the thumbnail,
    /// used when the user hasn't set their own threshold on the filter slider. Read it through
    /// <c>AppState.SoftThreshold</c> — a second, independent notion of "blurry" living on the
    /// view model is what made the badge disagree with the filter that selected the photo.</summary>
    public const double BlurFlagThreshold = 100;

    /// <summary>
    /// P(nsfw) at or above which a photo is called explicit. Read it through
    /// <c>AppState.NsfwThreshold</c>, for the same reason as the blur one above: a second,
    /// independent notion of "explicit" is what let the preview say "below threshold" about a
    /// photo the filter had just decided was over it.
    /// </summary>
    public const double NsfwFlagThreshold = 0.85;

    /// <summary>
    /// Below this the model is confident enough to leave the photo alone. Between the two the
    /// answer is "look at it yourself" — the model's output is bimodal, so this middle band is
    /// small by design, and small is the point: it is a review queue, not a category.
    /// </summary>
    public const double NsfwUnsureThreshold = 0.20;

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

    public string Dimensions => Width is { } w && Height is { } h ? $"{w} × {h}" : "unknown";

    /// <summary>Delegates so there is one byte formatter, not two that drift apart.</summary>
    public string SizeLabel => AppState.FormatBytes(FileSize);

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
