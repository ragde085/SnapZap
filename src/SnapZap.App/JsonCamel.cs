using System.Text.Json;

namespace SnapZap.App;

/// <summary>
/// camelCase JSON options for manually-serialized SSE event bodies, so they match the
/// camelCase that ASP.NET's Results.Ok already uses. One casing across the whole API.
/// </summary>
public static class JsonCamel
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
