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

    void Migrate()
    {
        Exec(Schema);

        // Columns added after the first release. CREATE TABLE IF NOT EXISTS is a no-op against an
        // existing catalogue, so adding a column to Schema below reaches new databases only —
        // every already-created one needs a guarded ALTER here too.
        AddColumnIfMissing("images", "dupe_checked_at", "INTEGER");
        AddColumnIfMissing("images", "dupe_checked_kinds", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("images", "phash", "BLOB");

        RenameSimilarToVariant();
    }

    /// <summary>
    /// Carry pre-v2 groups across the <c>Similar</c> → <c>Variant</c> rename.
    /// </summary>
    /// <remarks>
    /// Rewritten rather than discarded. czkawka produced these at comparable strictness, and a
    /// group carries the keeper the user chose — possibly after an evening of reviewing. Dropping
    /// them would silently throw that away and present a re-detect as the only way back.
    ///
    /// The rows are left in place if a 'variant' kind already exists, so this runs exactly once
    /// per catalogue no matter how often the app restarts.
    /// </remarks>
    void RenameSimilarToVariant() =>
        Exec("UPDATE dupe_groups SET kind='variant' WHERE kind='similar';");

    /// <summary>Idempotent ALTER: SQLite has no ADD COLUMN IF NOT EXISTS.</summary>
    void AddColumnIfMissing(string table, string column, string declaration)
    {
        using (var q = Connection.CreateCommand())
        {
            q.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $n";
            q.Parameters.AddWithValue("$n", column);
            if (Convert.ToInt64(q.ExecuteScalar()) > 0) return;
        }
        Exec($"ALTER TABLE {table} ADD COLUMN {column} {declaration};");
    }

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
          analyzed_at   INTEGER,
          -- When duplicate detection last covered this row. Null means "never checked", which is
          -- what lets the folder tree tell a folder with no duplicates apart from one nobody has
          -- looked at yet. Cleared by Upsert, so re-analysing a changed file makes it stale again.
          dupe_checked_at INTEGER,
          -- Which detectors that run covered (DupeKinds bitmask). The timestamp alone lies once
          -- detectors are configurable — enabling one later must re-flag folders as pending.
          dupe_checked_kinds INTEGER NOT NULL DEFAULT 0,
          -- 160-byte perceptual signature: 4 rotations x 5 x 64 bits. Null = not hashed yet.
          -- Cleared by Upsert alongside dupe_checked_at: a file whose bytes moved needs re-hashing.
          phash         BLOB
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
