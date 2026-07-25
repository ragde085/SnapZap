using Microsoft.Data.Sqlite;

namespace SnapZap.Core.Data;

/// <summary>Reads/writes duplicate groups and their members.</summary>
public sealed class DupeRepository(Database db)
{
    readonly SqliteConnection _c = db.Connection;

    /// <summary>Remove all groups of a given kind (a fresh detection run replaces them).</summary>
    public void ClearKind(DupeKind kind)
    {
        using var cmd = _c.CreateCommand();
        // dupe_members cascades on group delete.
        cmd.CommandText = "DELETE FROM dupe_groups WHERE kind=$k";
        cmd.Parameters.AddWithValue("$k", kind.ToString().ToLowerInvariant());
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
                meta.Add((r.GetInt64(0),
                          Enum.Parse<DupeKind>(r.GetString(1), ignoreCase: true),
                          r.IsDBNull(2) ? null : r.GetString(2)));

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
