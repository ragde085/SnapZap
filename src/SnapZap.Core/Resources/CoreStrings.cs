using System.Globalization;
using System.Resources;

namespace SnapZap.Core.Resources;

/// <summary>Portable string lookup — SnapZap.Core has no ASP.NET Core dependency, so this
/// reads System.Globalization.CultureInfo.CurrentUICulture directly via ResourceManager
/// instead of Microsoft.Extensions.Localization. The App project's request-culture
/// middleware sets CurrentUICulture per request/circuit before any of this runs.</summary>
public static class CoreStrings
{
    static readonly ResourceManager Manager =
        new("SnapZap.Core.Resources.CoreStrings", typeof(CoreStrings).Assembly);

    public static string Get(string key) => Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    public static string Get(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentUICulture, Get(key), args);
}
