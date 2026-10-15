// =====================================================================================================================
// Module: RentalManagement.Web / Infrastructure / HttpRequestExtensions.cs
// Purpose: Request helpers. Identifies fetch requests made by site.js and calls to the JSON API, so authentication
//          failures can be answered with status codes instead of HTML redirects.
// =====================================================================================================================

namespace RentalManagement.Web.Infrastructure;

/// <summary>Extension methods on <see cref="HttpRequest"/>.</summary>
public static class HttpRequestExtensions
{
    /// <summary>True for requests made by site.js (modals and region refreshes), which send <c>X-Requested-With</c>.</summary>
    /// <param name="request">The current request.</param>
    /// <returns>True when the request carries <c>X-Requested-With: XMLHttpRequest</c>.</returns>
    public static bool IsAjaxRequest(this HttpRequest request) =>
        string.Equals(request.Headers.XRequestedWith, "XMLHttpRequest", StringComparison.Ordinal);

    /// <summary>True for calls to the JSON API (paths under <c>/api</c>), which never get HTML redirects or pages.</summary>
    /// <param name="request">The current request.</param>
    /// <returns>True when the path starts with the <c>/api</c> segment.</returns>
    public static bool IsApiRequest(this HttpRequest request) => request.Path.StartsWithSegments("/api");
}
