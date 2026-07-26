using Microsoft.Data.Sqlite;
using SnapZap.Core.Dedup;

namespace SnapZap.Core.Data;

/// <summary>Reads/writes the <c>images</c> table.</summary>
public sealed class ImageRepository(Database db)
{
    /// <summary>
    /// Bulk-loaded tier-1 probe map for an entire scan root: path → (id, size, mtime, analyzed).
    /// Replaces a single-row <c>SELECT</c> per file under a lock — the larger benefit is removing
    /// lock contention from the cache-<b>miss</b> path, where workers used to queue on the same
    /// mutex before they could even begin decoding (measured 8x on the probe path alone: 40k
    /// files, 267ms → 32ms).
    /// </summary>
    public Dictionary<string, (long id, long size, long mtime, bool analyzed)> ProbeMap(string? root)
    {
        var map = new Dictionary<string, (long, long, long, bool)>(StringComparer.Ordinal);
        using var c = db.OpenRead();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT path, id, file_size, mtime, analyzed_at
            FROM images
            WHERE {PathScope.Where(root)}
            """;
        PathScope.Bind(cmd, root);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            map[r.GetString(0)] = (r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), !r.IsDBNull(4));
        return map;
    }

    /// <summary>Insert or replace a fully analyzed image row. Returns the row id.</summary>
    /// <remarks>
    /// <c>dupe_checked_at</c> is reset to null on conflict rather than carried over. The scanner
    /// only reaches this for a cache miss — the file's size or mtime moved, so its bytes may have
    /// too — and a duplicate verdict computed against the previous content is no longer a verdict
    /// about this file. Leaving the old timestamp would report the folder as checked while its
    /// changed photos silently were not.
    ///
    /// Single-row, autocommit form — kept for callers that need the row id back (tests build
    /// fixtures against it) or that only ever write one row. The scan's hot path uses
    /// <see cref="UpsertBatch"/> instead, which reuses one prepared command across a whole
    /// transaction rather than paying autocommit overhead per file.
    /// </remarks>
    public long Upsert(ImageRecord img)
    {
        lock (db.WriteLock)
        {
            using var cmd = db.Writer.CreateCommand();
            cmd.CommandText = """
                INSERT INTO images
                  (path, content_hash, file_size, mtime, width, height, format,
                   nsfw_score, blur_score, exif_taken, exif_camera, thumb_path, analyzed_at, phash)
                VALUES
                  ($path,$hash,$size,$mtime,$w,$h,$fmt,$nsfw,$blur,$taken,$camera,$thumb,$at,$phash)
                ON CONFLICT(path) DO UPDATE SET
                  content_hash=excluded.content_hash, file_size=excluded.file_size,
                  mtime=excluded.mtime, width=excluded.width, height=excluded.height,
                  format=excluded.format, nsfw_score=excluded.nsfw_score,
                  blur_score=excluded.blur_score, exif_taken=excluded.exif_taken,
                  exif_camera=excluded.exif_camera, thumb_path=excluded.thumb_path,
                  analyzed_at=excluded.analyzed_at, phash=excluded.phash,
                  dupe_checked_at=NULL, dupe_checked_kinds=0
                RETURNING id;
                """;
            BindUpsertParams(cmd, img);
            return (long)cmd.ExecuteScalar()!;
        }
    }

    /// <summary>
    /// Insert or replace many rows in one prepared, parameter-reused, transaction-batched pass.
    /// </summary>
    /// <remarks>
    /// Measured 10.8x against per-row autocommit with a fresh <c>SqliteCommand</c> and
    /// <c>AddWithValue</c> each time (20k rows: 998ms → 92ms). No row id is returned — nothing in
    /// the scan path uses it (confirmed: <c>Scanner</c> discards <see cref="Upsert"/>'s return
    /// value too). Durability trade: a crash mid-batch loses at most this batch's rows; the next
    /// scan's tier-1 probe treats them as unanalyzed (no id ever recorded them as done) and
    /// re-processes them — the same recovery path an interrupted scan already has.
    /// </remarks>
    public void UpsertBatch(IReadOnlyList<ImageRecord> records)
    {
        if (records.Count == 0) return;

        lock (db.WriteLock)
        {
            using var tx = db.Writer.BeginTransaction();
            using var cmd = db.Writer.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO images
                  (path, content_hash, file_size, mtime, width, height, format,
                   nsfw_score, blur_score, exif_taken, exif_camera, thumb_path, analyzed_at, phash)
                VALUES
                  ($path,$hash,$size,$mtime,$w,$h,$fmt,$nsfw,$blur,$taken,$camera,$thumb,$at,$phash)
                ON CONFLICT(path) DO UPDATE SET
                  content_hash=excluded.content_hash, file_size=excluded.file_size,
                  mtime=excluded.mtime, width=excluded.width, height=excluded.height,
                  format=excluded.format, nsfw_score=excluded.nsfw_score,
                  blur_score=excluded.blur_score, exif_taken=excluded.exif_taken,
                  exif_camera=excluded.exif_camera, thumb_path=excluded.thumb_path,
                  analyzed_at=excluded.analyzed_at, phash=excluded.phash,
                  dupe_checked_at=NULL, dupe_checked_kinds=0
                """;
            BindUpsertParams(cmd, records[0]);
            cmd.Prepare();

            foreach (var img in records)
            {
                SetUpsertParamValues(cmd, img);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    static void BindUpsertParams(SqliteCommand cmd, ImageRecord img)
    {
        cmd.Parameters.Add("$path", SqliteType.Text);
        cmd.Parameters.Add("$hash", SqliteType.Text);
        cmd.Parameters.Add("$size", SqliteType.Integer);
        cmd.Parameters.Add("$mtime", SqliteType.Integer);
        cmd.Parameters.Add("$w", SqliteType.Integer);
        cmd.Parameters.Add("$h", SqliteType.Integer);
        cmd.Parameters.Add("$fmt", SqliteType.Text);
        cmd.Parameters.Add("$nsfw", SqliteType.Real);
        cmd.Parameters.Add("$blur", SqliteType.Real);
        cmd.Parameters.Add("$taken", SqliteType.Integer);
        cmd.Parameters.Add("$camera", SqliteType.Text);
        cmd.Parameters.Add("$thumb", SqliteType.Text);
        cmd.Parameters.Add("$at", SqliteType.Integer);
        cmd.Parameters.Add("$phash", SqliteType.Blob);
        SetUpsertParamValues(cmd, img);
    }

    static void SetUpsertParamValues(SqliteCommand cmd, ImageRecord img)
    {
        cmd.Parameters["$path"].Value = img.Path;
        cmd.Parameters["$hash"].Value = img.ContentHash;
        cmd.Parameters["$size"].Value = img.FileSize;
        cmd.Parameters["$mtime"].Value = img.Mtime;
        cmd.Parameters["$w"].Value = (object?)img.Width ?? DBNull.Value;
        cmd.Parameters["$h"].Value = (object?)img.Height ?? DBNull.Value;
        cmd.Parameters["$fmt"].Value = (object?)img.Format ?? DBNull.Value;
        cmd.Parameters["$nsfw"].Value = (object?)img.NsfwScore ?? DBNull.Value;
        cmd.Parameters["$blur"].Value = (object?)img.BlurScore ?? DBNull.Value;
        cmd.Parameters["$taken"].Value = (object?)img.ExifTaken ?? DBNull.Value;
        cmd.Parameters["$camera"].Value = (object?)img.ExifCamera ?? DBNull.Value;
        cmd.Parameters["$thumb"].Value = (object?)img.ThumbPath ?? DBNull.Value;
        cmd.Parameters["$at"].Value = (object?)img.AnalyzedAt ?? DBNull.Value;
        cmd.Parameters["$phash"].Value = (object?)img.Phash ?? DBNull.Value;
    }

    /// <summary>
    /// Delete rows whose file is no longer on disk: inside <paramref name="root"/> the walk just
    /// done is the evidence, and outside it each remaining path is stat-ed.
    /// </summary>
    /// <remarks>
    /// The rule is "the file is gone", and it used to be approximated by "the file wasn't in the
    /// folder I just scanned" — so scanning a second library wiped the first, discarding every
    /// hash, score and keeper decision for photos still sitting on disk. Checking out-of-root
    /// rows individually costs one stat each, but only for rows the scan says nothing about,
    /// and it means a stale row is never kept just because its folder wasn't the one scanned.
    /// </remarks>
    public int DeleteMissing(string root, IReadOnlyCollection<string> livePaths)
    {
        lock (db.WriteLock)
        {
            var c = db.Writer;

            // Build a temp table of live paths and delete the complement. Cheap for large sets.
            using (var tmp = c.CreateCommand())
            {
                tmp.CommandText = "CREATE TEMP TABLE IF NOT EXISTS _live(path TEXT PRIMARY KEY); DELETE FROM _live;";
                tmp.ExecuteNonQuery();
            }
            using (var tx = c.BeginTransaction())
            {
                using var ins = c.CreateCommand();
                ins.CommandText = "INSERT OR IGNORE INTO _live(path) VALUES ($p)";
                var p = ins.Parameters.Add("$p", SqliteType.Text);
                foreach (var path in livePaths) { p.Value = path; ins.ExecuteNonQuery(); }
                tx.Commit();
            }

            // Inside the root: the enumeration is authoritative, so anything absent from it is gone.
            // Scoping goes through PathScope so this cannot drift from what the rest of the app calls
            // "inside the folder" — and so it gets the same index-usable range predicate.
            int removed;
            using (var del = c.CreateCommand())
            {
                del.CommandText = $"""
                    DELETE FROM images
                     WHERE {PathScope.Sql}
                       AND path NOT IN (SELECT path FROM _live)
                    """;
                PathScope.Bind(del, root);
                removed = del.ExecuteNonQuery();
            }

            // Outside the root: the scan saw nothing, so ask the filesystem directly.
            var strays = new List<string>();
            using (var q = c.CreateCommand())
            {
                q.CommandText = $"SELECT path FROM images WHERE NOT {PathScope.Sql}";
                PathScope.Bind(q, root);
                using var r = q.ExecuteReader();
                while (r.Read())
                {
                    var path = r.GetString(0);
                    if (!File.Exists(path)) strays.Add(path);
                }
            }
            if (strays.Count > 0)
            {
                using var tx = c.BeginTransaction();
                using var del = c.CreateCommand();
                del.CommandText = "DELETE FROM images WHERE path = $p";
                var p = del.Parameters.Add("$p", SqliteType.Text);
                foreach (var path in strays) { p.Value = path; removed += del.ExecuteNonQuery(); }
                tx.Commit();
            }
            return removed;
        }
    }

    /// <summary>Map absolute path → row id, pixel count and byte size, for correlating
    /// external tool output and picking a keeper among its matches.</summary>
    public Dictionary<string, (long id, long pixels, long bytes)> PathIndex()
    {
        var map = new Dictionary<string, (long, long, long)>(StringComparer.Ordinal);
        using var c = db.OpenRead();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path, id, COALESCE(width,0)*COALESCE(height,0), file_size FROM images";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            map[r.GetString(0)] = (r.GetInt64(1), r.GetInt64(2), r.GetInt64(3));
        return map;
    }

    /// <summary>
    /// Stamp every row under <paramref name="root"/> as covered by a completed duplicate-detection
    /// run. Returns the number of rows stamped.
    /// </summary>
    /// <remarks>
    /// Called only after detection finishes, never before it starts: a run that was cancelled or
    /// threw has told us nothing about these photos, and marking them checked would hide exactly
    /// the folders the user needs to go back to.
    /// </remarks>
    public int MarkDupeChecked(string? root, DupeKinds kinds, long? whenUtc = null)
    {
        lock (db.WriteLock)
        {
            using var cmd = db.Writer.CreateCommand();
            cmd.CommandText =
                $"UPDATE images SET dupe_checked_at=$at, dupe_checked_kinds=$kinds WHERE {PathScope.Where(root)}";
            cmd.Parameters.AddWithValue("$at", whenUtc ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$kinds", (int)kinds);
            PathScope.Bind(cmd, root);
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// The columns the perceptual detectors need, for every row under <paramref name="root"/>.
    /// </summary>
    /// <remarks>
    /// A narrow projection rather than <see cref="Under"/>: a detector holds the whole scope in
    /// memory and walks it in a tight n-squared loop, so carrying paths, thumbnails and NSFW
    /// scores through that would multiply the working set for fields it never reads.
    /// </remarks>
    public List<HashedImage> HashedUnder(string? root)
    {
        var list = new List<HashedImage>();
        using var c = db.OpenRead();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, phash,
                   COALESCE(width,0) * COALESCE(height,0) AS pixels,
                   file_size, exif_taken, exif_camera, blur_score, path
            FROM images
            WHERE {PathScope.Where(root)}
            ORDER BY id
            """;
        PathScope.Bind(cmd, root);

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var blob = r.IsDBNull(1) ? null : (byte[])r[1];
            list.Add(new HashedImage(
                Id: r.GetInt64(0),
                Path: r.GetString(7),
                Hash: PerceptualHash.FromBytes(blob),
                Pixels: r.GetInt64(2),
                Bytes: r.GetInt64(3),
                TakenUtc: r.IsDBNull(4) ? null : r.GetInt64(4),
                Camera: r.IsDBNull(5) ? null : r.GetString(5),
                BlurScore: r.IsDBNull(6) ? null : r.GetDouble(6)));
        }
        return list;
    }

    /// <summary>Rows under <paramref name="root"/> that still need a perceptual signature.
    /// <c>ContentHash</c> is included so a backfill can also regenerate the thumbnail keyed by it
    /// (Task 16) using the same decode, rather than looking the row back up separately.</summary>
    public List<(long Id, string Path, string ContentHash)> NeedingPhash(string? root)
    {
        var list = new List<(long, string, string)>();
        using var c = db.OpenRead();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT id, path, content_hash FROM images WHERE phash IS NULL AND {PathScope.Where(root)} ORDER BY id";
        PathScope.Bind(cmd, root);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1), r.GetString(2)));
        return list;
    }

    public void SetPhash(long id, byte[]? phash)
    {
        lock (db.WriteLock)
        {
            using var cmd = db.Writer.CreateCommand();
            cmd.CommandText = "UPDATE images SET phash=$p WHERE id=$id";
            cmd.Parameters.AddWithValue("$p", (object?)phash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Point a row at a file's new location after a verified move.
    /// </summary>
    /// <remarks>
    /// Only the path changes: the bytes were hash-verified at the destination, so the hash,
    /// dimensions, scores and thumbnail all still describe this row correctly. Leaving the old
    /// path in place instead would strand the row on a file that no longer exists.
    /// </remarks>
    public void Repath(long id, string newPath)
    {
        lock (db.WriteLock)
        {
            using var cmd = db.Writer.CreateCommand();
            cmd.CommandText = "UPDATE images SET path = $p WHERE id = $id";
            cmd.Parameters.AddWithValue("$p", newPath);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public long Count()
    {
        using var c = db.OpenRead();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM images";
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>Fetch specific images by id, in the given order (unknown ids dropped).</summary>
    /// <remarks>
    /// Parameterised <c>WHERE id IN (…)</c>, chunked at 500 parameters, rather than a full-table
    /// scan materialising every column (including the 160-byte <c>phash</c> blob) for every row
    /// in the catalogue just to pick out a handful of ids. <c>ExportEngine</c>, every preview-modal
    /// open and every delete hit this. Contract preserved exactly: results come back in
    /// caller-supplied order (not id order) across chunk boundaries, a duplicated input id is
    /// emitted as many times as it appears (matching the old dictionary-lookup behaviour), and
    /// empty input issues no query.
    /// </remarks>
    public IReadOnlyList<ImageRecord> ByIds(IEnumerable<long> ids)
    {
        var idList = ids as IReadOnlyList<long> ?? ids.ToList();
        if (idList.Count == 0) return [];

        var byId = new Dictionary<long, ImageRecord>();
        var distinctIds = idList.Distinct().ToList();
        const int ChunkSize = 500;

        using var c = db.OpenRead();
        for (int offset = 0; offset < distinctIds.Count; offset += ChunkSize)
        {
            var chunk = distinctIds.Skip(offset).Take(ChunkSize).ToList();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"""
                SELECT id, path, content_hash, file_size, mtime, width, height, format,
                       nsfw_score, blur_score, exif_taken, exif_camera, thumb_path, analyzed_at,
                       dupe_checked_at, dupe_checked_kinds, phash
                FROM images
                WHERE id IN ({string.Join(",", chunk.Select((_, i) => $"$id{i}"))})
                """;
            for (int i = 0; i < chunk.Count; i++)
                cmd.Parameters.AddWithValue($"$id{i}", chunk[i]);

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var img = ReadImageRecord(r);
                byId[img.Id] = img;
            }
        }

        var outp = new List<ImageRecord>(idList.Count);
        foreach (var id in idList)
            if (byId.TryGetValue(id, out var img)) outp.Add(img);
        return outp;
    }

    /// <summary>How many photos the catalogue holds in total, across every folder.</summary>
    public int TotalCount()
    {
        using var c = db.OpenRead();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM images";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// Empty the catalogue: every image, and the duplicate groups and export items that
    /// reference them by foreign key.
    /// </summary>
    /// <remarks>
    /// <c>undo_log</c> is deliberately left alone. It records recycled files by path, not by
    /// image id, so it survives this — and it has to: anything currently sitting in the
    /// Recycle Bin would otherwise become unrestorable from inside the app, which would make
    /// "forget the analysis" quietly destroy a recovery path (DESIGN §7 safety invariants).
    /// </remarks>
    public void DeleteAll()
    {
        lock (db.WriteLock)
        {
            var c = db.Writer;
            using (var tx = c.BeginTransaction())
            {
                using (var cmd = c.CreateCommand())
                {
                    // dupe_members and export_items cascade from these two.
                    cmd.CommandText = "DELETE FROM images; DELETE FROM dupe_groups; DELETE FROM export_runs;";
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }

            // Deleting rows does not shrink the file, and the dialog that offers this itemises the
            // database's size as something being reclaimed. Outside the transaction, because VACUUM
            // cannot run inside one — and best-effort, because failing to shrink a file is not a
            // failure to forget. Throwing here would abandon the caller midway, leaving the rows
            // gone but the scan scope and the thumbnails still in place.
            //
            // Even with per-operation reader connections (rather than one shared connection),
            // VACUUM still needs no other connection mid-transaction against this database file —
            // a concurrent reader opened during this window can still make it fail, which is
            // exactly why this stays best-effort rather than assumed to succeed (Task 18).
            try
            {
                using var vacuum = c.CreateCommand();
                // VACUUM first, checkpoint second. In WAL mode the rebuilt database lands in the
                // write-ahead log, so checkpointing beforehand truncates the old content and leaves
                // the new content in the WAL — the page count drops to nothing while the file on
                // disk stays exactly as large as it was, which is the one thing this is here to fix.
                vacuum.CommandText = "VACUUM; PRAGMA wal_checkpoint(TRUNCATE);";
                vacuum.ExecuteNonQuery();
            }
            catch (SqliteException) { /* no free space, or another connection is mid-read */ }
        }
    }

    /// <summary>
    /// Every image in the catalogue, across every folder ever scanned. Callers that show or
    /// act on photos want <see cref="Under"/> instead — the catalogue is a cache spanning
    /// folders, and a view built from all of it aims "select everything shown" at libraries
    /// the user scanned weeks ago and cannot see.
    /// </summary>
    public IEnumerable<ImageRecord> All() => Query(null);

    /// <summary>
    /// Images inside <paramref name="root"/>, or all of them when it is null or empty.
    /// </summary>
    /// <remarks>Uses <see cref="PathScope"/>, the same predicate the scoped work queries use,
    /// so what is shown and what is acted on cannot drift apart.</remarks>
    public IEnumerable<ImageRecord> Under(string? root) =>
        Query(string.IsNullOrEmpty(root) ? null : root);

    IEnumerable<ImageRecord> Query(string? root)
    {
        using var c = db.OpenRead();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, path, content_hash, file_size, mtime, width, height, format,
                   nsfw_score, blur_score, exif_taken, exif_camera, thumb_path, analyzed_at,
                   dupe_checked_at, dupe_checked_kinds, phash
            FROM images
            WHERE {PathScope.Where(root)}
            ORDER BY id
            """;
        PathScope.Bind(cmd, root);

        using var r = cmd.ExecuteReader();
        while (r.Read())
            yield return ReadImageRecord(r);
    }

    static ImageRecord ReadImageRecord(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Path = r.GetString(1),
        ContentHash = r.GetString(2),
        FileSize = r.GetInt64(3),
        Mtime = r.GetInt64(4),
        Width = r.IsDBNull(5) ? null : r.GetInt32(5),
        Height = r.IsDBNull(6) ? null : r.GetInt32(6),
        Format = r.IsDBNull(7) ? null : r.GetString(7),
        NsfwScore = r.IsDBNull(8) ? null : r.GetDouble(8),
        BlurScore = r.IsDBNull(9) ? null : r.GetDouble(9),
        ExifTaken = r.IsDBNull(10) ? null : r.GetInt64(10),
        ExifCamera = r.IsDBNull(11) ? null : r.GetString(11),
        ThumbPath = r.IsDBNull(12) ? null : r.GetString(12),
        AnalyzedAt = r.IsDBNull(13) ? null : r.GetInt64(13),
        DupeCheckedAt = r.IsDBNull(14) ? null : r.GetInt64(14),
        DupeCheckedKinds = r.IsDBNull(15) ? DupeKinds.None : (DupeKinds)r.GetInt32(15),
        Phash = r.IsDBNull(16) ? null : (byte[])r[16],
    };
}
