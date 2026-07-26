using Microsoft.Data.Sqlite;
using SnapZap.Core.Data;

namespace SnapZap.Core.Dedup;

/// <summary>
/// Exact-duplicate detection straight from the SHA-256 content hashes already in the catalog.
/// Perceptual matching is <see cref="VariantFinder"/>'s job; byte-identical files are found here
/// for free — and unlike every other detector this one has no setting to switch it off, because a
/// duplicate finder that cannot find identical files is not one.
///
/// The auto-keeper heuristic (best = highest resolution → largest file → first path) picks a
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
        var byHash = new Dictionary<string, List<(long id, long pixels, long size, string path)>>();
        using (var c = db.OpenRead())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT content_hash, id,
                       COALESCE(width,0) * COALESCE(height,0) AS pixels,
                       file_size, path
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
                    .Add((r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetString(4)));
            }
        }

        int groupCount = 0;
        // One lock, one transaction, for every group — re-entrant, so AddGroup's own internal
        // lock (Task 18) does not deadlock here.
        lock (db.WriteLock)
        {
            using var tx = db.Writer.BeginTransaction();
            foreach (var (_, members) in byHash)
            {
                // Keeper = most pixels, then largest bytes, then first path.
                //
                // Path is the key that actually decides here, every time: these files are
                // byte-identical by definition, so pixels and bytes always tie. It has to be a value
                // the library itself determines — id is assigned in parallel scan-completion order, so
                // tie-breaking on it handed a different photo the keeper flag on every fresh scan of
                // an unchanged folder. Same reasoning as StoreGroups.Keeper; both must stay in step.
                var keeperId = members
                    .OrderByDescending(m => m.pixels)
                    .ThenByDescending(m => m.size)
                    .ThenBy(m => m.path, StringComparer.Ordinal)
                    .First().id;

                repo.AddGroup(DupeKind.Exact, similarity: "identical",
                    members.Select(m => (m.id, m.id == keeperId)).ToList());
                groupCount++;
            }
            tx.Commit();
        }
        return groupCount;
    }
}
