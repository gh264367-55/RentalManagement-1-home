// =====================================================================================================================
// Module: RentalManagement.Web / ViewComponents / ResidenceListViewComponent.cs
// Purpose: The list of an application's prior residences (#residence-list). Rendered editable (with modal buttons)
//          in the Residence History section when the server allows it, read-only on the Summary and for
//          non-editable applications, and alone through Residences/List after a residence modal succeeds.
// =====================================================================================================================

using Microsoft.AspNetCore.Mvc;
using RentalManagement.Core.Services.Applications;
using RentalManagement.Web.ViewModels;

namespace RentalManagement.Web.ViewComponents;

/// <summary>
/// The residences of an application, with edit/remove modal buttons when <c>editable</c>. Rendered by the Residence
/// History section (editable as decided by the server) and by the Summary (always read-only), and alone through
/// <c>Residences/List</c> after a residence modal succeeds. Callers have already checked access to the application.
/// </summary>
public sealed class ResidenceListViewComponent(ApplicationService applications) : ViewComponent
{
    /// <summary>Loads the application's residences and renders the list.</summary>
    /// <param name="applicationId">The application whose residences to list.</param>
    /// <param name="editable">Whether to show edit/remove buttons; decided by the server, never by the browser.</param>
    /// <returns>The rendered list.</returns>
    public async Task<IViewComponentResult> InvokeAsync(int applicationId, bool editable) =>
        View(new ResidenceListModel(
            applicationId,
            await applications.GetResidencesAsync(applicationId, HttpContext.RequestAborted),
            editable));
}
