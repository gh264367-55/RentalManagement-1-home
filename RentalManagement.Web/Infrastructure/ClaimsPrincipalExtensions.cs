// =====================================================================================================================
// Module: RentalManagement.Web / Infrastructure / ClaimsPrincipalExtensions.cs
// Purpose: Reads the signed-in user's Identity id from the claims, for MVC controllers, API controllers and view
//          components alike.
// =====================================================================================================================

using System.Security.Claims;

namespace RentalManagement.Web.Infrastructure;

/// <summary>Extension methods on <see cref="ClaimsPrincipal"/>.</summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>The signed-in user's id (the Identity user id claim).</summary>
    /// <param name="user">The current user.</param>
    /// <returns>The user id.</returns>
    /// <exception cref="InvalidOperationException">No user is signed in (an endpoint missing its [Authorize]).</exception>
    public static string GetRequiredUserId(this ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("No signed-in user.");
}
