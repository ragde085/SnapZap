using Microsoft.Data.Sqlite;
using SnapZap.Core.Data;

namespace SnapZap.Core.Dedup;

/// <summary>
/// Exact-duplicate detection straight from the SHA-256 content hashes already in the
/// catalog — no external tool required (DESIGN §2 note). Czkawka is reserved for *similar*
/// (perceptual) detection; byte-identical files are found here for free and always work.
///
/// The auto-keeper heuristic (best = highest resolution → largest file → lowest id) picks a
/// default keeper per group; the user can override in the UI (step 6).
/// </summary>
public sealed class ExactDuplicateFinder(Database db)
{
    /// <param name="root">Restrict to photos beneath this folder; null covers the catalogue.
    /// Two identical photos in different libraries are not a duplicate the user can act on
    /// while only one of those libraries is on screen.</param>
    public int FindAndStore(string? root = null)
    {
        var repo = new DupeRepository(db);
        repo.ClearKind(DupeKind.Exact, root);

        // Gather members of every hash that appears more than once.
        var byHash = new Dictionary<string, List<(long id, long pixels, long size)>>();
        using (var cmd = db.Connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT content_hash, id,
                       COALESCE(width,0) * COALESCE(height,0) AS pixels,
                       file_size
                FROM images
                WHERE {PathScope.Where(root)}
                  AND content_hash IN (
                    SELECT content_hash FROM images
                    WHERE {PathScope.Where(root)}
                    GROUP BY content_hash HAVING COUNT(*) > 1
                  )
                """;
            PathScope.Bind(cmd, root);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var h = r.GetString(0);
                (byHash.TryGetValue(h, out var list) ? list : byHash[h] = [])
                    .Add((r.GetInt64(1), r.GetInt64(2), r.GetInt64(3)));
            }
        }

        int groupCount = 0;
        using var tx = db.Connection.BeginTransaction();
        foreach (var (_, members) in byHash)
        {
            // Keeper = most pixels, then largest bytes, then lowest id (stable).
            var keeperId = members
                .OrderByDescending(m => m.pixels)
                .ThenByDescending(m => m.size)
                .ThenBy(m => m.id)
                .First().id;

            repo.AddGroup(DupeKind.Exact, similarity: "identical",
                members.Select(m => (m.id, m.id == keeperId)).ToList());
            groupCount++;
        }
        tx.Commit();
        return groupCount;
    }
}
