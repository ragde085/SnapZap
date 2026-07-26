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
/// Prefix comparison is a half-open range, not LIKE and no longer substr. LIKE is out because
/// "50%_off" is a legal directory name, not a wildcard. substr was correct but not sargable —
/// wrapping the column in a function hides it from the UNIQUE index on <c>images.path</c>, so
/// every scoped query degraded to a full table scan, twice over in <c>ExactDuplicateFinder</c>
/// (outer predicate plus the HAVING subquery). The range form compares the bare column and can
/// use that index.
///
/// The separator is the platform's, because these are raw filesystem paths as stored — not the
/// '/'-normalised form the UI displays. The upper bound is the prefix with its trailing
/// separator incremented by one code unit, which under SQLite's default BINARY collation admits
/// exactly the strings starting with the prefix: nothing sorts between "…\" and "…]".
/// </remarks>
public static class PathScope
{
    /// <summary>
    /// Predicate over an <c>images</c> row. Bind with <see cref="Bind"/>.
    /// </summary>
    /// <remarks>
    /// Parenthesised, and it has to stay that way: callers embed it under <c>NOT</c>
    /// (<see cref="DupeRepository.ClearKind"/>), and an unbracketed two-clause AND would bind the
    /// negation to the first comparison alone and silently invert the scope.
    ///
    /// Prefix only: a root is always a directory and every stored path is a file, so an equality
    /// branch against the root itself could never match.
    /// </remarks>
    public const string Sql = "(path >= $prefix AND path < $prefixEnd)";

    /// <summary><see cref="Sql"/>, or "everything" when no root is set — which is the state of
    /// a catalogue written before scoping existed, and of one that has never been scanned.</summary>
    public static string Where(string? root) => string.IsNullOrEmpty(root) ? "1=1" : Sql;

    public static void Bind(SqliteCommand cmd, string? root)
    {
        if (string.IsNullOrEmpty(root)) return;
        var prefix = Prefix(root);
        cmd.Parameters.AddWithValue("$prefix", prefix);
        cmd.Parameters.AddWithValue("$prefixEnd", End(prefix));
    }

    /// <summary>The stored-path prefix that every file under <paramref name="root"/> starts with.</summary>
    public static string Prefix(string root) =>
        root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        + Path.DirectorySeparatorChar;

    /// <summary>Exclusive upper bound of the prefix range, for BINARY-collated comparison.</summary>
    public static string End(string prefix) => prefix[..^1] + (char)(prefix[^1] + 1);
}
