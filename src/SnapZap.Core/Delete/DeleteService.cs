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
        using var c = db.OpenRead();
        using var cmd = c.CreateCommand();
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
        var rows = new List<(long id, string op, string original, string? loc)>();
        using (var c = db.OpenRead())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, op, original_path, new_location FROM undo_log WHERE batch_id=$b AND restored=0";
            cmd.Parameters.AddWithValue("$b", batchId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
        }

        int restored = 0, missing = 0;
        foreach (var (id, op, original, loc) in rows)
        {
            ct.ThrowIfCancellationRequested();

            // A recycled file comes back out of the trash; a moved file is still on disk at the
            // export destination and comes back by copying.
            var ok = op == "move"
                ? RestoreMoved(original, loc)
                : await trash.RestoreAsync(original, loc, ct);

            if (ok)
            {
                // The export repointed the catalog at the destination when it moved the file;
                // putting the file back has to put the row back too, or undoing a move leaves
                // the grid describing the copy the user was trying to walk away from.
                if (op == "move") RepathByPath(loc, original);
                MarkRestored(id);
                restored++;
            }
            else missing++;
        }
        return new RestoreResult(batchId, restored, missing);
    }

    /// <summary>
    /// Undo an export-move by copying the file back from the destination it was relocated to.
    /// The exported copy is deliberately left in place: it was hash-verified on the way out,
    /// and removing it here would be a destructive act performed *during an undo*.
    /// </summary>
    static bool RestoreMoved(string originalPath, string? destination)
    {
        if (string.IsNullOrEmpty(destination) || !File.Exists(destination)) return false;
        if (File.Exists(originalPath)) return true;   // already back; treat as restored

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!);
            File.Copy(destination, originalPath, overwrite: false);   // never a silent overwrite
            return true;
        }
        catch { return false; }
    }

    /// <summary>Point the catalog row currently at <paramref name="from"/> back at
    /// <paramref name="to"/>. No-op if nothing is filed under the old path.</summary>
    void RepathByPath(string? from, string to)
    {
        if (string.IsNullOrEmpty(from)) return;
        lock (db.WriteLock)
        {
            using var cmd = db.Writer.CreateCommand();
            cmd.CommandText = "UPDATE images SET path=$to WHERE path=$from";
            cmd.Parameters.AddWithValue("$to", to);
            cmd.Parameters.AddWithValue("$from", from);
            cmd.ExecuteNonQuery();
        }
    }

    void DeleteRow(long imageId)
    {
        lock (db.WriteLock)
        {
            using var cmd = db.Writer.CreateCommand();
            cmd.CommandText = "DELETE FROM images WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", imageId);
            cmd.ExecuteNonQuery();
        }
    }

    void LogUndo(string batch, string op, string original, string? loc)
    {
        lock (db.WriteLock)
        {
            using var cmd = db.Writer.CreateCommand();
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
    }

    void MarkRestored(long undoId)
    {
        lock (db.WriteLock)
        {
            using var cmd = db.Writer.CreateCommand();
            cmd.CommandText = "UPDATE undo_log SET restored=1 WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", undoId);
            cmd.ExecuteNonQuery();
        }
    }
}
