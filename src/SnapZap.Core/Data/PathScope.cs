using Microsoft.Data.Sqlite;

namespace SnapZap.Core.Data;

/// <summary>
/// The single definition of "inside the folder the session is working on", shared by every
/// query that has to respect it.
/// </summary>
/// <remarks>
/// It exists because scoping the *view* without scoping the *work* is worse than not scoping
/// at all: with a four-photo folder open, Score NSFW went off and scored 3,890 photos in
/// folders the user could neither see nor have consented to, and Find duplicates reported
/// "3 exact groups" for a folder whose duplicate count on screen was zero. One predicate, one
/// binding, so a query cannot quietly disagree with the grid about what is in scope.
///
/// Prefix comparison uses substr rather than LIKE: "50%_off" is a legal directory name, not a
/// wildcard. The separator is the platform's, because these are raw filesystem paths as
/// stored — not the '/'-normalised form the UI displays.
/// </remarks>
public static class PathScope
{
    /// <summary>
    /// Predicate over an <c>images</c> row. Bind with <see cref="Bind"/>.
    /// </summary>
    /// <remarks>Prefix only: a root is always a directory and every stored path is a file, so
    /// an equality branch against the root itself could never match.</remarks>
    public const string Sql = "substr(path, 1, length($prefix)) = $prefix";

    /// <summary><see cref="Sql"/>, or "everything" when no root is set — which is the state of
    /// a catalogue written before scoping existed, and of one that has never been scanned.</summary>
    public static string Where(string? root) => string.IsNullOrEmpty(root) ? "1=1" : Sql;

    public static void Bind(SqliteCommand cmd, string? root)
    {
        if (string.IsNullOrEmpty(root)) return;
        cmd.Parameters.AddWithValue("$prefix",
            root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar);
    }
}
