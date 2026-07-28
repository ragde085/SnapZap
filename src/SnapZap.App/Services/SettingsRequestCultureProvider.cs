using Microsoft.AspNetCore.Localization;

namespace SnapZap.App.Services;

/// <summary>
/// Resolves request culture from <see cref="DependencyChecker.Language"/> instead of a cookie or
/// an Accept-Language header, so language is one more setting persisted in settings.json next to
/// <see cref="DependencyChecker.Theme"/> — a single source of truth instead of a browser cookie
/// that can silently disagree with what the UI shows.
/// </summary>
public sealed class SettingsRequestCultureProvider : RequestCultureProvider
{
    public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        var checker = httpContext.RequestServices.GetRequiredService<DependencyChecker>();
        return Task.FromResult<ProviderCultureResult?>(new ProviderCultureResult(checker.Language));
    }
}
