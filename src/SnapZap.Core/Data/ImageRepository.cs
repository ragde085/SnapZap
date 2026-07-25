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

    /// <summary>Delete rows whose path no longer exists on disk (pruned during a scan).</summary>
    public int DeleteMissing(IReadOnlyCollection<string> livePaths)
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
        using var del = _c.CreateCommand();
        del.CommandText = "DELETE FROM images WHERE path NOT IN (SELECT path FROM _live)";
        return del.ExecuteNonQuery();
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

    public IEnumerable<ImageRecord> All()
    {
        using var cmd = _c.CreateCommand();
        cmd.CommandText = """
            SELECT id, path, content_hash, file_size, mtime, width, height, format,
                   nsfw_score, blur_score, exif_taken, exif_camera, thumb_path, analyzed_at
            FROM images ORDER BY id
            """;
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
