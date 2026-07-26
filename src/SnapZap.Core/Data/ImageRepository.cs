using Microsoft.Data.Sqlite;

namespace SnapZap.Core.Data;

/// <summary>Reads/writes the <c>images</c> table.</summary>
public sealed class ImageRepository(Database db)
{
    readonly SqliteConnection _c = db.Connection;

    /// <summary>The cheap cache probe (DESIGN §5 tier 1): identity of a file without hashing.
    /// Returns null when the path is unknown.</summary>
    public (long id, long size, long mtime, bool analyzed)? Probe(string path)
    {
        using var cmd = _c.CreateCommand();
        cmd.CommandText = "SELECT id, file_size, mtime, analyzed_at FROM images WHERE path=$p";
        cmd.Parameters.AddWithValue("$p", path);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), !r.IsDBNull(3));
    }

    /// <summary>Insert or replace a fully analyzed image row. Returns the row id.</summary>
    public long Upsert(ImageRecord img)
    {
        using var cmd = _c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO images
              (path, content_hash, file_size, mtime, width, height, format,
               nsfw_score, blur_score, exif_taken, exif_camera, thumb_path, analyzed_at)
            VALUES
              ($path,$hash,$size,$mtime,$w,$h,$fmt,$nsfw,$blur,$taken,$camera,$thumb,$at)
            ON CONFLICT(path) DO UPDATE SET
              content_hash=excluded.content_hash, file_size=excluded.file_size,
              mtime=excluded.mtime, width=excluded.width, height=excluded.height,
              format=excluded.format, nsfw_score=excluded.nsfw_score,
              blur_score=excluded.blur_score, exif_taken=excluded.exif_taken,
              exif_camera=excluded.exif_camera, thumb_path=excluded.thumb_path,
              analyzed_at=excluded.analyzed_at
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$path", img.Path);
        cmd.Parameters.AddWithValue("$hash", img.ContentHash);
        cmd.Parameters.AddWithValue("$size", img.FileSize);
        cmd.Parameters.AddWithValue("$mtime", img.Mtime);
        cmd.Parameters.AddWithValue("$w", (object?)img.Width ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$h", (object?)img.Height ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fmt", (object?)img.Format ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$nsfw", (object?)img.NsfwScore ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$blur", (object?)img.BlurScore ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$taken", (object?)img.ExifTaken ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$camera", (object?)img.ExifCamera ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$thumb", (object?)img.ThumbPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", (object?)img.AnalyzedAt ?? DBNull.Value);
        return (long)cmd.ExecuteScalar()!;
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
        // Build a temp table of live paths and delete the complement. Cheap for large sets.
        using (var tmp = _c.CreateCommand())
        {
            tmp.CommandText = "CREATE TEMP TABLE IF NOT EXISTS _live(path TEXT PRIMARY KEY); DELETE FROM _live;";
            tmp.ExecuteNonQuery();
        }
        using (var tx = _c.BeginTransaction())
        {
            using var ins = _c.CreateCommand();
            ins.CommandText = "INSERT OR IGNORE INTO _live(path) VALUES ($p)";
            var p = ins.Parameters.Add("$p", SqliteType.Text);
            foreach (var path in livePaths) { p.Value = path; ins.ExecuteNonQuery(); }
            tx.Commit();
        }

        // Prefix-compare with substr rather than LIKE: a folder called "50%_off" or "a_b" is a
        // legal directory name and would be read as wildcards.
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;

        // Inside the root: the enumeration is authoritative, so anything absent from it is gone.
        int removed;
        using (var del = _c.CreateCommand())
        {
            del.CommandText = """
                DELETE FROM images
                 WHERE substr(path, 1, length($prefix)) = $prefix
                   AND path NOT IN (SELECT path FROM _live)
                """;
            del.Parameters.AddWithValue("$prefix", prefix);
            removed = del.ExecuteNonQuery();
        }

        // Outside the root: the scan saw nothing, so ask the filesystem directly.
        var strays = new List<string>();
        using (var q = _c.CreateCommand())
        {
            q.CommandText = "SELECT path FROM images WHERE substr(path, 1, length($prefix)) <> $prefix";
            q.Parameters.AddWithValue("$prefix", prefix);
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                var path = r.GetString(0);
                if (!File.Exists(path)) strays.Add(path);
            }
        }
        if (strays.Count > 0)
        {
            using var tx = _c.BeginTransaction();
            using var del = _c.CreateCommand();
            del.CommandText = "DELETE FROM images WHERE path = $p";
            var p = del.Parameters.Add("$p", SqliteType.Text);
            foreach (var path in strays) { p.Value = path; removed += del.ExecuteNonQuery(); }
            tx.Commit();
        }
        return removed;
    }

    /// <summary>Map absolute path → row id, pixel count and byte size, for correlating
    /// external tool output and picking a keeper among its matches.</summary>
    public Dictionary<string, (long id, long pixels, long bytes)> PathIndex()
    {
        var map = new Dictionary<string, (long, long, long)>(StringComparer.Ordinal);
        using var cmd = _c.CreateCommand();
        cmd.CommandText = "SELECT path, id, COALESCE(width,0)*COALESCE(height,0), file_size FROM images";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            map[r.GetString(0)] = (r.GetInt64(1), r.GetInt64(2), r.GetInt64(3));
        return map;
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
        using var cmd = _c.CreateCommand();
        cmd.CommandText = "UPDATE images SET path = $p WHERE id = $id";
        cmd.Parameters.AddWithValue("$p", newPath);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public long Count()
    {
        using var cmd = _c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM images";
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>Fetch specific images by id, in the given order (unknown ids dropped).</summary>
    public IReadOnlyList<ImageRecord> ByIds(IEnumerable<long> ids)
    {
        var byId = All().ToDictionary(i => i.Id);
        var outp = new List<ImageRecord>();
        foreach (var id in ids)
            if (byId.TryGetValue(id, out var img)) outp.Add(img);
        return outp;
    }

    /// <summary>How many photos the catalogue holds in total, across every folder.</summary>
    public int TotalCount()
    {
        using var cmd = _c.CreateCommand();
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
        using var tx = _c.BeginTransaction();
        using (var cmd = _c.CreateCommand())
        {
            // dupe_members and export_items cascade from these two.
            cmd.CommandText = "DELETE FROM images; DELETE FROM dupe_groups; DELETE FROM export_runs;";
            cmd.ExecuteNonQuery();
        }
        tx.Commit();

        // Deleting rows does not shrink the file, and the dialog that offers this itemises the
        // database's size as something being reclaimed. Outside the transaction, because VACUUM
        // cannot run inside one — and best-effort, because failing to shrink a file is not a
        // failure to forget. Throwing here would abandon the caller midway, leaving the rows
        // gone but the scan scope and the thumbnails still in place.
        try
        {
            using var vacuum = _c.CreateCommand();
            // VACUUM first, checkpoint second. In WAL mode the rebuilt database lands in the
            // write-ahead log, so checkpointing beforehand truncates the old content and leaves
            // the new content in the WAL — the page count drops to nothing while the file on
            // disk stays exactly as large as it was, which is the one thing this is here to fix.
            vacuum.CommandText = "VACUUM; PRAGMA wal_checkpoint(TRUNCATE);";
            vacuum.ExecuteNonQuery();
        }
        catch (SqliteException) { /* no free space, or another connection is mid-read */ }
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
        using var cmd = _c.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, path, content_hash, file_size, mtime, width, height, format,
                   nsfw_score, blur_score, exif_taken, exif_camera, thumb_path, analyzed_at
            FROM images
            WHERE {PathScope.Where(root)}
            ORDER BY id
            """;
        PathScope.Bind(cmd, root);

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            yield return new ImageRecord
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
            };
        }
    }
}
