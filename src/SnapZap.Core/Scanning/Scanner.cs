using System.Collections.Concurrent;
using SnapZap.Core.Analysis;
using SnapZap.Core.Data;
using SnapZap.Core.Dedup;
using SnapZap.Core.Imaging;

namespace SnapZap.Core.Scanning;

public sealed record ScanProgress(int Seen, int Analyzed, int Cached, int Failed, string? Current, int Total);

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

    /// <summary>Both the thumbnail (320) and the greyscale buffer (512) are derived from one
    /// decode at the larger of the two, so decoding never happens twice for one file.</summary>
    int DecodeMaxEdge => Math.Max(imaging.ThumbMaxEdge, _blur.WorkEdge);

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

    /// <summary>Rows accumulated between batch flushes (Task 19). ~500 rows per transaction was
    /// measured at 10.8x over one autocommit statement per row (20k rows: 998ms -> 92ms).</summary>
    const int WriteBatchSize = 500;

    /// <summary>At most ~10 progress reports per second — one <c>IProgress.Report</c> per image
    /// used to marshal across the Blazor circuit up to 40k times per scan. Counters stay exact;
    /// only notification rate changes.</summary>
    const long ReportIntervalMs = 100;
    long _lastReportTicks;

    /// <param name="settings">
    /// Scan options. Defaults to whatever the catalogue has stored, so both heads pick up a change
    /// without threading it through their own call sites; passed explicitly only by tests, which
    /// need to reach both branches without writing to <c>meta</c> first.
    /// </param>
    public async Task<ScanResult> ScanAsync(
        string root,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default,
        ScanSettings? settings = null)
    {
        var start = Environment.TickCount64;
        var opts = settings ?? ScanSettings.Load(db);
        // Off the calling thread (the Blazor circuit's sync context) and cancellable per file:
        // the walk over a large/wrong folder used to block Stop from even being dispatched,
        // let alone honored, until the whole tree had been enumerated.
        var (files, unsupported) = await Task.Run(() => Enumerate(root, ct, opts.SkipHidden), ct);
        _lastReportTicks = 0;

        int seen = 0, analyzed = 0, cached = 0, failed = 0, undecodable = 0;
        var livePaths = new ConcurrentBag<string>();
        var failures = new ConcurrentBag<ScanFailure>();

        var repo = new ImageRepository(db);

        // Bulk-loaded once, rather than a single-row SELECT under a lock per file: the larger
        // benefit is removing lock contention from the cache-miss path, where workers used to
        // queue on the same mutex before they could even begin decoding (Task 20).
        var probes = repo.ProbeMap(root);

        // Completed rows accumulate here and flush in batches of WriteBatchSize via a prepared,
        // transaction-batched statement (Task 19), instead of one autocommit INSERT per file.
        var pending = new List<ImageRecord>(WriteBatchSize);
        var pendingLock = new object();

        void FlushPending(List<ImageRecord> batch)
        {
            if (batch.Count == 0) return;
            repo.UpsertBatch(batch);
        }

        await Parallel.ForEachAsync(
            files,
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
            (file, token) =>
            {
                token.ThrowIfCancellationRequested();
                Interlocked.Increment(ref seen);
                livePaths.Add(file.FullName);
                try
                {
                    long mtime = new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeSeconds();

                    // Tier-1 probe: unchanged + already analyzed → skip all I/O beyond the dir entry.
                    if (probes.TryGetValue(file.FullName, out var p) &&
                        p.analyzed && p.size == file.Length && p.mtime == mtime)
                    {
                        Interlocked.Increment(ref cached);
                        Report(progress, seen, analyzed, cached, failed, file.Name, files.Count);
                        return default;
                    }

                    var rec = Analyze(file, mtime);
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
                        return default;
                    }

                    List<ImageRecord>? toFlush = null;
                    lock (pendingLock)
                    {
                        pending.Add(rec);
                        if (pending.Count >= WriteBatchSize)
                        {
                            toFlush = new List<ImageRecord>(pending);
                            pending.Clear();
                        }
                    }
                    if (toFlush is not null) FlushPending(toFlush);

                    Interlocked.Increment(ref analyzed);
                    Report(progress, seen, analyzed, cached, failed, file.Name, files.Count);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Record why. Swallowing the exception left a bare count that told the user
                    // nothing about which files to go and look at.
                    Interlocked.Increment(ref failed);
                    failures.Add(new ScanFailure(file.FullName, Describe(ex)));
                }
                return default;
            });

        // Final partial batch.
        List<ImageRecord> tail;
        lock (pendingLock) { tail = new List<ImageRecord>(pending); pending.Clear(); }
        FlushPending(tail);

        int pruned = repo.DeleteMissing(root, livePaths.ToArray());

        // Always emit the true final state, even if the last per-file report was throttled away —
        // the UI must never end mid-count.
        Report(progress, seen, analyzed, cached, failed, current: null, total: files.Count, force: true);

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
        // One decode feeds the thumbnail, the greyscale buffer, blur and the perceptual hash —
        // replacing the old Probe (header) + WriteThumbnail (full decode) + DecodeGray (full
        // decode) sequence, which paid for the pixels twice. Also applies EXIF orientation, so
        // width/height and every downstream signal describe the photo as displayed.
        using var decoded = imaging.DecodeScaled(file.FullName, DecodeMaxEdge);
        if (decoded is null) return null; // not a decodable image

        var hash = Hasher.HashFile(file.FullName, out var length);
        var thumbPath = Path.Combine(thumbDir, hash[..2], hash + ".jpg");
        // Thumbnail generation is best-effort: a failure here must not drop the catalog row,
        // which carries the far more important identity + signal data.
        bool haveThumb = File.Exists(thumbPath);
        if (!haveThumb)
        {
            try { haveThumb = imaging.WriteThumbnailFrom(decoded, thumbPath); }
            catch { haveThumb = false; }
        }

        // Blur, the perceptual signature and EXIF all ride on this pass (one scan, five signals).
        // NSFW is the exception — it runs as a separate, heavier pass. All are best-effort.
        double? blur = null;
        byte[]? phash = null;
        try
        {
            var g = SkiaImageService.GrayFrom(decoded, _blur.WorkEdge);
            blur = BlurDetector.ScoreFrom(g.gray, g.w, g.h);
            var sig = PerceptualHash.FromGray(g.gray, g.w, g.h);
            if (!sig.IsEmpty) phash = sig.ToBytes();
        }
        catch { /* a signal we could not compute is a null column, never a dropped row */ }

        ExifInfo exif;
        try { exif = ExifExtractor.Read(file.FullName); } catch { exif = new ExifInfo(null, null); }

        return new ImageRecord
        {
            Path = file.FullName,
            ContentHash = hash,
            FileSize = length,
            Mtime = mtime,
            Width = decoded.Width,
            Height = decoded.Height,
            Format = decoded.Format,
            BlurScore = blur,
            Phash = phash,
            ExifTaken = exif.TakenUnixUtc,
            ExifCamera = exif.Camera,
            ThumbPath = haveThumb ? thumbPath : null,
            AnalyzedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
    }

    void Report(IProgress<ScanProgress>? p, int seen, int a, int c, int f, string? current, int total, bool force = false)
    {
        if (p is null) return;
        if (!force)
        {
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastReportTicks);
            if (now - last < ReportIntervalMs) return;
            if (Interlocked.CompareExchange(ref _lastReportTicks, now, last) != last) return;
        }
        p.Report(new ScanProgress(seen, a, c, f, current, total));
    }

    /// <summary>
    /// One walk, two buckets: files we can analyse, and a tally of recognised-but-undecodable
    /// formats by extension so the caller can say what was left behind.
    /// </summary>
    static (List<FileInfo> Supported, Dictionary<string, int> Unsupported) Enumerate(
        string root, CancellationToken ct, bool skipHidden)
    {
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            // Hidden is added conditionally; it is deliberately absent from the base set, since the
            // .NET default (Hidden | System) is what this overrides.
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint
                               | (skipHidden ? FileAttributes.Hidden : 0),
        };

        var supported = new List<FileInfo>();
        var unsupported = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Pre-populated FileInfo straight from the directory walk (size/mtime already known),
        // instead of Directory.EnumerateFiles + a second stat via `new FileInfo(path)` per file.
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", opts))
        {
            ct.ThrowIfCancellationRequested();

            // The attribute alone is not enough, and the gap is platform-shaped. On Unix — macOS,
            // Linux, Android — .NET synthesises Hidden from a leading dot, so AttributesToSkip
            // already prunes `.thumbnails` and never recurses into it. On Windows it does not: a
            // folder literally named ".git" carries no Hidden attribute, so the walk would descend
            // into it. This second pass closes that, and costs one span scan per file.
            if (skipHidden && HasHiddenSegmentBelowRoot(file.FullName, root)) continue;

            var ext = file.Extension;
            if (Extensions.Contains(ext))
                supported.Add(file);
            else if (UnsupportedExtensions.Contains(ext))
                unsupported[ext.TrimStart('.').ToUpperInvariant()] =
                    unsupported.GetValueOrDefault(ext.TrimStart('.').ToUpperInvariant()) + 1;
        }
        return (supported, unsupported);
    }

    /// <summary>
    /// Whether any path segment strictly below <paramref name="root"/> starts with a dot.
    /// </summary>
    /// <remarks>
    /// <b>Below the root, never the root itself.</b> Scanning a folder the user explicitly chose has
    /// to work even when that folder is hidden — otherwise picking <c>~/.photos</c> in the browser
    /// yields a scan that silently finds nothing. So the comparison starts after the root prefix.
    ///
    /// Allocation-free on purpose: this runs once per file in the walk, and a 40k-photo library
    /// makes 40k of them.
    /// </remarks>
    static bool HasHiddenSegmentBelowRoot(string fullPath, string root)
    {
        var rooted = root.AsSpan().TrimEnd(Path.DirectorySeparatorChar);
        if (fullPath.Length <= rooted.Length) return false;

        var rel = fullPath.AsSpan(rooted.Length);
        // rel now begins with a separator, so every segment start is separator-prefixed and the
        // file's own leading dot is caught by the same test.
        for (int i = 0; i < rel.Length - 1; i++)
            if ((rel[i] == Path.DirectorySeparatorChar || rel[i] == Path.AltDirectorySeparatorChar)
                && rel[i + 1] == '.')
                return true;

        return false;
    }
}
