using Microsoft.Data.Sqlite;
using SnapZap.Core.Dedup;

namespace SnapZap.Core.Data;

/// <summary>Result of the eager phash-recipe check run once at catalogue open (see
/// <see cref="Database.EnsurePhashRecipe"/>).</summary>
public sealed record PhashRecipeMigration(bool Migrated, int RowsInvalidated, string? BackupPath);

/// <summary>
/// Owns the SQLite connections and schema (DESIGN.md §5). One database file per catalog;
/// WAL mode so reads (the SPA, the HTTP endpoints) don't block the writer (the scanner).
/// </summary>
/// <remarks>
/// <b>Connection ownership.</b> <c>SqliteConnection</c> is not thread-safe, so no single instance
/// is ever shared across threads. Reads open a fresh, cheap, pooled connection per operation via
/// <see cref="OpenRead"/> — WAL lets these run concurrently with the writer. Writes funnel through
/// one dedicated <see cref="Writer"/> connection, serialized by <see cref="WriteLock"/>; a plain
/// C# <c>lock</c> is re-entrant for the same thread, so a caller composing several writes into one
/// transaction (e.g. <c>StoreGroups.Write</c>) can safely lock once around the whole operation
/// while the individual repository methods it calls also lock internally.
/// </remarks>
public sealed class Database : IDisposable
{
    readonly string _connectionString;
    readonly string _dbPath;

    /// <summary>The single connection every write goes through. Never touch this without holding
    /// <see cref="WriteLock"/> — it is not safe to use from more than one thread at a time.</summary>
    public SqliteConnection Writer { get; }

    /// <summary>Guards <see cref="Writer"/>. Re-entrant (same thread) by ordinary C# <c>lock</c>
    /// semantics, so composing multiple write calls into one transaction is safe.</summary>
    public object WriteLock { get; } = new();

    /// <summary>Set once at construction if opening this catalogue found a stale
    /// <c>phash.recipe</c> and cleared signatures catalogue-wide. Null on every launch after the
    /// first (idempotent — the whole point of AC 6a).</summary>
    public PhashRecipeMigration? RecipeMigration { get; }

    public Database(string dbPath)
    {
        _dbPath = dbPath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Shared-cache adds table-level locking BETWEEN connections, working directly against
            // the per-operation connection model below and against WAL's own concurrency.
        }.ToString();

        Writer = OpenConnectionCore();
        Migrate();
        RecipeMigration = EnsurePhashRecipe(PerceptualHash.PhashRecipeVersion);
    }

    SqliteConnection OpenConnectionCore()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        ApplyPragmas(c);
        return c;
    }

    /// <summary>Pragmas are per-connection (session) state in SQLite, except <c>journal_mode</c>
    /// which is persisted in the file itself — re-asserting it here is harmless and keeps every
    /// connection's setup identical.</summary>
    static void ApplyPragmas(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA foreign_keys=ON;
            PRAGMA temp_store=MEMORY;
            PRAGMA cache_size=-20000;
            PRAGMA mmap_size=268435456;
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Opens a fresh connection for one read operation. Cheap: Microsoft.Data.Sqlite
    /// pools physical connections by connection string. Caller disposes.</summary>
    public SqliteConnection OpenRead() => OpenConnectionCore();

    /// <summary>A catalogue-wide setting, or null when unset.</summary>
    public string? Meta(string key)
    {
        using var c = OpenRead();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Writes a catalogue-wide setting; null removes it.</summary>
    public void SetMeta(string key, string? value)
    {
        lock (WriteLock)
        {
            using var cmd = Writer.CreateCommand();
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
    }

    void Migrate()
    {
        lock (WriteLock)
        {
            Exec(Schema);

            // Columns added after the first release. CREATE TABLE IF NOT EXISTS is a no-op against
            // an existing catalogue, so adding a column to Schema below reaches new databases only —
            // every already-created one needs a guarded ALTER here too.
            AddColumnIfMissing("images", "dupe_checked_at", "INTEGER");
            AddColumnIfMissing("images", "dupe_checked_kinds", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing("images", "phash", "BLOB");
            AddColumnIfMissing("images", "nsfw_tile_mean", "REAL");
            AddColumnIfMissing("images", "burst_adjacent", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing("undo_log", "content_hash", "TEXT");

            RenameSimilarToVariant();
        }
    }

    void Exec(string sql)
    {
        using var cmd = Writer.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
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
        using (var q = Writer.CreateCommand())
        {
            q.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $n";
            q.Parameters.AddWithValue("$n", column);
            if (Convert.ToInt64(q.ExecuteScalar()) > 0) return;
        }
        Exec($"ALTER TABLE {table} ADD COLUMN {column} {declaration};");
    }

    const string PhashRecipeMetaKey = "phash.recipe";

    /// <summary>
    /// Structural signature invalidation (tech-spec Task 13): if the stored <c>phash.recipe</c>
    /// differs from <see cref="PerceptualHash.PhashRecipeVersion"/>, clear every stored signature
    /// and duplicate-checked flag catalogue-wide and record the new version. Runs once, eagerly,
    /// at catalogue open — no image is decoded here, so this never delays app launch regardless of
    /// catalogue size (AC 6a). The (expensive) recompute is a separate, lazy backfill phase inside
    /// <see cref="Dedup.DuplicateService.DetectAsync"/>, driven by <c>NeedingPhash</c>/<c>SetPhash</c>.
    /// </summary>
    /// <remarks>
    /// Anything that changes how a signature is derived — decode scale, resampling filter,
    /// orientation handling, grid size — must bump <see cref="PerceptualHash.PhashRecipeVersion"/>.
    /// The tier-1 probe skips unchanged files, so a signature this method doesn't clear is never
    /// recomputed on its own: old-recipe and new-recipe hashes would sit in one catalogue being
    /// compared against each other, which is silent matching degradation with no error and no
    /// user-visible signal.
    /// </remarks>
    PhashRecipeMigration EnsurePhashRecipe(int targetVersion)
    {
        var stored = Meta(PhashRecipeMetaKey);
        if (stored is not null && int.TryParse(stored, out var v) && v == targetVersion)
            return new PhashRecipeMigration(false, 0, null);

        lock (WriteLock)
        {
            long rowCount;
            using (var count = Writer.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM images";
                rowCount = (long)count.ExecuteScalar()!;
            }

            // Forward-only by design: reverting the code means restoring this file. Only taken
            // when there is data to protect, and before the UPDATE below touches anything.
            // VACUUM INTO produces a complete, consistent single-file copy regardless of what's
            // currently in the WAL, unlike a raw file copy of catalog.db alone.
            string? backupPath = null;
            if (rowCount > 0)
            {
                // Only the most recent backup is ever useful (reverting means restoring the file
                // from just before the last bump, not every bump this catalogue has ever seen) —
                // prune prior ones first, or every future recipe bump leaves another full copy of
                // the catalogue on disk forever with nothing surfaced to the user about any of them.
                PruneOldBackups();

                // A Guid suffix, not just the timestamp: two bumps landing in the same release
                // (or the same test) can fall in the same second, and VACUUM INTO refuses to
                // overwrite an existing file.
                backupPath = _dbPath +
                    $".bak-recipe-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{Guid.NewGuid():N}";
                using var vacuum = Writer.CreateCommand();
                vacuum.CommandText = "VACUUM INTO $path";
                vacuum.Parameters.AddWithValue("$path", backupPath);
                vacuum.ExecuteNonQuery();
            }

            int affected;
            using (var cmd = Writer.CreateCommand())
            {
                cmd.CommandText = "UPDATE images SET phash=NULL, dupe_checked_at=NULL, dupe_checked_kinds=0";
                affected = cmd.ExecuteNonQuery();
            }

            using (var meta = Writer.CreateCommand())
            {
                meta.CommandText = """
                    INSERT INTO meta(key, value) VALUES($k, $v)
                    ON CONFLICT(key) DO UPDATE SET value=excluded.value
                    """;
                meta.Parameters.AddWithValue("$k", PhashRecipeMetaKey);
                meta.Parameters.AddWithValue("$v", targetVersion.ToString());
                meta.ExecuteNonQuery();
            }

            return new PhashRecipeMigration(true, affected, backupPath);
        }
    }

    /// <summary>Deletes every prior <c>.bak-recipe-*</c> file beside the catalogue. Best-effort —
    /// a leftover old backup is disk-usage debt, not a broken migration, so a locked or
    /// already-gone file is not worth failing catalogue open over.</summary>
    void PruneOldBackups()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (string.IsNullOrEmpty(dir)) return;
        var pattern = Path.GetFileName(_dbPath) + ".bak-recipe-*";
        foreach (var old in Directory.EnumerateFiles(dir, pattern))
        {
            try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Test-only hook: runs the same invalidate-if-different logic against an arbitrary
    /// target version, so a sequence of recipe bumps (AC 6b: two bumps, one backfill) can be
    /// exercised without redefining <see cref="PerceptualHash.PhashRecipeVersion"/> itself.</summary>
    internal PhashRecipeMigration EnsurePhashRecipeForTest(int targetVersion) => EnsurePhashRecipe(targetVersion);

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
          -- Mean score across the 3x3 tiles (NsfwDecision.Tiles). Null means this row has only
          -- ever been scored whole-frame, which is what lets a deeper re-run find the rows it
          -- still owes work on — the same job dupe_checked_kinds does for duplicate detection.
          nsfw_tile_mean REAL,
          blur_score    REAL,
          exif_taken    INTEGER,
          exif_camera   TEXT,
          thumb_path    TEXT,
          analyzed_at   INTEGER,
          -- When duplicate detection last covered this row. Null means "never checked", which is
          -- what lets the folder tree tell a folder with no duplicates apart from one nobody has
          -- looked at yet. Cleared by Upsert, so re-analysing a changed file makes it stale again.
          -- 1 when BurstFinder saw this photo in a qualifying burst relationship with another —
          -- same camera, inside the EXIF window, visually close — regardless of whether it made
          -- it into a burst *group*. Grouping is complete-linkage, so a real burst frame that sits
          -- too far from the far end of the run is legitimately excluded from the clique and then
          -- described only as a Variant, where it becomes bulk-selectable. This flag is what stops
          -- that: it protects the photo without touching how groups are formed. See BurstFinder.
          burst_adjacent INTEGER NOT NULL DEFAULT 0,
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
          restored      INTEGER NOT NULL DEFAULT 0,
          content_hash  TEXT
        );
        """;

    public void Dispose() => Writer.Dispose();
}
