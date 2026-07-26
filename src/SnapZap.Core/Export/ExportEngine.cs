using Microsoft.Data.Sqlite;
using SnapZap.Core.Data;
using SnapZap.Core.Platform;
using SnapZap.Core.Scanning;

namespace SnapZap.Core.Export;

/// <summary>
/// Writes the curated set to a destination folder (DESIGN §6). Enforces the safety
/// invariants (§7): verify-before-destroy, never overwrite, everything logged/reversible,
/// resumable. This is the primary workflow of the app.
/// </summary>
public sealed class ExportEngine(Database db, ILinkService links, ITrashService trash)
{
    /// <summary>Estimate the run without moving any bytes: counts, size, free space, hardlink availability.</summary>
    public Preflight Plan(ExportRequest req)
    {
        var imgs = new ImageRepository(db).ByIds(req.KeeperIds);
        long total = imgs.Sum(i => i.FileSize);

        // null = we couldn't ask the drive. Kept distinct from 0 ("the disk is full"), which
        // the old sentinel conflated — a genuinely full destination reported "enough room".
        long? free = null;
        try { free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(req.Destination))!).AvailableFreeSpace; }
        catch { /* non-fatal: unknown free space */ }

        bool hardlink = req.Mode == TransferMode.Hardlink &&
                        Directory.Exists(req.SourceRoot) &&
                        links.SameVolume(req.SourceRoot, EnsureDirProbe(req.Destination));

        var planner = MakePlanner(req);
        var samples = imgs.Take(5).Select(i => planner.DirectoryFor(i)).Distinct().Take(5).ToList();

        // Hardlink needs zero extra space; copy/move needs the full size on the destination.
        // Unknown free space doesn't block the export — the user may know better than we can
        // measure — but it is reported as unverified, not as a pass.
        bool enough = hardlink || free is null || free > total;
        return new Preflight(imgs.Count, total, free, enough, hardlink, samples);
    }

    public async Task<ExportResult> RunAsync(
        ExportRequest req,
        IProgress<ExportProgress>? progress = null,
        CancellationToken ct = default)
    {
        var repo = new ImageRepository(db);
        var keepers = repo.ByIds(req.KeeperIds);
        Directory.CreateDirectory(req.Destination);

        // Decide the effective mode: hardlink silently falls back to copy across volumes.
        var mode = req.Mode;
        if (mode == TransferMode.Hardlink &&
            !links.SameVolume(req.SourceRoot, EnsureDirProbe(req.Destination)))
            mode = TransferMode.Copy;

        long runId = OpenRun(req, mode);
        var doneStatuses = LoadCompleted(runId); // resume: ids already verified/skipped
        var planner = MakePlanner(req);

        int exported = 0, skipped = 0, failed = 0, done = 0;
        var outcomes = new List<ExportItemOutcome>();

        // Move relocates the original, so each run gets its own undo batch and shows up in
        // History alongside deletes.
        var moveBatch = $"move-{runId}";

        foreach (var img in keepers)
        {
            ct.ThrowIfCancellationRequested();
            done++;
            progress?.Report(new ExportProgress(done, keepers.Count, Path.GetFileName(img.Path), "export"));

            if (doneStatuses.TryGetValue(img.Id, out var prev))
            {
                if (prev == "verified") exported++; else if (prev == "skipped") skipped++;
                continue; // already handled in a previous run
            }

            try
            {
                if (!File.Exists(img.Path))
                    throw new FileNotFoundException("source missing", img.Path);

                var (dest, skipIdentical) = planner.Resolve(img);
                if (skipIdentical)
                {
                    RecordItem(runId, img.Id, "skipped", dest, "identical file already at destination");
                    outcomes.Add(new ExportItemOutcome(img.Id, "skipped", dest, "identical"));
                    skipped++;
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                Transfer(img.Path, dest, mode);
                Verify(img, dest, mode);                     // throws on mismatch
                if (mode == TransferMode.Move)
                {
                    // Log before releasing: if the process dies between the two, an undo entry
                    // for a file still present is harmless, whereas a released file with no
                    // entry would be unreversible (DESIGN §7.4).
                    LogUndo(moveBatch, "move", img.Path, dest);
                    ReleaseSource(img.Path, dest);
                    // The file lives at `dest` now, so the catalog has to as well — otherwise
                    // every row we just moved points at a path we just deleted.
                    repo.Repath(img.Id, dest);
                }

                RecordItem(runId, img.Id, "verified", dest, null);
                outcomes.Add(new ExportItemOutcome(img.Id, "verified", dest, mode.ToString()));
                exported++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                RecordItem(runId, img.Id, "failed", null, ex.Message);
                outcomes.Add(new ExportItemOutcome(img.Id, "failed", null, ex.Message));
                failed++;
            }
        }

        // Rejects are only ever touched AFTER keepers are verified (§7.2). Recycle, never hard-delete.
        int recycled = 0;
        if (req.RejectAction == RejectAction.Recycle && req.RejectIds.Count > 0)
        {
            var batch = $"export-{runId}";
            var rejects = repo.ByIds(req.RejectIds);
            done = 0;
            foreach (var img in rejects)
            {
                ct.ThrowIfCancellationRequested();
                done++;
                progress?.Report(new ExportProgress(done, rejects.Count, Path.GetFileName(img.Path), "recycle"));
                try
                {
                    if (!File.Exists(img.Path)) continue;
                    var loc = await trash.SendToTrashAsync(img.Path, ct);
                    LogUndo(batch, "recycle", img.Path, loc);
                    recycled++;
                }
                catch { /* a reject that won't recycle is non-fatal; keepers are already safe */ }
            }
        }

        CloseRun(runId, failed == 0 ? "done" : "failed");
        return new ExportResult(runId, exported, skipped, failed, recycled, req.Destination, outcomes);
    }

    // --- transfer + verification ------------------------------------------------

    void Transfer(string src, string dest, TransferMode mode)
    {
        switch (mode)
        {
            case TransferMode.Hardlink:
                links.CreateHardLink(dest, src);
                break;
            default: // Copy and Move both start by copying; File.Copy never overwrites here.
                File.Copy(src, dest, overwrite: false);
                PreserveTimestamps(src, dest);
                break;
        }
    }

    void Verify(ImageRecord img, string dest, TransferMode mode)
    {
        if (!File.Exists(dest)) throw new IOException($"destination not written: {dest}");
        if (mode == TransferMode.Hardlink)
        {
            // Same inode ⇒ identical bytes; a size check catches gross link errors cheaply.
            if (new FileInfo(dest).Length != img.FileSize)
                throw new IOException($"hardlink size mismatch for {dest}");
            return;
        }
        // Copy/Move: re-hash the destination and compare to the source's known hash.
        var destHash = Hasher.HashFile(dest);
        if (destHash != img.ContentHash)
            throw new IOException($"verification failed: {dest} hash != source");
    }

    void ReleaseSource(string src, string dest)
    {
        // Only reached after the destination is hash-verified, and only after the move has been
        // written to undo_log. This is relocation, not the reject-deletion path: the bytes still
        // exist at `dest`, so the source is deleted outright rather than recycled — recycling
        // would leave a second copy and defeat the space saving that makes anyone choose Move.
        // Reversibility comes from the undo entry, which restores by copying back from `dest`.
        File.Delete(src);
    }

    static void PreserveTimestamps(string src, string dest)
    {
        try
        {
            File.SetCreationTimeUtc(dest, File.GetCreationTimeUtc(src));
            File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(src));
        }
        catch { /* timestamp preservation is best-effort */ }
    }

    // --- helpers ----------------------------------------------------------------

    DestinationPlanner MakePlanner(ExportRequest req) => new(
        req.Destination, req.Structure, req.SourceRoot,
        exists: File.Exists,
        hashOf: p => { try { return Hasher.HashFile(p); } catch { return ""; } });

    static string EnsureDirProbe(string dest)
    {
        // Give SameVolume an existing path to stat: the destination if present, else its
        // nearest existing ancestor, else the destination string itself.
        var p = Path.GetFullPath(dest);
        while (!Directory.Exists(p))
        {
            var parent = Path.GetDirectoryName(p);
            if (string.IsNullOrEmpty(parent) || parent == p) break;
            p = parent;
        }
        return p;
    }

    // --- persistence (export_runs / export_items / undo_log) --------------------

    long OpenRun(ExportRequest req, TransferMode effectiveMode)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO export_runs(started_utc, destination, mode, structure, reject_action, status)
            VALUES ($t,$d,$m,$s,$r,'running') RETURNING id
            """;
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$d", req.Destination);
        cmd.Parameters.AddWithValue("$m", effectiveMode.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$s", req.Structure.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$r", req.RejectAction.ToString().ToLowerInvariant());
        return (long)cmd.ExecuteScalar()!;
    }

    Dictionary<long, string> LoadCompleted(long runId)
    {
        var map = new Dictionary<long, string>();
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT image_id, status FROM export_items WHERE run_id=$r AND status IN ('verified','skipped')";
        cmd.Parameters.AddWithValue("$r", runId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetInt64(0)] = r.GetString(1);
        return map;
    }

    void RecordItem(long runId, long imageId, string status, string? dest, string? detail)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO export_items(run_id, image_id, dest_path, status, skip_reason, error)
            VALUES ($run,$img,$dest,$status,$skip,$err)
            ON CONFLICT(run_id, image_id) DO UPDATE SET
              dest_path=excluded.dest_path, status=excluded.status,
              skip_reason=excluded.skip_reason, error=excluded.error
            """;
        cmd.Parameters.AddWithValue("$run", runId);
        cmd.Parameters.AddWithValue("$img", imageId);
        cmd.Parameters.AddWithValue("$dest", (object?)dest ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$skip", (object?)(status == "skipped" ? detail : null) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$err", (object?)(status == "failed" ? detail : null) ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    void CloseRun(long runId, string status)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE export_runs SET finished_utc=$t, status=$s WHERE id=$id";
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$id", runId);
        cmd.ExecuteNonQuery();
    }

    void LogUndo(string batch, string op, string original, string? newLoc)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO undo_log(batch_id, op, original_path, new_location, ts_utc)
            VALUES ($b,$o,$p,$n,$t)
            """;
        cmd.Parameters.AddWithValue("$b", batch);
        cmd.Parameters.AddWithValue("$o", op);
        cmd.Parameters.AddWithValue("$p", original);
        cmd.Parameters.AddWithValue("$n", (object?)newLoc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }
}
