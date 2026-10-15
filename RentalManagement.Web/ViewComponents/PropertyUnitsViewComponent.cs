// =====================================================================================================================
// Module: RentalManagement.Web / ViewComponents / PropertyUnitsViewComponent.cs
// Purpose: A property's unit panel on "Properties & units" (#units-{propertyId}): each unit with its type (inactive
//          types flagged), rent, today's lease status and application count, plus the unit modal buttons. Renders
//          inside the property list and alone through Units/ForProperty after a unit modal succeeds.
// =====================================================================================================================

using Microsoft.AspNetCore.Mvc;
using RentalManagement.Core.Services.Properties;

namespace RentalManagement.Web.ViewComponents;

/// <summary>
/// A property's unit panel: each unit with its type (inactive types flagged), rent, today's lease status and
/// application count, plus the add/edit/remove modal buttons. It runs its own query, so the same panel renders inside
/// the property list and alone through <c>Units/ForProperty</c> when a unit modal succeeds.
/// </summary>
public sealed class PropertyUnitsViewComponent(PropertyService properties) : ViewComponent
{
    /// <summary>Loads the property's units and renders the panel.</summary>
    /// <param name="propertyId">The property whose units to show.</param>
    /// <returns>The rendered panel.</returns>
    public async Task<IViewComponentResult> InvokeAsync(int propertyId) =>
        View(new PropertyUnitsModel(propertyId, await properties.GetUnitsAsync(propertyId, HttpContext.RequestAborted)));
}

/// <summary>Model of the PropertyUnits view component.</summary>
/// <param name="PropertyId">The property the units belong to.</param>
/// <param name="Units">The units, ordered by unit number.</param>
public sealed record PropertyUnitsModel(int PropertyId, IReadOnlyList<UnitRow> Units)
{
    /// <summary>How many of the units have a lease covering today (shown in the panel footer).</summary>
    public int LeasedCount => Units.Count(u => u.LeasedUntil is not null);
}
