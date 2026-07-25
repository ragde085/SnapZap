using System.Collections.Concurrent;
using SnapZap.Core.Analysis;
using SnapZap.Core.Data;
using SnapZap.Core.Imaging;

namespace SnapZap.Core.Scanning;

public sealed record ScanProgress(int Seen, int Analyzed, int Cached, int Failed, string? Current);

public sealed record ScanResult(int Total, int Analyzed, int Cached, int Failed, int Pruned, long ElapsedMs);

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

    public int Parallelism { get; init; } = Math.Max(2, Environment.ProcessorCount - 1);

    public async Task<ScanResult> ScanAsync(
        string root,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        var start = Environment.TickCount64;
        var files = EnumerateImages(root).ToList();

        int seen = 0, analyzed = 0, cached = 0, failed = 0;
        var livePaths = new ConcurrentBag<string>();

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
                    if (rec is null) { Interlocked.Increment(ref failed); return; }

                    lock (writeLock) repo.Upsert(rec);
                    Interlocked.Increment(ref analyzed);
                    Report(progress, seen, analyzed, cached, failed, file.Name);
                }
                catch (OperationCanceledException) { throw; }
                catch { Interlocked.Increment(ref failed); }
            });

        int pruned;
        lock (writeLock) pruned = repo.DeleteMissing(livePaths.ToArray());

        return new ScanResult(files.Count, analyzed, cached, failed, pruned,
                              Environment.TickCount64 - start);
    }

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

    static IEnumerable<FileInfo> EnumerateImages(string root)
    {
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
        };
        foreach (var path in Directory.EnumerateFiles(root, "*", opts))
            if (Extensions.Contains(Path.GetExtension(path)))
                yield return new FileInfo(path);
    }
}
