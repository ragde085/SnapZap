using Microsoft.Data.Sqlite;

namespace SnapZap.Core.Data;

/// <summary>
/// Owns the SQLite connection and schema (DESIGN.md §5). One database file per catalog;
/// WAL mode so reads (the SPA) don't block the writer (the scanner).
/// </summary>
public sealed class Database : IDisposable
{
    public SqliteConnection Connection { get; }

    public Database(string dbPath)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        Connection = new SqliteConnection(cs);
        Connection.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;");
        Migrate();
    }

    void Exec(string sql)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>A catalogue-wide setting, or null when unset.</summary>
    public string? Meta(string key)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Writes a catalogue-wide setting; null removes it.</summary>
    public void SetMeta(string key, string? value)
    {
        using var cmd = Connection.CreateCommand();
        if (value is null)
        {
            cmd.CommandText = "DELETE FROM meta WHERE key=$k";
            cmd.Parameters.AddWithValue("$k", key);
        }
        else
        {
            cmd.CommandText = """
                INSERT INTO meta(key, value) VALUES($k, $v)
                ON CONFLICT(key) DO UPDATE SET value=excluded.value
                """;
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
        }
        cmd.ExecuteNonQuery();
    }

    void Migrate() => Exec(Schema);

    // Kept idempotent (IF NOT EXISTS) so re-opening an existing catalog is a no-op.
    const string Schema = """
        CREATE TABLE IF NOT EXISTS images (
          id            INTEGER PRIMARY KEY,
          path          TEXT    NOT NULL UNIQUE,
          content_hash  TEXT    NOT NULL,
          file_size     INTEGER NOT NULL,
          mtime         INTEGER NOT NULL,
          width         INTEGER,
          height        INTEGER,
          format        TEXT,
          nsfw_score    REAL,
          blur_score    REAL,
          exif_taken    INTEGER,
          exif_camera   TEXT,
          thumb_path    TEXT,
          analyzed_at   INTEGER
        );
        CREATE INDEX IF NOT EXISTS ix_images_hash  ON images(content_hash);
        CREATE INDEX IF NOT EXISTS ix_images_nsfw  ON images(nsfw_score);
        CREATE INDEX IF NOT EXISTS ix_images_taken ON images(exif_taken);

        CREATE TABLE IF NOT EXISTS dupe_groups (
          id          INTEGER PRIMARY KEY,
          kind        TEXT NOT NULL,
          similarity  TEXT
        );
        CREATE TABLE IF NOT EXISTS dupe_members (
          group_id  INTEGER NOT NULL REFERENCES dupe_groups(id) ON DELETE CASCADE,
          image_id  INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
          is_keeper INTEGER NOT NULL DEFAULT 0,
          PRIMARY KEY (group_id, image_id)
        );

        CREATE TABLE IF NOT EXISTS export_runs (
          id            INTEGER PRIMARY KEY,
          started_utc   INTEGER NOT NULL,
          finished_utc  INTEGER,
          destination   TEXT NOT NULL,
          mode          TEXT NOT NULL,
          structure     TEXT NOT NULL,
          reject_action TEXT NOT NULL,
          status        TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS export_items (
          run_id      INTEGER NOT NULL REFERENCES export_runs(id) ON DELETE CASCADE,
          image_id    INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
          dest_path   TEXT,
          status      TEXT NOT NULL,
          skip_reason TEXT,
          error       TEXT,
          PRIMARY KEY (run_id, image_id)
        );

        -- Catalogue-wide settings that must travel with the catalogue rather than with the
        -- app: chiefly scan_root, the folder the current session is scoped to. Keeping it here
        -- means deleting catalog.db resets the scope along with the data it describes, and two
        -- catalogues never disagree about which folder they are showing.
        CREATE TABLE IF NOT EXISTS meta (
          key   TEXT PRIMARY KEY,
          value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS undo_log (
          id            INTEGER PRIMARY KEY,
          batch_id      TEXT    NOT NULL,
          op            TEXT    NOT NULL,
          original_path TEXT    NOT NULL,
          new_location  TEXT,
          ts_utc        INTEGER NOT NULL,
          restored      INTEGER NOT NULL DEFAULT 0
        );
        """;

    public void Dispose() => Connection.Dispose();
}
