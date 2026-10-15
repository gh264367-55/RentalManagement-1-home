// =====================================================================================================================
// Module: RentalManagement.Web / ViewComponents / PortfolioSummaryViewComponent.cs
// Purpose: The row of headline numbers at the top of "Properties & units" (#portfolio-summary). Renders inside the
//          page and alone through Properties/Summary after any property or unit change.
// =====================================================================================================================

using Microsoft.AspNetCore.Mvc;
using RentalManagement.Core.Services.Properties;

namespace RentalManagement.Web.ViewComponents;

/// <summary>
/// Headline numbers for property managers: properties, units, units leased today, units available, and applications
/// awaiting review. Re-rendered alone through <c>Properties/Summary</c> after any property or unit change.
/// </summary>
public sealed class PortfolioSummaryViewComponent(PropertyService properties) : ViewComponent
{
    /// <summary>Loads the current numbers and renders the tiles.</summary>
    /// <returns>The rendered summary.</returns>
    public async Task<IViewComponentResult> InvokeAsync() =>
        View(await properties.GetPortfolioStatsAsync(HttpContext.RequestAborted));
}
