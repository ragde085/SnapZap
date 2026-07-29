using SnapZap.Core.Platform;
using SnapZap.Core.Resources;

namespace SnapZap.Core.Delete;

/// <summary>
/// Portable, folder-backed <see cref="ITrashService"/>: everything a real trash needs to do —
/// move a file somewhere reversible, move it back — with the "somewhere" supplied as a plain
/// directory path rather than an OS trash API. This is the implementation an Android host will
/// use (its "trash" is an app-private directory under <c>getExternalFilesDir</c>, with no
/// <c>MediaStore</c>/Recycle-Bin/Finder equivalent to call into), but it has **no Android SDK
/// reference, no <c>Context</c>, no P/Invoke** — it is exactly as portable as the rest of
/// <c>SnapZap.Core</c> and compiles and runs on any OS this library targets. The Android-specific
/// part of the port shrinks to "construct this with <c>context.GetExternalFilesDir(null)/trash</c>",
/// which is the whole point of splitting it out this way (see docs/ANDROID-PORT-ACS.md §E3): the
/// risky logic — collision-safe naming, a restore target that's occupied, moves across a volume
/// boundary — becomes unit-testable today, instead of only provable on a physical device later.
/// </summary>
public sealed class FolderTrashService : ITrashService
{
    readonly string _trashRoot;

    // The first move attempt normally goes straight to File.Move (a fast OS rename). Tests
    // substitute this to force the cross-volume fallback path deterministically — see the
    // internal constructor below — without needing a second real volume mounted in CI.
    readonly Action<string, string> _primaryMove;

    public FolderTrashService(string trashRoot) : this(trashRoot, File.Move) { }

    internal FolderTrashService(string trashRoot, Action<string, string> primaryMove)
    {
        _trashRoot = trashRoot;
        _primaryMove = primaryMove;
    }

    /// <summary>How much the trash currently holds. Cheap enough to call on every screen open.</summary>
    /// <remarks>
    /// Needed because a folder-backed trash is <b>invisible</b>: it sits in app-private storage
    /// where no file manager will show it, so unlike the Recycle Bin or Finder's Trash the user has
    /// no other way to discover that deleting a thousand photos is still holding their bytes. A
    /// trash the user cannot see the size of is a storage leak with good intentions.
    /// </remarks>
    public (int Files, long Bytes) Measure()
    {
        if (!Directory.Exists(_trashRoot)) return (0, 0);

        int files = 0; long bytes = 0;
        foreach (var f in Directory.EnumerateFiles(_trashRoot, "*", SearchOption.AllDirectories))
        {
            try { bytes += new FileInfo(f).Length; files++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that vanished mid-walk is not an error worth failing a size read-out over.
            }
        }
        return (files, bytes);
    }

    /// <summary>
    /// Permanently deletes everything in the trash. Returns how many files were removed.
    /// </summary>
    /// <remarks>
    /// <para>⚠ <b>This is the one place in SnapZap that hard-deletes, and it is deliberate.</b>
    /// DESIGN's invariant is that no destructive step happens without the user asking for it and
    /// without a reversible path first — recycling gives that reversible path, and emptying the
    /// trash is the user spending it. Every other delete route in the app moves files here; this
    /// is the only exit. Callers must confirm explicitly before calling it.</para>
    ///
    /// <para><b>What it costs.</b> <c>undo_log</c> rows that point at these files survive, and
    /// restoring one afterwards reports it as missing rather than failing — <c>RestoreAsync</c>
    /// already counts missing separately for exactly this reason. That is the honest behaviour,
    /// but it does leave history entries that can no longer be acted on, which is the other half
    /// of ROADMAP item 14.</para>
    ///
    /// <para>Best-effort per file: one unreadable entry does not abandon the rest, since a trash
    /// that cannot be emptied because of a single stuck file is a trash that grows forever.</para>
    /// </remarks>
    public int Empty()
    {
        if (!Directory.Exists(_trashRoot)) return 0;

        int removed = 0;
        foreach (var f in Directory.EnumerateFiles(_trashRoot, "*", SearchOption.AllDirectories).ToList())
        {
            try { File.Delete(f); removed++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }

    public Task<string?> SendToTrashAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Trashing a path that doesn't exist is a caller bug, not a normal outcome to swallow:
        // both existing callers (DeleteService.RecycleAsync, ExportEngine's reject path) already
        // guard with File.Exists before calling in. Throwing the same exception File.Move itself
        // would throw keeps the contract predictable and loud, rather than quietly "succeeding"
        // at a no-op, which would look exactly like a completed delete to anything not checking.
        if (!File.Exists(path))
            throw new FileNotFoundException(CoreStrings.Get("SourceMissing"), path);

        Directory.CreateDirectory(_trashRoot);

        // Collision-safe: two files both named IMG_0001.jpg, dragged in from different source
        // folders, would otherwise clobber each other the moment they land in one flat trash
        // directory — there's no per-source subfolder here to keep them apart. The GUID prefix is
        // what actually guarantees uniqueness; the original filename is kept as a suffix purely so
        // a human skimming the trash folder (or a restore-by-basename fallback, the way
        // MacOsTrashService's RestoreAsync does when it has no exact location) can still tell what
        // a trashed file was.
        var dest = Path.Combine(_trashRoot, $"{Guid.NewGuid():N}_{Path.GetFileName(path)}");

        MoveWithCrossVolumeFallback(path, dest);
        return Task.FromResult<string?>(dest);
    }

    public Task<bool> RestoreAsync(string originalPath, string? trashedLocation, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Nothing to restore from. Matches ITrashService's documented contract for a location the
        // caller can't supply (or that has since gone missing) — report false, don't guess at
        // where the file might be (that guessing is MacOsTrashService's job, for the one real
        // trash it knows how to search).
        if (string.IsNullOrEmpty(trashedLocation) || !File.Exists(trashedLocation))
            return Task.FromResult(false);

        if (File.Exists(originalPath))
            // Something already occupies the original spot. The Android plan's first draft used
            // File.Move(src, dest, overwrite: false) here, which *throws* on exactly this case
            // (documented .NET behavior) instead of returning the `false` ITrashService's doc
            // comment promises ("Returns true if the file is back at its original path" implies
            // false for anything else, not an exception). Overwriting silently would also be a
            // real data-loss bug wearing a "restore" costume, so this is a hard no, not a
            // best-effort merge or a "newer file wins" guess.
            return Task.FromResult(false);

        var dir = Path.GetDirectoryName(originalPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        try
        {
            MoveWithCrossVolumeFallback(trashedLocation, originalPath);
        }
        catch (IOException)
        {
            // Caught and reported as `false`, per the same "don't throw, don't overwrite"
            // contract above — a restore that fails is a no-op the caller can retry or surface,
            // not a crash the caller has to remember to catch.
            return Task.FromResult(false);
        }

        return Task.FromResult(File.Exists(originalPath));
    }

    /// <summary>
    /// Move a file, tolerating a volume boundary between <paramref name="src"/> and
    /// <paramref name="dest"/>. <see cref="File.Move(string, string)"/> is a fast rename that only
    /// works within one filesystem; it fails when the two paths are on different volumes (Unix
    /// errno EXDEV when a mount point is crossed; distinct drive roots on Windows raise an
    /// <see cref="IOException"/> of their own). That boundary is not hypothetical here — a trash
    /// root under Android app-private storage and a source folder on removable/SD storage are
    /// exactly the kind of two-volume setup this app has to work across. Rather than let that
    /// surface as an unhandled, cryptic framework exception, fall back to copy+delete, which works
    /// across volumes at the cost of extra I/O. If the fallback also fails, clean up any partial
    /// copy and re-throw one clear, contextual <see cref="IOException"/> — a "caught, reported"
    /// error the caller can catch and log (DeleteService.RecycleAsync already does exactly that,
    /// incrementing its Failed count) rather than a bare, unexplained one.
    /// </summary>
    void MoveWithCrossVolumeFallback(string src, string dest)
    {
        try
        {
            _primaryMove(src, dest);
        }
        catch (IOException)
        {
            try
            {
                File.Copy(src, dest, overwrite: false);
                File.Delete(src);
            }
            catch (Exception fallbackEx)
            {
                try { if (File.Exists(dest)) File.Delete(dest); } catch { /* best-effort cleanup */ }
                throw new IOException(CoreStrings.Get("CrossVolumeMoveFailed", src, dest, fallbackEx.Message), fallbackEx);
            }
        }
    }
}
