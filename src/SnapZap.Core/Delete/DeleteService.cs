using Microsoft.Data.Sqlite;
using SnapZap.Core.Data;
using SnapZap.Core.Platform;

namespace SnapZap.Core.Delete;

public sealed record DeleteResult(string BatchId, int Recycled, int Failed);
public sealed record UndoBatch(string BatchId, long Ts, int Total, int Restored);
public sealed record RestoreResult(string BatchId, int Restored, int Missing);
public sealed record DeleteProgress(int Done, int Total, string? Current);

/// <summary>One recycled/moved photo within a batch, for the History dialog's preview strip.
/// <see cref="ContentHash"/> is null for rows logged before this column existed — those items
/// render without a thumbnail rather than a broken image.</summary>
public sealed record UndoItem(long Id, string OriginalPath, string? ContentHash, bool Restored);

/// <summary>
/// In-place deletion as a separate mode (DESIGN §6/§7): recycle selected images, log every
/// op to undo_log for one-click restore, and prune recycled rows from the catalog. Never a
/// hard delete.
/// </summary>
public sealed class DeleteService(Database db, ITrashService trash)
{
    public async Task<DeleteResult> RecycleAsync(
        IReadOnlyList<long> imageIds, IProgress<DeleteProgress>? progress = null, CancellationToken ct = default)
    {
        var repo = new ImageRepository(db);
        var imgs = repo.ByIds(imageIds);
        var batch = "delete-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int recycled = 0, failed = 0, done = 0;

        foreach (var img in imgs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(img.Path))
                {
                    var loc = await trash.SendToTrashAsync(img.Path, ct);
                    LogUndo(batch, "recycle", img.Path, loc, img.ContentHash);
                }
                DeleteRow(img.Id);
                recycled++;
            }
            catch (OperationCanceledException) { throw; }
            catch { failed++; }
            finally
            {
                done++;
                progress?.Report(new DeleteProgress(done, imageIds.Count, img.Path));
            }
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

    /// <summary>Up to <paramref name="limit"/> items from a batch, oldest-logged first, for a
    /// thumbnail preview strip. Callers compare against <see cref="UndoBatch.Total"/> to know
    /// how many more were left out.</summary>
    public IReadOnlyList<UndoItem> ItemsInBatch(string batchId, int limit = 8)
    {
        var list = new List<UndoItem>();
        using var c = db.OpenRead();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, original_path, content_hash, restored
            FROM undo_log WHERE batch_id=$b ORDER BY id LIMIT $lim
            """;
        cmd.Parameters.AddWithValue("$b", batchId);
        cmd.Parameters.AddWithValue("$lim", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new UndoItem(r.GetInt64(0), r.GetString(1),
                                  r.IsDBNull(2) ? null : r.GetString(2), r.GetInt32(3) != 0));
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
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (await RestoreRowAsync(row, ct)) restored++; else missing++;
        }
        return new RestoreResult(batchId, restored, missing);
    }

    /// <summary>
    /// Restore one photo out of a batch (the History dialog's per-thumbnail Restore), rather than
    /// waiting to undo everything the batch touched. A no-op (returns <see langword="false"/>)
    /// if the row is already restored or doesn't exist — same "missing" outcome
    /// <see cref="RestoreAsync"/> gives a row it can't put back.
    /// </summary>
    public async Task<bool> RestoreItemAsync(long undoLogId, CancellationToken ct = default)
    {
        (long id, string op, string original, string? loc)? row = null;
        using (var c = db.OpenRead())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, op, original_path, new_location FROM undo_log WHERE id=$id AND restored=0";
            cmd.Parameters.AddWithValue("$id", undoLogId);
            using var r = cmd.ExecuteReader();
            if (r.Read())
                row = (r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3));
        }
        return row is { } item && await RestoreRowAsync(item, ct);
    }

    /// <summary>
    /// Permanently removes one photo from the trash and drops its history entry.
    /// </summary>
    /// <remarks>
    /// <para>⚠ <b>Irreversible, and one of only two places in SnapZap that is.</b> Callers must
    /// confirm with the user first — the other is <c>FolderTrashService.Empty</c>.</para>
    ///
    /// <para><b>Why it purges the file rather than only forgetting the record.</b>
    /// <c>undo_log.new_location</c> is the only pointer SnapZap keeps to a trashed file. Dropping
    /// the row on its own would strand the file in the trash permanently: still consuming the
    /// user's storage, no longer restorable, and no longer visible anywhere in the app. "Remove
    /// from trash" that silently leaves the bytes behind is a worse outcome than either honest
    /// alternative, so the record and the file go together.</para>
    ///
    /// <para>Already-restored rows are history, not trash — the file is back where it belongs, so
    /// only the record is dropped and nothing is deleted.</para>
    /// </remarks>
    /// <returns>True when a row was removed.</returns>
    public bool ForgetItem(long undoLogId)
    {
        string? loc = null; bool restored = false, found = false;
        using (var c = db.OpenRead())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT new_location, restored FROM undo_log WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", undoLogId);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                found = true;
                loc = r.IsDBNull(0) ? null : r.GetString(0);
                restored = r.GetInt32(1) != 0;
            }
        }
        if (!found) return false;

        // Restored rows point at the user's own file, back in their library. Deleting that would
        // turn "remove this history entry" into "delete the photo I already got back".
        if (!restored && loc is not null)
        {
            try { if (File.Exists(loc)) File.Delete(loc); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Leave the row in place if the file could not be removed, so the entry still
                // reflects a file that is really there. Reporting success here would hide bytes.
                return false;
            }
        }

        lock (db.WriteLock)
        {
            using var del = db.Writer.CreateCommand();
            del.CommandText = "DELETE FROM undo_log WHERE id=$id";
            del.Parameters.AddWithValue("$id", undoLogId);
            return del.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// <see cref="ForgetItem"/> for every entry in a batch. Returns how many were removed.
    /// </summary>
    /// <remarks>Best-effort per row: one file that will not delete does not abandon the rest,
    /// and its row survives so the trash never under-reports what it is holding.</remarks>
    public int ForgetBatch(string batchId)
    {
        var ids = new List<long>();
        using (var c = db.OpenRead())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM undo_log WHERE batch_id=$b";
            cmd.Parameters.AddWithValue("$b", batchId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetInt64(0));
        }

        int removed = 0;
        foreach (var id in ids) if (ForgetItem(id)) removed++;
        return removed;
    }

    /// <summary>Shared by both <see cref="RestoreAsync"/> and <see cref="RestoreItemAsync"/>: puts
    /// one logged file back and marks it restored, or reports it missing.</summary>
    async Task<bool> RestoreRowAsync((long id, string op, string original, string? loc) row, CancellationToken ct)
    {
        var (id, op, original, loc) = row;

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
        }
        return ok;
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

    void LogUndo(string batch, string op, string original, string? loc, string? contentHash)
    {
        lock (db.WriteLock)
        {
            using var cmd = db.Writer.CreateCommand();
            cmd.CommandText = """
                INSERT INTO undo_log(batch_id, op, original_path, new_location, ts_utc, content_hash)
                VALUES ($b,$o,$p,$n,$t,$h)
                """;
            cmd.Parameters.AddWithValue("$b", batch);
            cmd.Parameters.AddWithValue("$o", op);
            cmd.Parameters.AddWithValue("$p", original);
            cmd.Parameters.AddWithValue("$n", (object?)loc ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$h", (object?)contentHash ?? DBNull.Value);
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
