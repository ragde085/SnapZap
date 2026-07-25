using System.Collections.Concurrent;
using SnapZap.Core.Analysis;
using SnapZap.Core.Data;
using SnapZap.Core.Imaging;

namespace SnapZap.Core.Scanning;

public sealed record ScanProgress(int Seen, int Analyzed, int Cached, int Failed, string? Current);

/// <summary>One file the scan could not take, and why. Attributing the reason matters more
/// than the count: "3 failed" is not actionable, "3 unreadable HEIC files in /2021" is.</summary>
public sealed record ScanFailure(string Path, string Reason)
{
    public string Folder
    {
        get
        {
            var norm = Path.Replace('\\', '/');
            var i = norm.LastIndexOf('/');
            return i < 0 ? "" : norm[..i];
        }
    }
}

public sealed record ScanResult(int Total, int Analyzed, int Cached, int Failed, int Pruned, long ElapsedMs)
{
    /// <summary>Non-positional so existing callers and tests are unaffected.</summary>
    public IReadOnlyList<ScanFailure> Failures { get; init; } = [];

    /// <summary>Images whose extension we accept but that no decoder would open — usually the
    /// real explanation for a surprising "failed" count.</summary>
    public int Undecodable { get; init; }

    /// <summary>Recognised image formats SnapZap cannot decode yet, counted by format
    /// (HEIC, CR2…). Not failures — these were never attempted.</summary>
    public IReadOnlyDictionary<string, int> UnsupportedByFormat { get; init; }
        = new Dictionary<string, int>();

    public int UnsupportedTotal => UnsupportedByFormat.Values.Sum();
}

/// <summary>
/// Walks a folder, and for each image either reuses the cached row (tier-1 probe hit) or
/// runs full analysis: hash + dimensions + thumbnail (DESIGN §4/§5). NSFW/blur/EXIF signals
/// are layered on in later steps; this stage populates identity + thumbnail.
/// </summary>
public sealed class Scanner(Database db, SkiaImageService imaging, string thumbDir)
{
    readonly BlurDetector _blur = new(imaging);

    static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".tif", ".tiff" };

    /// <summary>
    /// Image formats we recognise but cannot decode: SkiaSharp has no HEIC/AVIF or camera-RAW
    /// codec. These are counted and reported rather than ignored — a library shot on an iPhone
    /// is mostly HEIC, and silently returning "0 photos" would look like the app was broken.
    /// </summary>
    static readonly HashSet<string> UnsupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".heic", ".heif", ".avif",                                  // modern phone formats
        ".cr2", ".cr3", ".nef", ".arw", ".dng", ".orf", ".rw2", ".raf", ".srw", ".pef", // camera RAW
    };

    public int Parallelism { get; init; } = Math.Max(2, Environment.ProcessorCount - 1);

    public async Task<ScanResult> ScanAsync(
        string root,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        var start = Environment.TickCount64;
        var (files, unsupported) = Enumerate(root);

        int seen = 0, analyzed = 0, cached = 0, failed = 0, undecodable = 0;
        var livePaths = new ConcurrentBag<string>();
        var failures = new ConcurrentBag<ScanFailure>();

        // The DB writer is single-threaded (SQLite): analysis fans out, writes funnel through a lock.
        var repo = new ImageRepository(db);
        var writeLock = new object();

        await Parallel.ForEachAsync(
            files,
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
            async (file, token) =>
            {
                Interlocked.Increment(ref seen);
                livePaths.Add(file.FullName);
                try
                {
                    long mtime = new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeSeconds();

                    // Tier-1 probe: unchanged + already analyzed → skip all I/O beyond the dir entry.
                    (long id, long size, long m, bool an)? probe;
                    lock (writeLock) probe = repo.Probe(file.FullName);
                    if (probe is { } p && p.an && p.size == file.Length && p.m == mtime)
                    {
                        Interlocked.Increment(ref cached);
                        Report(progress, seen, analyzed, cached, failed, file.Name);
                        return;
                    }

                    var rec = await Task.Run(() => Analyze(file, mtime), token);
                    if (rec is null)
                    {
                        // Probe returns null for *any* failure, so distinguish "we couldn't read
                        // the bytes" from "the bytes aren't an image" — otherwise a permissions
                        // problem is misreported as a corrupt file and the user looks in the
                        // wrong place.
                        Interlocked.Increment(ref failed);
                        var reason = ClassifyUnreadable(file);
                        if (reason is null) Interlocked.Increment(ref undecodable);
                        failures.Add(new ScanFailure(file.FullName, reason ?? "not a decodable image"));
                        return;
                    }

                    lock (writeLock) repo.Upsert(rec);
                    Interlocked.Increment(ref analyzed);
                    Report(progress, seen, analyzed, cached, failed, file.Name);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Record why. Swallowing the exception left a bare count that told the user
                    // nothing about which files to go and look at.
                    Interlocked.Increment(ref failed);
                    failures.Add(new ScanFailure(file.FullName, Describe(ex)));
                }
            });

        int pruned;
        lock (writeLock) pruned = repo.DeleteMissing(livePaths.ToArray());

        return new ScanResult(files.Count, analyzed, cached, failed, pruned,
                              Environment.TickCount64 - start)
        {
            Failures = failures.OrderBy(f => f.Path, StringComparer.Ordinal).ToList(),
            Undecodable = undecodable,
            UnsupportedByFormat = unsupported,
        };
    }

    /// <summary>Why the bytes couldn't be read, or null when the file is readable and the
    /// problem really is the image data. Only runs on the failure path.</summary>
    static string? ClassifyUnreadable(FileInfo file)
    {
        try
        {
            using var fs = file.OpenRead();
            fs.ReadByte();
            return null; // readable, so it genuinely isn't a decodable image
        }
        catch (UnauthorizedAccessException) { return "permission denied"; }
        catch (FileNotFoundException) { return "file disappeared during the scan"; }
        catch (IOException io) { return $"could not be read ({io.Message})"; }
        catch { return null; }
    }

    /// <summary>A short, human-readable cause — the exception type alone is noise to a user.</summary>
    static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "permission denied",
        FileNotFoundException => "file disappeared during the scan",
        DirectoryNotFoundException => "folder disappeared during the scan",
        IOException io => $"could not be read ({io.Message})",
        _ => ex.Message,
    };

    ImageRecord? Analyze(FileInfo file, long mtime)
    {
        var info = imaging.Probe(file.FullName);
        if (info is null) return null; // not a decodable image

        var hash = Hasher.HashFile(file.FullName, out var length);
        var thumbPath = Path.Combine(thumbDir, hash[..2], hash + ".jpg");
        // Thumbnail generation is best-effort: a failure here must not drop the catalog row,
        // which carries the far more important identity + signal data.
        bool haveThumb = File.Exists(thumbPath);
        if (!haveThumb)
        {
            try { haveThumb = imaging.WriteThumbnail(file.FullName, thumbPath); }
            catch { haveThumb = false; }
        }

        // Blur + EXIF are cheap enough to compute in the same pass (one scan, four signals).
        // NSFW is the exception — it runs as a separate, heavier pass. Both are best-effort.
        double? blur = null;
        try { blur = _blur.Score(file.FullName); } catch { }
        ExifInfo exif;
        try { exif = ExifExtractor.Read(file.FullName); } catch { exif = new ExifInfo(null, null); }

        return new ImageRecord
        {
            Path = file.FullName,
            ContentHash = hash,
            FileSize = length,
            Mtime = mtime,
            Width = info.Width,
            Height = info.Height,
            Format = info.Format,
            BlurScore = blur,
            ExifTaken = exif.TakenUnixUtc,
            ExifCamera = exif.Camera,
            ThumbPath = haveThumb ? thumbPath : null,
            AnalyzedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
    }

    static void Report(IProgress<ScanProgress>? p, int seen, int a, int c, int f, string cur)
        => p?.Report(new ScanProgress(seen, a, c, f, cur));

    /// <summary>
    /// One walk, two buckets: files we can analyse, and a tally of recognised-but-undecodable
    /// formats by extension so the caller can say what was left behind.
    /// </summary>
    static (List<FileInfo> Supported, Dictionary<string, int> Unsupported) Enumerate(string root)
    {
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
        };

        var supported = new List<FileInfo>();
        var unsupported = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(root, "*", opts))
        {
            var ext = Path.GetExtension(path);
            if (Extensions.Contains(ext))
                supported.Add(new FileInfo(path));
            else if (UnsupportedExtensions.Contains(ext))
                unsupported[ext.TrimStart('.').ToUpperInvariant()] =
                    unsupported.GetValueOrDefault(ext.TrimStart('.').ToUpperInvariant()) + 1;
        }
        return (supported, unsupported);
    }
}
