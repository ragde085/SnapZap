using System.Globalization;
using System.Resources;
using Microsoft.Extensions.Localization;

namespace SnapZap.Tests;

/// <summary>
/// A minimal, dependency-free <see cref="IStringLocalizer{T}"/> for constructing services like
/// <c>AppState</c> outside DI. Reads the real, compiled <c>.resx</c> for <typeparamref name="T"/>
/// via a plain <see cref="ResourceManager"/> — the same convention
/// <c>ResourceManagerStringLocalizer</c> uses internally — so assertions against the English
/// resource text (e.g. <c>NsfwBandTests</c> checking <c>AppState.BandLabel</c>) keep comparing
/// against the actual shipped strings instead of a stand-in.
/// </summary>
sealed class TestStringLocalizer<T> : IStringLocalizer<T>
{
    static readonly ResourceManager Manager = new(typeof(T).FullName!, typeof(T).Assembly);

    public LocalizedString this[string name]
    {
        get
        {
            var value = Manager.GetString(name, CultureInfo.CurrentUICulture);
            return new LocalizedString(name, value ?? name, resourceNotFound: value is null);
        }
    }

    public LocalizedString this[string name, params object[] arguments]
    {
        get
        {
            var format = Manager.GetString(name, CultureInfo.CurrentUICulture);
            var value = format is null ? name : string.Format(format, arguments);
            return new LocalizedString(name, value, resourceNotFound: format is null);
        }
    }

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
}
