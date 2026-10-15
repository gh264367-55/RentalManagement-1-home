// =====================================================================================================================
// Module: RentalManagement.Web / Infrastructure / OpenApiConfiguration.cs
// Purpose: The OpenAPI document of the JSON API (Microsoft.AspNetCore.OpenApi): title and description, the sign-in
//          cookie as the security scheme, and on each operation that requires authorization the security requirement
//          and the roles allowed. Only attribute-routed API controllers ([ApiController]) appear in the document; the
//          MVC pages are conventionally routed and excluded.
// =====================================================================================================================

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.OpenApi;

namespace RentalManagement.Web.Infrastructure;

/// <summary>Registers and describes the OpenAPI document (<c>/openapi/v1.json</c>).</summary>
public static class OpenApiConfiguration
{
    /// <summary>The OpenAPI document name; the document is served at <c>/openapi/{name}.json</c>.</summary>
    public const string DocumentName = "v1";

    /// <summary>The security scheme's id in the document.</summary>
    public const string CookieSchemeId = "IdentityCookie";

    /// <summary>The ASP.NET Core Identity sign-in cookie that authenticates API calls.</summary>
    public static readonly string CookieName = $".AspNetCore.{IdentityConstants.ApplicationScheme}";

    /// <summary>Adds the OpenAPI document generator with the app's title, security scheme and per-operation requirements.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddRentalManagementOpenApi(this IServiceCollection services) =>
        services.AddOpenApi(DocumentName, options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "RentalManagement API",
                    Version = DocumentName,
                    Description =
                        "JSON endpoints of the rental application system. Calls are authenticated with the site's sign-in " +
                        "cookie: sign in through the web app (/Account/Login) in the same browser, then call the API. " +
                        "Applicants only ever receive their own applications; property managers receive all of them.",
                };
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes[CookieSchemeId] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    In = ParameterLocation.Cookie,
                    Name = CookieName,
                    Description = "The ASP.NET Core Identity cookie set by signing in at /Account/Login.",
                };
                return Task.CompletedTask;
            });

            options.AddOperationTransformer((operation, context, _) =>
            {
                var authorize = context.Description.ActionDescriptor.EndpointMetadata.OfType<IAuthorizeData>().ToList();
                if (authorize.Count == 0 || context.Description.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any())
                {
                    return Task.CompletedTask;
                }

                // The cookie handler answers API calls with a bare 401/403 (see Program.cs), so those have no body.
                foreach (var code in new[] { "401", "403" })
                {
                    if (operation.Responses?.TryGetValue(code, out var response) == true && response is OpenApiResponse concrete)
                    {
                        concrete.Content?.Clear();
                    }
                }

                operation.Security ??= [];
                operation.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(CookieSchemeId, context.Document)] = [],
                });

                var roles = authorize
                    .Where(a => !string.IsNullOrWhiteSpace(a.Roles))
                    .Select(a => a.Roles!.Replace(",", " or "))
                    .ToList();
                var requirement = roles.Count == 0
                    ? "Requires a signed-in user."
                    : $"Requires a signed-in user in role {string.Join(" and ", roles)}.";
                operation.Description = string.IsNullOrWhiteSpace(operation.Description)
                    ? requirement
                    : $"{operation.Description}\n\n**Authorization:** {requirement}";
                return Task.CompletedTask;
            });
        });
}
