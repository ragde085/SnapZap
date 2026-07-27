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
    public async Task<NsfwRunResult> ScoreAllAsync(
        string? root = null,
        IProgress<NsfwProgress>? progress = null,
        bool rescoreExisting = false,
        IReadOnlyList<long>? imageIds = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(modelPath))
            return new NsfwRunResult(false, 0, 0, $"NSFW model not found at {modelPath} — scoring skipped.");

        // Inference is CPU-bound and the ORT session is not thread-safe for concurrent Run on
        // all providers, so we score sequentially; the SQLite writes are naturally serialized too.
        var targets = CollectTargets(root, imageIds, rescoreExisting);

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
        SqliteParameter ps, pid;
        lock (db.WriteLock)
        {
            update = db.Writer.CreateCommand();
            update.CommandText = "UPDATE images SET nsfw_score=$s WHERE id=$id";
            ps = update.Parameters.Add("$s", SqliteType.Real);
            pid = update.Parameters.Add("$id", SqliteType.Integer);
        }
        using var _ = update;

        foreach (var (id, path) in targets)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var score = await Task.Run(() => clf.ScoreFile(path), ct);
                lock (db.WriteLock)
                {
                    ps.Value = score;
                    pid.Value = id;
                    update.ExecuteNonQuery();
                }
            }
            catch { failed++; }
            done++;
            progress?.Report(new NsfwProgress(done, targets.Count, Path.GetFileName(path)));
        }

        return new NsfwRunResult(true, done - failed, failed,
            clf.ConfigFromFile ? null : "⚠ preprocessor_config.json not found — using Falconsai defaults.");
    }

    /// <summary>Split out of <see cref="ScoreAllAsync"/> so the id-vs-path scoping and the
    /// already-scored filter can be tested without loading the ONNX model.</summary>
    internal List<(long id, string path)> CollectTargets(
        string? root, IReadOnlyList<long>? imageIds, bool rescoreExisting)
    {
        if (imageIds is not null)
        {
            // Explicit id scope (selection / current folder) rather than a path prefix — reuses
            // the chunked id lookup so a large selection can't blow past SQLite's parameter cap.
            return new ImageRepository(db).ByIds(imageIds)
                .Where(r => rescoreExisting || r.NsfwScore is null)
                .Select(r => (r.Id, r.Path))
                .ToList();
        }

        var targets = new List<(long id, string path)>();
        using var c = db.OpenRead();
        using var cmd = c.CreateCommand();
        cmd.CommandText = rescoreExisting
            ? $"SELECT id, path FROM images WHERE {PathScope.Where(root)}"
            : $"SELECT id, path FROM images WHERE nsfw_score IS NULL AND {PathScope.Where(root)}";
        PathScope.Bind(cmd, root);
        using var r = cmd.ExecuteReader();
        while (r.Read()) targets.Add((r.GetInt64(0), r.GetString(1)));
        return targets;
    }
}
