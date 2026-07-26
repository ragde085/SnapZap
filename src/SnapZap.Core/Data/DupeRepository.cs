using Microsoft.Data.Sqlite;

namespace SnapZap.Core.Data;

/// <summary>Reads/writes duplicate groups and their members.</summary>
public sealed class DupeRepository(Database db)
{
    readonly SqliteConnection _c = db.Connection;

    /// <summary>Remove all groups of a given kind (a fresh detection run replaces them).</summary>
    /// <param name="root">
    /// Only drop groups lying entirely under this folder; null clears the kind outright.
    /// </param>
    /// <remarks>
    /// Re-detecting inside one folder used to clear every group in the catalogue, so running
    /// dedup on folder B threw away folder A's reviewed results with no message.
    ///
    /// "Entirely under", not "any member under": a group straddling two libraries would
    /// otherwise be deleted whole — members cascade — taking the out-of-scope copy's duplicate
    /// status with it. A scoped rebuild cannot put that back, because it filters both sides of
    /// its own hash match to the same root, so the cross-folder pairing was unrecoverable
    /// without an unscoped rescan the UI no longer offers. A straddling group left alone is
    /// harmless: LoadAsync drops any group with fewer than two members still in view.
    /// </remarks>
    public void ClearKind(DupeKind kind, string? root = null)
    {
        using var cmd = _c.CreateCommand();
        // dupe_members cascades on group delete.
        cmd.CommandText = root is null
            ? "DELETE FROM dupe_groups WHERE kind=$k"
            : $"""
               DELETE FROM dupe_groups
                WHERE kind=$k
                  AND NOT EXISTS (SELECT 1 FROM dupe_members m
                                    JOIN images i ON i.id = m.image_id
                                   WHERE m.group_id = dupe_groups.id
                                     AND NOT {PathScope.Sql})
               """;
        cmd.Parameters.AddWithValue("$k", kind.ToString().ToLowerInvariant());
        PathScope.Bind(cmd, root);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Delete one group by id. Members cascade.</summary>
    public void DeleteGroup(long groupId)
    {
        using var cmd = _c.CreateCommand();
        cmd.CommandText = "DELETE FROM dupe_groups WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", groupId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Insert one group with its members. The first keeper is flagged; caller decides.</summary>
    public long AddGroup(DupeKind kind, string? similarity, IReadOnlyList<(long imageId, bool keeper)> members)
    {
        using var g = _c.CreateCommand();
        g.CommandText = "INSERT INTO dupe_groups(kind, similarity) VALUES ($k,$s) RETURNING id";
        g.Parameters.AddWithValue("$k", kind.ToString().ToLowerInvariant());
        g.Parameters.AddWithValue("$s", (object?)similarity ?? DBNull.Value);
        var groupId = (long)g.ExecuteScalar()!;

        using var m = _c.CreateCommand();
        m.CommandText = "INSERT INTO dupe_members(group_id, image_id, is_keeper) VALUES ($g,$i,$keep)";
        var pi = m.Parameters.Add("$i", SqliteType.Integer);
        var pk = m.Parameters.Add("$keep", SqliteType.Integer);
        m.Parameters.AddWithValue("$g", groupId);
        foreach (var (imageId, keeper) in members)
        {
            pi.Value = imageId;
            pk.Value = keeper ? 1 : 0;
            m.ExecuteNonQuery();
        }
        return groupId;
    }

    /// <summary>
    /// Make one member the keeper, clearing the flag from the rest of its group. The detector
    /// picks a keeper by pixel count, which is a reasonable default and a poor decision for
    /// crops, edits, or the one shot that happens to be the good one — this is the override.
    /// </summary>
    public void SetKeeper(long groupId, long keeperImageId)
    {
        using var tx = _c.BeginTransaction();

        using (var clear = _c.CreateCommand())
        {
            clear.CommandText = "UPDATE dupe_members SET is_keeper=0 WHERE group_id=$g";
            clear.Parameters.AddWithValue("$g", groupId);
            clear.ExecuteNonQuery();
        }
        using (var set = _c.CreateCommand())
        {
            set.CommandText = "UPDATE dupe_members SET is_keeper=1 WHERE group_id=$g AND image_id=$i";
            set.Parameters.AddWithValue("$g", groupId);
            set.Parameters.AddWithValue("$i", keeperImageId);
            set.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public IReadOnlyList<DupeGroup> Groups(DupeKind? kind = null)
    {
        var groups = new List<DupeGroup>();
        using var cmd = _c.CreateCommand();
        cmd.CommandText = kind is null
            ? "SELECT id, kind, similarity FROM dupe_groups ORDER BY id"
            : "SELECT id, kind, similarity FROM dupe_groups WHERE kind=$k ORDER BY id";
        if (kind is not null)
            cmd.Parameters.AddWithValue("$k", kind.Value.ToString().ToLowerInvariant());

        var meta = new List<(long id, DupeKind kind, string? sim)>();
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                // Skip rather than throw on a kind this build does not know. A catalogue written
                // by a newer version has to degrade to "some groups are not shown", not to an
                // exception on the code path that loads the grid.
                if (!Enum.TryParse<DupeKind>(r.GetString(1), ignoreCase: true, out var k)) continue;
                meta.Add((r.GetInt64(0), k, r.IsDBNull(2) ? null : r.GetString(2)));
            }

        foreach (var (id, k, sim) in meta)
        {
            using var mc = _c.CreateCommand();
            mc.CommandText = "SELECT image_id, is_keeper FROM dupe_members WHERE group_id=$g";
            mc.Parameters.AddWithValue("$g", id);
            var members = new List<DupeMember>();
            using var mr = mc.ExecuteReader();
            while (mr.Read())
                members.Add(new DupeMember { ImageId = mr.GetInt64(0), IsKeeper = mr.GetInt64(1) != 0 });
            groups.Add(new DupeGroup { Id = id, Kind = k, Similarity = sim, Members = members });
        }
        return groups;
    }
}
