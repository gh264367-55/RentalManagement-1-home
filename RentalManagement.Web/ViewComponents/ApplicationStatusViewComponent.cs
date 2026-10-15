// =====================================================================================================================
// Module: RentalManagement.Web / ViewComponents / ApplicationStatusViewComponent.cs
// Purpose: The status card shown beside an application (#status-panel): status badge, unit and rent, key dates, the
//          lease once approved, the latest reviewer comment, the review claim, the Withdraw action for the applicant and
//          the Claim / Review / Release actions for a property manager, each only when allowed. Runs its own
//          access-checked query so it can be rendered inside the page or alone through Applications/StatusPanel.
// =====================================================================================================================

using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using RentalManagement.Core.Domain;
using RentalManagement.Core.Services.Applications;
using RentalManagement.Web.ViewModels;

namespace RentalManagement.Web.ViewComponents;

/// <summary>
/// The status card beside the application: status, unit and rent, key dates, the lease once approved, the latest
/// reviewer comment (so an applicant knows what to correct after a Return), the Withdraw action when the
/// signed-in applicant may still withdraw, and the Review action when a property manager views a Submitted
/// application. It loads the application itself with the same access rule as the page.
/// </summary>
public sealed class ApplicationStatusViewComponent(ApplicationService applications) : ViewComponent
{
    /// <summary>Renders the status card for an application the signed-in user may see.</summary>
    /// <param name="applicationId">The application id.</param>
    /// <returns>The card, or empty content when the application isn't visible to the user.</returns>
    public async Task<IViewComponentResult> InvokeAsync(int applicationId)
    {
        var userId = UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var isManager = UserClaimsPrincipal.IsInRole(Roles.PropertyManager);
        var details = await applications.GetAsync(applicationId, userId, isManager, HttpContext.RequestAborted);
        if (details is null)
        {
            return Content(string.Empty);
        }

        return View(new ApplicationStatusModel(
            details,
            details.IsWithdrawableBy(userId),
            isManager,
            CanReview: isManager && details.IsClaimedBy(userId),
            CanClaim: isManager && details.Status == ApplicationStatus.Submitted));
    }
}
