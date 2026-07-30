using SnapZap.Core.Data;

namespace SnapZap.Core.Scanning;

/// <summary>
/// What the scan reads, as opposed to what detection makes of it.
/// </summary>
/// <remarks>
/// <para>Persisted in the catalogue's <c>meta</c> table, next to <c>scan_root</c> and the
/// <c>dedup.*</c> keys, for the same reason those are: it describes how <i>this</i> catalogue was
/// built, so it has to reset when <c>catalog.db</c> does. <c>settings.json</c> stays the home for
/// app-level preferences that outlive any one catalogue (window, language, dependency prompts).</para>
///
/// <para>Separate from <see cref="Dedup.DedupSettings"/> on purpose. Those decide what detection
/// makes of the rows; these decide which rows exist at all, and changing them means a re-scan
/// rather than a re-detect.</para>
/// </remarks>
public sealed record ScanSettings
{
    /// <summary>
    /// Skip hidden folders and files — dot-prefixed names on macOS/Linux/Android, and anything
    /// carrying the Hidden attribute on Windows. <b>On by default.</b>
    /// </summary>
    /// <remarks>
    /// <para><b>Why the default is on.</b> Nothing a person would call a photo lives in a
    /// dot-folder. What lives there is caches and machinery: <c>.thumbnails</c> (which on Android is
    /// full of image files, and is exactly the kind of thing that made a first scan of internal
    /// storage report thousands of "photos"), <c>.trashed</c>, <c>.git</c>, Synology's
    /// <c>@eaDir</c>-alikes, and every app's private scratch. Indexing them does not just pad the
    /// count — the entries become duplicate-group members and land in the review queue, so the user
    /// triages a cache.</para>
    ///
    /// <para><b>The pickers already behaved this way and the scanner did not.</b> Both
    /// <c>DirectoryPickerDialog</c> and <c>DirectoryPickerActivity</c> filter dot-folders out of the
    /// browse list, so a folder you could not navigate into was still scanned when you picked its
    /// parent. This closes that.</para>
    ///
    /// <para><b>A hidden folder chosen as the scan root is still scanned.</b> Only hidden folders
    /// <i>below</i> the root are skipped — picking one explicitly is an instruction, not an
    /// accident, and refusing it would leave the user with a folder they selected and a scan that
    /// found nothing.</para>
    ///
    /// <para>Turning this on needs a re-scan, not a re-detect, and the re-scan cleans up after
    /// itself: <c>ImageRepository.DeleteMissing</c> prunes rows for files the walk no longer
    /// returns, so previously-indexed hidden files drop out of the catalogue rather than lingering
    /// as unreachable entries.</para>
    /// </remarks>
    public bool SkipHidden { get; init; } = true;

    const string Prefix = "scan.";

    public static ScanSettings Load(Database db)
    {
        var fallback = new ScanSettings();
        return new ScanSettings
        {
            SkipHidden = Bool(db, "skiphidden", fallback.SkipHidden),
        };
    }

    public void Save(Database db) =>
        db.SetMeta(Prefix + "skiphidden", SkipHidden ? "1" : "0");

    static bool Bool(Database db, string key, bool fallback) =>
        db.Meta(Prefix + key) switch { "1" => true, "0" => false, _ => fallback };
}
