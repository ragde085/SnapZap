using System.Text;
using System.Text.Json;
using SnapZap.Core.Data;

namespace SnapZap.Core.Export;

/// <summary>
/// Writes an audit trail of an export run (CSV + JSON): source → destination, status, reason.
/// Written to an app-data manifests folder — NEVER the destination root, which Plex indexes
/// (DESIGN §6). Returns the paths written.
/// </summary>
public static class ManifestWriter
{
    public static (string csvPath, string jsonPath) Write(
        Database db, ExportResult result, string manifestDir)
    {
        Directory.CreateDirectory(manifestDir);
        var byId = new ImageRepository(db).All().ToDictionary(i => i.Id);
        var stamp = $"export-{result.RunId}";
        var csvPath = Path.Combine(manifestDir, stamp + ".csv");
        var jsonPath = Path.Combine(manifestDir, stamp + ".json");

        // CSV
        var sb = new StringBuilder();
        sb.AppendLine("image_id,status,source,destination,detail");
        foreach (var it in result.Items)
        {
            var src = byId.TryGetValue(it.ImageId, out var img) ? img.Path : "";
            sb.Append(it.ImageId).Append(',')
              .Append(Csv(it.Status)).Append(',')
              .Append(Csv(src)).Append(',')
              .Append(Csv(it.DestPath ?? "")).Append(',')
              .Append(Csv(it.Detail ?? "")).Append('\n');
        }
        File.WriteAllText(csvPath, sb.ToString());

        // JSON
        var payload = new
        {
            runId = result.RunId,
            destination = result.Destination,
            summary = new { result.Exported, result.SkippedExisting, result.Failed, result.Recycled },
            items = result.Items.Select(it => new
            {
                it.ImageId,
                source = byId.TryGetValue(it.ImageId, out var img) ? img.Path : null,
                it.Status,
                it.DestPath,
                it.Detail,
            }),
        };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(payload,
            new JsonSerializerOptions { WriteIndented = true }));

        return (csvPath, jsonPath);
    }

    static string Csv(string s)
    {
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
            return '"' + s.Replace("\"", "\"\"") + '"';
        return s;
    }
}
