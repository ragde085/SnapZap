using System.Diagnostics;
using System.Text.Json;
using SnapZap.Core.Data;

namespace SnapZap.Core.Dedup;

public sealed record CzkawkaResult(bool Available, int GroupsFound, string? Message);

/// <summary>
/// Similar-image (perceptual) detection via the external <c>czkawka_cli</c> (MIT). This is
/// the only thing Czkawka is used for — exact duplicates come from our own content hashes
/// (<see cref="ExactDuplicateFinder"/>). If the binary is absent, this degrades gracefully:
/// the app is fully functional, it just won't surface near-duplicates.
///
/// ✅ VALIDATED against czkawka 12.0.0 (2026-07-25). Real output is an array of groups, each
/// an array of objects carrying <c>path</c> plus size/width/height/difference metadata that we
/// ignore. <c>DedupTests.Parses_real_czkawka_12_output</c> locks that captured shape in. The
/// parser stays deliberately tolerant — it finds any array of objects with a string "path" —
/// so a schema change is more likely to degrade than to break outright.
/// </summary>
public sealed class CzkawkaFinder(Database db, string? explicitBinaryPath = null)
{
    /// <summary>
    /// 0–40; higher = looser matching. Kept conservative on purpose: this feeds a tool that
    /// deletes things, so a false positive costs more than a miss.
    ///
    /// Note czkawka 12 defaults <c>--hash-size</c> to 16, for which it recommends values up to
    /// 20; 10 is therefore stricter than czkawka's own guidance and will under-detect. Raise
    /// it if similar-detection feels too quiet.
    /// </summary>
    public int MaxDifference { get; init; } = 10;

    public string? LocateBinary()
    {
        // An explicit path is authoritative: if it was configured and isn't there, report
        // unavailable rather than quietly running some other czkawka found on PATH. Silently
        // substituting a different binary than the one asked for is worse than doing nothing.
        if (!string.IsNullOrWhiteSpace(explicitBinaryPath))
            return File.Exists(explicitBinaryPath) ? explicitBinaryPath : null;

        // Otherwise: beside our binary (sidecar), then PATH.
        var exeName = OperatingSystem.IsWindows() ? "czkawka_cli.exe" : "czkawka_cli";
        var beside = Path.Combine(AppContext.BaseDirectory, exeName);
        if (File.Exists(beside)) return beside;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var candidate = Path.Combine(dir.Trim(), exeName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public async Task<CzkawkaResult> FindSimilarAsync(string root, CancellationToken ct = default)
    {
        var bin = LocateBinary();
        if (bin is null)
            return new CzkawkaResult(false, 0, "czkawka_cli not found — similar-image detection skipped.");

        var jsonPath = Path.Combine(Path.GetTempPath(), $"czkawka_{Guid.NewGuid():N}.json");
        try
        {
            var psi = new ProcessStartInfo(bin)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("image");
            psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(root);
            psi.ArgumentList.Add("--max-difference"); psi.ArgumentList.Add(MaxDifference.ToString());
            psi.ArgumentList.Add("-C"); psi.ArgumentList.Add(jsonPath); // compact JSON

            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync(ct);
            if (!File.Exists(jsonPath))
            {
                var err = await p.StandardError.ReadToEndAsync(ct);
                return new CzkawkaResult(true, 0, $"czkawka produced no output (exit {p.ExitCode}). {Trim(err)}");
            }

            var groups = ParseGroups(await File.ReadAllTextAsync(jsonPath, ct));
            var stored = StoreGroups(groups, root);
            return new CzkawkaResult(true, stored, stored == 0 ? "No similar images found." : null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new CzkawkaResult(true, 0, $"czkawka invocation failed: {ex.Message}");
        }
        finally
        {
            if (File.Exists(jsonPath)) try { File.Delete(jsonPath); } catch { }
        }
    }

    /// <summary>Recursively extract groups: any JSON array whose elements are objects with a
    /// string "path" is treated as one similarity group. Schema-tolerant by design.</summary>
    internal static List<List<string>> ParseGroups(string json)
    {
        var groups = new List<List<string>>();
        using var doc = JsonDocument.Parse(json);
        Walk(doc.RootElement, groups);
        return groups;

        static void Walk(JsonElement el, List<List<string>> acc)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Array:
                    // Is this array a group of file entries?
                    var paths = new List<string>();
                    bool looksLikeGroup = el.GetArrayLength() > 0;
                    foreach (var item in el.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object &&
                            item.TryGetProperty("path", out var pv) &&
                            pv.ValueKind == JsonValueKind.String)
                            paths.Add(pv.GetString()!);
                        else
                            looksLikeGroup = false;
                    }
                    if (looksLikeGroup && paths.Count > 1)
                        acc.Add(paths);
                    else
                        foreach (var item in el.EnumerateArray()) Walk(item, acc);
                    break;

                case JsonValueKind.Object:
                    foreach (var prop in el.EnumerateObject()) Walk(prop.Value, acc);
                    break;
            }
        }
    }

    /// <summary>
    /// Resolve symlinked ancestors so a catalog path and a czkawka path for the same file
    /// compare equal. Czkawka canonicalises what it reports, while the catalog stores the
    /// path the user typed — on macOS a scan of <c>/tmp/x</c> comes back as
    /// <c>/private/tmp/x</c> and every group silently fails to match.
    /// </summary>
    internal static string Canonical(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(full);
            return dir is null ? full : Path.Combine(ResolveDir(dir), Path.GetFileName(full));
        }
        catch { return path; }

        static string ResolveDir(string dir)
        {
            try
            {
                if (new DirectoryInfo(dir).ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    return target.FullName;
            }
            catch { /* unreadable or gone — fall through to the parent */ }

            var parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent) || parent == dir) return dir;

            var resolvedParent = ResolveDir(parent);
            return resolvedParent == parent ? dir : Path.Combine(resolvedParent, Path.GetFileName(dir));
        }
    }

    int StoreGroups(List<List<string>> groups, string root)
    {
        var repo = new ImageRepository(db);
        var dupes = new DupeRepository(db);
        var stored = repo.PathIndex();

        // Look up by the stored path first, then by its canonical form. Both are indexed so a
        // group matches whichever spelling czkawka reports.
        var index = new Dictionary<string, (long id, long pixels, long bytes)>(stored, StringComparer.Ordinal);
        foreach (var (path, hit) in stored)
        {
            var canonical = Canonical(path);
            if (!index.ContainsKey(canonical)) index[canonical] = hit;
        }

        // Scoped to the folder just searched, so re-running here cannot discard the similar
        // groups found in a different library.
        dupes.ClearKind(DupeKind.Similar, root);

        int count = 0;
        using var tx = db.Connection.BeginTransaction();
        foreach (var group in groups)
        {
            // Map each path to a catalog row; drop unknown paths.
            var members = new List<(long id, long pixels, long bytes)>();
            foreach (var path in group)
                if (index.TryGetValue(path, out var hit) ||
                    index.TryGetValue(Canonical(path), out hit))
                    members.Add(hit);
            if (members.Count < 2) continue;

            // Highest resolution wins; on a tie prefer the larger file, which for the same
            // dimensions means the less-compressed copy. Falling back to row id — i.e. scan
            // order — could keep a heavily compressed version over its original.
            var keeperId = members
                .OrderByDescending(m => m.pixels)
                .ThenByDescending(m => m.bytes)
                .ThenBy(m => m.id)
                .First().id;
            dupes.AddGroup(DupeKind.Similar, similarity: $"maxdiff<={MaxDifference}",
                members.Select(m => (m.id, m.id == keeperId)).ToList());
            count++;
        }
        tx.Commit();
        return count;
    }

    static string Trim(string s) => s.Length > 200 ? s[..200] + "…" : s;
}
