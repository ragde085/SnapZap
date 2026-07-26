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
    /// Score only photos beneath this folder; null scores the whole catalogue. The catalogue
    /// spans every folder ever scanned, so an unscoped run on a four-photo folder went off and
    /// scored thousands of photos the user could not see and had not asked about.
    /// </param>
    public async Task<NsfwRunResult> ScoreAllAsync(
        string? root = null,
        IProgress<NsfwProgress>? progress = null,
        bool rescoreExisting = false,
        CancellationToken ct = default)
    {
        if (!File.Exists(modelPath))
            return new NsfwRunResult(false, 0, 0, $"NSFW model not found at {modelPath} — scoring skipped.");

        // Collect target rows (id, path). Inference is CPU-bound and the ORT session is not
        // thread-safe for concurrent Run on all providers, so we score sequentially; the SQLite
        // writes are naturally serialized too.
        var targets = new List<(long id, string path)>();
        using (var cmd = db.Connection.CreateCommand())
        {
            cmd.CommandText = rescoreExisting
                ? $"SELECT id, path FROM images WHERE {PathScope.Where(root)}"
                : $"SELECT id, path FROM images WHERE nsfw_score IS NULL AND {PathScope.Where(root)}";
            PathScope.Bind(cmd, root);
            using var r = cmd.ExecuteReader();
            while (r.Read()) targets.Add((r.GetInt64(0), r.GetString(1)));
        }

        if (targets.Count == 0)
            return new NsfwRunResult(true, 0, 0, "All images already scored.");

        int done = 0, failed = 0;
        // Off-thread: constructing the session reads and prepares ~328 MB, and on the caller's
        // context that is a multi-second freeze with no progress bar moving.
        using var clf = await Task.Run(() => new OnnxNsfwClassifier(modelPath), ct);

        using var update = db.Connection.CreateCommand();
        update.CommandText = "UPDATE images SET nsfw_score=$s WHERE id=$id";
        var ps = update.Parameters.Add("$s", SqliteType.Real);
        var pid = update.Parameters.Add("$id", SqliteType.Integer);

        foreach (var (id, path) in targets)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var score = await Task.Run(() => clf.ScoreFile(path), ct);
                ps.Value = score;
                pid.Value = id;
                update.ExecuteNonQuery();
            }
            catch { failed++; }
            done++;
            progress?.Report(new NsfwProgress(done, targets.Count, Path.GetFileName(path)));
        }

        return new NsfwRunResult(true, done - failed, failed,
            clf.ConfigFromFile ? null : "⚠ preprocessor_config.json not found — using Falconsai defaults.");
    }
}
