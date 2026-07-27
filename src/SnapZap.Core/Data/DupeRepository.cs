using Microsoft.Data.Sqlite;

namespace SnapZap.Core.Data;

/// <summary>Reads/writes duplicate groups and their members.</summary>
public sealed class DupeRepository(Database db)
{
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
        lock (db.WriteLock)
        {
            using var cmd = db.Writer.CreateCommand();
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
    }

    /// <summary>Delete one group by id. Members cascade.</summary>
    public void DeleteGroup(long groupId)
    {
        lock (db.WriteLock)
        {
            using var cmd = db.Writer.CreateCommand();
            cmd.CommandText = "DELETE FROM dupe_groups WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", groupId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Insert one group with its members. The first keeper is flagged; caller decides.</summary>
    public long AddGroup(DupeKind kind, string? similarity, IReadOnlyList<(long imageId, bool keeper)> members)
    {
        lock (db.WriteLock)
        {
            var c = db.Writer;
            using var g = c.CreateCommand();
            g.CommandText = "INSERT INTO dupe_groups(kind, similarity) VALUES ($k,$s) RETURNING id";
            g.Parameters.AddWithValue("$k", kind.ToString().ToLowerInvariant());
            g.Parameters.AddWithValue("$s", (object?)similarity ?? DBNull.Value);
            var groupId = (long)g.ExecuteScalar()!;

            using var m = c.CreateCommand();
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
    }

    /// <summary>
    /// Flip one member's keeper flag, without touching the rest of its group. The detector picks
    /// a single keeper by pixel count, which is a reasonable default and a poor decision for
    /// crops, edits, or a burst where more than one frame is worth keeping — this is the
    /// override, and more than one member may be kept at once.
    /// </summary>
    /// <remarks>
    /// Refuses to turn off the group's last remaining keeper: a group must always survive with
    /// at least one copy, the same invariant <c>AppState.BuildLoadSnapshot</c> falls back to when
    /// a keeper gets recycled out from under it. Silently a no-op if <paramref name="imageId"/>
    /// isn't a member of <paramref name="groupId"/> — callers only ever pass a member they read
    /// off the group itself, so this is a defend-in-depth check, not an expected path.
    /// </remarks>
    public void ToggleKeeper(long groupId, long imageId)
    {
        lock (db.WriteLock)
        {
            var c = db.Writer;
            using var tx = c.BeginTransaction();

            long? current;
            using (var read = c.CreateCommand())
            {
                read.CommandText = "SELECT is_keeper FROM dupe_members WHERE group_id=$g AND image_id=$i";
                read.Parameters.AddWithValue("$g", groupId);
                read.Parameters.AddWithValue("$i", imageId);
                current = (long?)read.ExecuteScalar();
            }
            if (current is null) return;

            var isKeeper = current.Value != 0;
            if (isKeeper)
            {
                using var count = c.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM dupe_members WHERE group_id=$g AND is_keeper=1";
                count.Parameters.AddWithValue("$g", groupId);
                if ((long)count.ExecuteScalar()! <= 1) return;
            }

            using (var set = c.CreateCommand())
            {
                set.CommandText = "UPDATE dupe_members SET is_keeper=$v WHERE group_id=$g AND image_id=$i";
                set.Parameters.AddWithValue("$v", isKeeper ? 0 : 1);
                set.Parameters.AddWithValue("$g", groupId);
                set.Parameters.AddWithValue("$i", imageId);
                set.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public IReadOnlyList<DupeGroup> Groups(DupeKind? kind = null)
    {
        var groups = new List<DupeGroup>();
        using var c = db.OpenRead();
        using var cmd = c.CreateCommand();
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
            using var mc = c.CreateCommand();
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
