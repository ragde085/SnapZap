using Microsoft.Data.Sqlite;
using SnapZap.Core.Data;
using SnapZap.Core.Platform;

namespace SnapZap.Core.Delete;

public sealed record DeleteResult(string BatchId, int Recycled, int Failed);
public sealed record UndoBatch(string BatchId, long Ts, int Total, int Restored);
public sealed record RestoreResult(string BatchId, int Restored, int Missing);

/// <summary>
/// In-place deletion as a separate mode (DESIGN §6/§7): recycle selected images, log every
/// op to undo_log for one-click restore, and prune recycled rows from the catalog. Never a
/// hard delete.
/// </summary>
public sealed class DeleteService(Database db, ITrashService trash)
{
    public async Task<DeleteResult> RecycleAsync(
        IReadOnlyList<long> imageIds, CancellationToken ct = default)
    {
        var repo = new ImageRepository(db);
        var imgs = repo.ByIds(imageIds);
        var batch = "delete-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int recycled = 0, failed = 0;

        foreach (var img in imgs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(img.Path))
                {
                    var loc = await trash.SendToTrashAsync(img.Path, ct);
                    LogUndo(batch, "recycle", img.Path, loc);
                }
                DeleteRow(img.Id);
                recycled++;
            }
            catch (OperationCanceledException) { throw; }
            catch { failed++; }
        }
        return new DeleteResult(batch, recycled, failed);
    }

    public IReadOnlyList<UndoBatch> Batches()
    {
        var list = new List<UndoBatch>();
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT batch_id, MIN(ts_utc) AS ts, COUNT(*) AS total,
                   SUM(restored) AS restored
            FROM undo_log GROUP BY batch_id ORDER BY ts DESC
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new UndoBatch(r.GetString(0), r.GetInt64(1), r.GetInt32(2),
                                   r.IsDBNull(3) ? 0 : r.GetInt32(3)));
        return list;
    }

    public async Task<RestoreResult> RestoreAsync(string batchId, CancellationToken ct = default)
    {
        var rows = new List<(long id, string original, string? loc)>();
        using (var cmd = db.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT id, original_path, new_location FROM undo_log WHERE batch_id=$b AND restored=0";
            cmd.Parameters.AddWithValue("$b", batchId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add((r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
        }

        int restored = 0, missing = 0;
        foreach (var (id, original, loc) in rows)
        {
            ct.ThrowIfCancellationRequested();
            var ok = await trash.RestoreAsync(original, loc, ct);
            if (ok) { MarkRestored(id); restored++; } else missing++;
        }
        return new RestoreResult(batchId, restored, missing);
    }

    void DeleteRow(long imageId)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM images WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", imageId);
        cmd.ExecuteNonQuery();
    }

    void LogUndo(string batch, string op, string original, string? loc)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO undo_log(batch_id, op, original_path, new_location, ts_utc)
            VALUES ($b,$o,$p,$n,$t)
            """;
        cmd.Parameters.AddWithValue("$b", batch);
        cmd.Parameters.AddWithValue("$o", op);
        cmd.Parameters.AddWithValue("$p", original);
        cmd.Parameters.AddWithValue("$n", (object?)loc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }

    void MarkRestored(long undoId)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE undo_log SET restored=1 WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", undoId);
        cmd.ExecuteNonQuery();
    }
}
