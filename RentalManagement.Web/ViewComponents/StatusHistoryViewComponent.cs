// =====================================================================================================================
// Module: RentalManagement.Web / ViewComponents / StatusHistoryViewComponent.cs
// Purpose: The status history card (#status-history) shown to property managers on the application page: every status
//          change with who made it, when, the review outcome and the comment. Renders nothing for anyone else, so it
//          is safe wherever it is placed, and re-renders alone through Applications/History after a review.
// =====================================================================================================================

using Microsoft.AspNetCore.Mvc;
using RentalManagement.Core.Domain;
using RentalManagement.Core.Services.Applications;
using RentalManagement.Web.ViewModels;

namespace RentalManagement.Web.ViewComponents;

/// <summary>The application's status history, for property managers only.</summary>
public sealed class StatusHistoryViewComponent(ApplicationReviewService reviews) : ViewComponent
{
    /// <summary>Loads and renders the history when the viewer is a property manager.</summary>
    /// <param name="applicationId">The application id.</param>
    /// <returns>The history card, or empty content for any other user.</returns>
    public async Task<IViewComponentResult> InvokeAsync(int applicationId)
    {
        if (!UserClaimsPrincipal.IsInRole(Roles.PropertyManager))
        {
            return Content(string.Empty);
        }

        var entries = await reviews.GetStatusHistoryAsync(applicationId, HttpContext.RequestAborted);
        return View(new StatusHistoryModel(applicationId, entries));
    }
}
