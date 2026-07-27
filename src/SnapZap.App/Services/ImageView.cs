using SnapZap.Core;
using SnapZap.Core.Dedup;
using SnapZap.Core.Nsfw;

namespace SnapZap.App.Services;

/// <summary>Resolved duplicate-group membership for one image (mirrors the JS `dupeOf` map entry).</summary>
public sealed record DupeInfo(long GroupId, DupeKind Kind, bool IsKeeper);

/// <summary>An <see cref="ImageRecord"/> plus the view-only fields components need: thumb/full
/// URLs, resolved dupe info, and folder/year computed from the path/EXIF.</summary>
/// <param name="ThumbGeneration">
/// The cached thumbnail file's own last-write time (ticks), stamped once per <c>LoadAsync</c>
/// snapshot — see <see cref="ThumbUrl"/>. Zero when there is no thumbnail file to stat.
/// </param>
public sealed record ImageView(ImageRecord Record, DupeInfo? Dupe, long ThumbGeneration = 0)
{
    /// <summary>Laplacian variance below this reads as soft enough to flag on the thumbnail,
    /// used when the user hasn't set their own threshold on the filter slider. Read it through
    /// <c>AppState.SoftThreshold</c> — a second, independent notion of "blurry" living on the
    /// view model is what made the badge disagree with the filter that selected the photo.</summary>
    public const double BlurFlagThreshold = 100;

    /// <summary>
    /// Whole-frame P(nsfw) at or above which a photo is called explicit on that evidence alone.
    /// Read it through <c>AppState.NsfwThreshold</c>, for the same reason as the blur one above:
    /// a second, independent notion of "explicit" is what let the preview say "below threshold"
    /// about a photo the filter had just decided was over it.
    ///
    /// <para>It is no longer the whole rule — a tiled score can flag a photo this leaves alone —
    /// so anything deciding whether a photo is explicit must ask <see cref="NsfwDecision"/>, not
    /// compare against this.</para>
    /// </summary>
    public const double NsfwFlagThreshold = NsfwDecision.WholeFlagThreshold;

    /// <summary>
    /// Below this the model is confident enough to leave the photo alone. Between here and a
    /// flag the answer is "look at it yourself" — a review queue, not a category.
    /// </summary>
    public const double NsfwUnsureThreshold = NsfwDecision.UnsureThreshold;

    public long Id => Record.Id;
    public string Path => Record.Path;
    public long FileSize => Record.FileSize;
    public int? Width => Record.Width;
    public int? Height => Record.Height;
    public double? NsfwScore => Record.NsfwScore;

    /// <summary>Mean score over the 3×3 tiles; null when this photo was scored whole-frame only.</summary>
    public double? NsfwTileMean => Record.NsfwTileMean;

    /// <summary>
    /// The single number to put in front of a person. Always the one the flag decision was
    /// actually made on, so the UI can never show "0.40 of 1.00 · flagged at 0.85" next to a
    /// photo it has just flagged.
    /// </summary>
    public double? NsfwDisplayScore => NsfwDecision.DisplayScore(Record.NsfwScore, Record.NsfwTileMean);
    public double? BlurScore => Record.BlurScore;
    public long? ExifTaken => Record.ExifTaken;
    public string? ExifCamera => Record.ExifCamera;

    /// <summary>
    /// The query parameter busts the browser's one-year <c>immutable</c> cache header (Task 2)
    /// whenever the thumbnail file is regenerated in place — content-addressed by hash, which does
    /// not change when orientation handling does, so without this the cache header would defeat
    /// Task 16's regeneration and browsers would keep serving the stale sideways image.
    /// </summary>
    /// <remarks>
    /// Keyed on the thumbnail file's own <see cref="ThumbGeneration"/> (its last-write time), not
    /// a compile-time recipe-version constant. A global constant only busts the cache across an
    /// app-version boundary — the lazy signature backfill (<c>DuplicateService</c>) can rewrite a
    /// thumbnail mid-session, at the same app version, the moment "Find duplicates" is next run,
    /// and a static constant would leave anyone who had already fetched that URL this session
    /// stuck on the stale image for up to a year.
    /// </remarks>
    public string ThumbUrl => $"/api/thumb/{Record.ContentHash}?v={ThumbGeneration}";
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
