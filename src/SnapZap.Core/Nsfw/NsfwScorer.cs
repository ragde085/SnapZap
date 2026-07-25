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
    public async Task<NsfwRunResult> ScoreAllAsync(
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
                ? "SELECT id, path FROM images"
                : "SELECT id, path FROM images WHERE nsfw_score IS NULL";
            using var r = cmd.ExecuteReader();
            while (r.Read()) targets.Add((r.GetInt64(0), r.GetString(1)));
        }

        if (targets.Count == 0)
            return new NsfwRunResult(true, 0, 0, "All images already scored.");

        int done = 0, failed = 0;
        using var clf = new OnnxNsfwClassifier(modelPath);

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
