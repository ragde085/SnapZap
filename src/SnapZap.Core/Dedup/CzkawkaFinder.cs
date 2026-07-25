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
/// ⚠ VALIDATION PENDING: Czkawka's JSON schema for similar images is not publicly documented
/// and could not be captured on the dev machine (binary not installed). The parser below is
/// deliberately defensive — it recursively finds arrays of file-entry objects (any object
/// carrying a string "path") and treats each as a group. Verify against real output on a
/// machine with czkawka_cli before relying on similar-detection results.
/// </summary>
public sealed class CzkawkaFinder(Database db, string? explicitBinaryPath = null)
{
    /// <summary>0–40; higher = looser matching. Czkawka's default region is ~10.</summary>
    public int MaxDifference { get; init; } = 10;

    public string? LocateBinary()
    {
        // 1) explicit config, 2) beside our binary (sidecar), 3) PATH.
        if (!string.IsNullOrWhiteSpace(explicitBinaryPath) && File.Exists(explicitBinaryPath))
            return explicitBinaryPath;

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
            var stored = StoreGroups(groups);
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

    int StoreGroups(List<List<string>> groups)
    {
        var repo = new ImageRepository(db);
        var dupes = new DupeRepository(db);
        var index = repo.PathIndex();
        dupes.ClearKind(DupeKind.Similar);

        int count = 0;
        using var tx = db.Connection.BeginTransaction();
        foreach (var group in groups)
        {
            // Map each path to a catalog row; drop unknown paths.
            var members = new List<(long id, long pixels)>();
            foreach (var path in group)
                if (index.TryGetValue(path, out var hit))
                    members.Add(hit);
            if (members.Count < 2) continue;

            var keeperId = members.OrderByDescending(m => m.pixels).ThenBy(m => m.id).First().id;
            dupes.AddGroup(DupeKind.Similar, similarity: $"maxdiff<={MaxDifference}",
                members.Select(m => (m.id, m.id == keeperId)).ToList());
            count++;
        }
        tx.Commit();
        return count;
    }

    static string Trim(string s) => s.Length > 200 ? s[..200] + "…" : s;
}
