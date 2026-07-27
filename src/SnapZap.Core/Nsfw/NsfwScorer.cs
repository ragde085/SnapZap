using Microsoft.Data.Sqlite;
using SnapZap.Core.Data;

namespace SnapZap.Core.Nsfw;

public sealed record NsfwProgress(int Done, int Total, string? Current);
public sealed record NsfwRunResult(bool ModelAvailable, int Scored, int Failed, string? Note);

/// <summary>
/// Batch-scores catalog images for NSFW and writes <c>nsfw_score</c>. Skips rows already
/// scored so re-runs are cheap. If the model file is missing, returns unavailable without
/// touching the catalog (graceful degrade).
/// </summary>
public sealed class NsfwScorer(Database db, string modelPath)
{
    /// <param name="root">
    /// Score only photos beneath this folder; null scores the whole catalogue. Ignored when
    /// <paramref name="imageIds"/> is supplied. The catalogue spans every folder ever scanned,
    /// so an unscoped run on a four-photo folder went off and scored thousands of photos the
    /// user could not see and had not asked about.
    /// </param>
    /// <param name="imageIds">
    /// Score exactly these images (e.g. the current selection, or everything under the folder
    /// focused in the tree) instead of a path scope. Takes precedence over <paramref name="root"/>.
    /// </param>
    /// <param name="depth">
    /// How hard to look. <see cref="NsfwDepth.Tiled"/> costs ten inferences per photo instead of
    /// one and is what finds a subject that occupies only part of the frame.
    /// </param>
    public async Task<NsfwRunResult> ScoreAllAsync(
        string? root = null,
        IProgress<NsfwProgress>? progress = null,
        bool rescoreExisting = false,
        IReadOnlyList<long>? imageIds = null,
        NsfwDepth depth = NsfwDepth.Tiled,
        CancellationToken ct = default)
    {
        if (!File.Exists(modelPath))
            return new NsfwRunResult(false, 0, 0, $"NSFW model not found at {modelPath} — scoring skipped.");

        var targets = CollectTargets(root, imageIds, rescoreExisting, depth);

        if (targets.Count == 0)
            return new NsfwRunResult(true, 0, 0, "All images already scored.");

        int done = 0, failed = 0;
        // Off-thread: constructing the session reads and prepares ~328 MB, and on the caller's
        // context that is a multi-second freeze with no progress bar moving.
        using var clf = await Task.Run(() => new OnnxNsfwClassifier(modelPath), ct);

        // The prepared command is reused across the whole run, but every touch of it — including
        // building it — happens under db.WriteLock, per Database.Writer's own contract ("never
        // touch this without holding WriteLock"). Only ScoreFile below (the expensive part) runs
        // unlocked, so it never blocks other writers.
        SqliteCommand update;
        SqliteParameter ps, pt, pid;
        lock (db.WriteLock)
        {
            update = db.Writer.CreateCommand();
            update.CommandText = "UPDATE images SET nsfw_score=$s, nsfw_tile_mean=$t WHERE id=$id";
            ps = update.Parameters.Add("$s", SqliteType.Real);
            pt = update.Parameters.Add("$t", SqliteType.Real);
            pid = update.Parameters.Add("$id", SqliteType.Integer);
        }
        using var _ = update;

        // Scored a few images at a time rather than one, and rather than all of them.
        //
        // ONNX Runtime already spreads a single Run across cores, so concurrency here is not free
        // parallelism — measured on 12 cores it takes 93 ms/inference to 59 ms, and more than
        // about four workers made it worse again by fighting ORT's own thread pool. Four is where
        // that measurement levelled off. InferenceSession.Run is documented thread-safe; the
        // SQLite write stays behind WriteLock as everywhere else in this codebase.
        //
        // It matters more than it used to: a tiled run is ten inferences per photo.
        var options = new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct };
        await Parallel.ForEachAsync(targets, options, async (target, token) =>
        {
            var (id, path) = target;
            try
            {
                var verdict = await Task.Run(() => clf.ScoreFile(path, depth), token);
                lock (db.WriteLock)
                {
                    ps.Value = verdict.Whole;
                    pt.Value = (object?)verdict.TileMean ?? DBNull.Value;
                    pid.Value = id;
                    update.ExecuteNonQuery();
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { Interlocked.Increment(ref failed); }

            // Counted before reporting, and deliberately not inside the Report argument: `?.`
            // short-circuits the whole expression when there is no progress sink, so an
            // Interlocked.Increment written there silently stops counting whenever the caller
            // passes no IProgress — which is every caller except the UI, including the tests.
            int n = Interlocked.Increment(ref done);

            // Reported from whichever worker finished, so the count climbs monotonically while
            // the filename is simply the most recent one — it was never a promise of ordering.
            progress?.Report(new NsfwProgress(n, targets.Count, Path.GetFileName(path)));
        });

        return new NsfwRunResult(true, done - failed, failed,
            clf.ConfigFromFile ? null : "⚠ preprocessor_config.json not found — using Falconsai defaults.");
    }

    /// <summary>Split out of <see cref="ScoreAllAsync"/> so the id-vs-path scoping and the
    /// already-scored filter can be tested without loading the ONNX model.</summary>
    /// <remarks>
    /// A row already scored whole-frame is still owed work by a tiled run, which is the whole
    /// reason <c>nsfw_tile_mean</c> is nullable rather than defaulted: without that distinction,
    /// switching to a deeper setting would silently leave every previously-scored photo on the
    /// shallower answer and the new setting would appear to do nothing.
    /// </remarks>
    internal List<(long id, string path)> CollectTargets(
        string? root, IReadOnlyList<long>? imageIds, bool rescoreExisting,
        NsfwDepth depth = NsfwDepth.Tiled)
    {
        bool Pending(ImageRecord r) =>
            r.NsfwScore is null || (depth == NsfwDepth.Tiled && r.NsfwTileMean is null);

        if (imageIds is not null)
        {
            // Explicit id scope (selection / current folder) rather than a path prefix — reuses
            // the chunked id lookup so a large selection can't blow past SQLite's parameter cap.
            return new ImageRepository(db).ByIds(imageIds)
                .Where(r => rescoreExisting || Pending(r))
                .Select(r => (r.Id, r.Path))
                .ToList();
        }

        var pending = depth == NsfwDepth.Tiled
            ? "(nsfw_score IS NULL OR nsfw_tile_mean IS NULL)"
            : "nsfw_score IS NULL";

        var targets = new List<(long id, string path)>();
        using var c = db.OpenRead();
        using var cmd = c.CreateCommand();
        cmd.CommandText = rescoreExisting
            ? $"SELECT id, path FROM images WHERE {PathScope.Where(root)}"
            : $"SELECT id, path FROM images WHERE {pending} AND {PathScope.Where(root)}";
        PathScope.Bind(cmd, root);
        using var r = cmd.ExecuteReader();
        while (r.Read()) targets.Add((r.GetInt64(0), r.GetString(1)));
        return targets;
    }
}
