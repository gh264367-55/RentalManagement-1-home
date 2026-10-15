// =====================================================================================================================
// Module: RentalManagement.Web / Controllers / UnitsController.cs
// Purpose: Units for both roles. Applicants browse units that are available today, filtered in the database by
//          property, bedrooms, rent and type. Property managers add, edit and remove units through modals whose
//          unit-type dropdown offers active types plus the unit's own (possibly inactive) type; the server re-checks
//          that choice. Also re-renders a property's unit panel after a change.
// =====================================================================================================================

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using RentalManagement.Core.Domain;
using RentalManagement.Core.Services.Properties;
using RentalManagement.Web.Infrastructure;
using RentalManagement.Web.ViewComponents;
using RentalManagement.Web.ViewModels;

namespace RentalManagement.Web.Controllers;

/// <summary>
/// Units: maintenance through modals for property managers, and the list of available units for applicants.
/// Authorization is per action because the two roles use different actions (stacked [Authorize] attributes would
/// require both roles).
/// </summary>
public sealed class UnitsController(PropertyService properties) : AppController
{
    // ---- Applicants -------------------------------------------------------------------------------------------

    /// <summary>Units with no lease covering today, filtered in the database by the query-string criteria.</summary>
    /// <param name="model">The filter criteria bound from the query string.</param>
    /// <param name="cancellationToken">Cancels the queries.</param>
    /// <returns>The Available units page.</returns>
    [HttpGet, Authorize(Roles = Roles.Applicant)]
    public async Task<IActionResult> Available(AvailableUnitsViewModel model, CancellationToken cancellationToken)
    {
        // Invalid criteria (e.g. a negative bedroom count typed into the URL) are shown and ignored.
        var filter = ModelState.IsValid ? model.ToFilter() : new AvailableUnitFilter();
        model.Units = await properties.GetAvailableUnitsAsync(filter, cancellationToken);
        model.PropertyOptions = (await properties.GetPropertyOptionsAsync(cancellationToken))
            .Select(p => new SelectListItem(p.Name, p.Id.ToString(), p.Id == model.PropertyId))
            .ToList();
        model.UnitTypeOptions = (await properties.GetAllUnitTypesAsync(cancellationToken))
            .Select(t => new SelectListItem(t.Name, t.Id.ToString(), t.Id == model.UnitTypeId))
            .ToList();
        return View(model);
    }

    // ---- Property managers ------------------------------------------------------------------------------------

    /// <summary>The PropertyUnits view component alone, for refreshing <c>#units-{propertyId}</c>.</summary>
    /// <param name="propertyId">The property whose unit panel to render.</param>
    /// <returns>The rendered view component.</returns>
    [HttpGet, Authorize(Roles = Roles.PropertyManager)]
    public IActionResult ForProperty(int propertyId) =>
        ViewComponent(typeof(PropertyUnitsViewComponent), new { propertyId });

    /// <summary>The empty "Add unit" modal for a property, with active unit types only.</summary>
    /// <param name="propertyId">The property to add the unit to.</param>
    /// <param name="cancellationToken">Cancels the queries.</param>
    /// <returns>The form partial, or 404.</returns>
    [HttpGet, Authorize(Roles = Roles.PropertyManager)]
    public async Task<IActionResult> Create(int propertyId, CancellationToken cancellationToken)
    {
        var property = await properties.GetPropertyAsync(propertyId, cancellationToken);
        if (property is null)
        {
            return NotFound();
        }

        var model = new UnitFormViewModel { PropertyId = propertyId, PropertyName = property.Input.Name };
        await LoadUnitTypeOptionsAsync(model, cancellationToken);
        return PartialView("_UnitForm", model);
    }

    /// <summary>Adds a unit from the modal.</summary>
    /// <param name="model">The posted form.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>JSON success, or the form with errors (422) and its dropdown rebuilt.</returns>
    [HttpPost, Authorize(Roles = Roles.PropertyManager)]
    public async Task<IActionResult> Create(UnitFormViewModel model, CancellationToken cancellationToken)
    {
        model.Id = null;
        if (ModelState.IsValid)
        {
            var result = await properties.CreateUnitAsync(model.PropertyId, model.ToInput(), cancellationToken);
            if (result.Succeeded)
            {
                return ModalSuccess($"Unit {model.UnitNumber.Trim()} added.");
            }

            AddError(result);
        }

        model.PropertyName = (await properties.GetPropertyAsync(model.PropertyId, cancellationToken))?.Input.Name ?? string.Empty;
        await LoadUnitTypeOptionsAsync(model, cancellationToken);
        return ModalInvalid("_UnitForm", model);
    }

    /// <summary>The "Edit unit" modal, pre-filled, with the unit's own type offered even if inactive.</summary>
    /// <param name="id">The unit id.</param>
    /// <param name="cancellationToken">Cancels the queries.</param>
    /// <returns>The form partial, or 404.</returns>
    [HttpGet, Authorize(Roles = Roles.PropertyManager)]
    public async Task<IActionResult> Edit(int id, CancellationToken cancellationToken)
    {
        var details = await properties.GetUnitAsync(id, cancellationToken);
        if (details is null)
        {
            return NotFound();
        }

        var model = UnitFormViewModel.From(details);
        await LoadUnitTypeOptionsAsync(model, cancellationToken);
        return PartialView("_UnitForm", model);
    }

    /// <summary>Saves a unit edit from the modal; the unit's property comes from the database, not the form.</summary>
    /// <param name="id">The unit id (from the route).</param>
    /// <param name="model">The posted form.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>JSON success, the form with errors (422), or 404.</returns>
    [HttpPost, Authorize(Roles = Roles.PropertyManager)]
    public async Task<IActionResult> Edit([FromRoute] int id, UnitFormViewModel model, CancellationToken cancellationToken)
    {
        // The unit's property is read from the database, never from the posted form.
        var existing = await properties.GetUnitAsync(id, cancellationToken);
        if (existing is null)
        {
            return NotFound();
        }

        model.Id = id;
        model.PropertyId = existing.PropertyId;
        model.PropertyName = existing.PropertyName;
        if (ModelState.IsValid)
        {
            var result = await properties.UpdateUnitAsync(id, model.ToInput(), model.ConcurrencyStamp, cancellationToken);
            if (result.Succeeded)
            {
                return ModalSuccess($"Unit {model.UnitNumber.Trim()} saved.");
            }

            AddError(result);
        }

        await LoadUnitTypeOptionsAsync(model, cancellationToken);
        return ModalInvalid("_UnitForm", model);
    }

    /// <summary>The "Remove unit" confirmation modal.</summary>
    /// <param name="id">The unit id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The confirmation partial, or 404.</returns>
    [HttpGet, Authorize(Roles = Roles.PropertyManager)]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var details = await properties.GetUnitAsync(id, cancellationToken);
        return details is null ? NotFound() : PartialView("_Confirm", ConfirmModel(details));
    }

    /// <summary>Removes the unit unless it has applications or leases.</summary>
    /// <param name="id">The unit id.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>JSON success, the confirmation with the reason (422), or 404.</returns>
    [HttpPost, ActionName(nameof(Delete)), Authorize(Roles = Roles.PropertyManager)]
    public async Task<IActionResult> DeleteConfirmed([FromRoute] int id, CancellationToken cancellationToken)
    {
        var details = await properties.GetUnitAsync(id, cancellationToken);
        if (details is null)
        {
            return NotFound();
        }

        var result = await properties.DeleteUnitAsync(id, cancellationToken);
        if (!result.Succeeded)
        {
            AddError(result);
            return ModalInvalid("_Confirm", ConfirmModel(details));
        }

        return ModalSuccess($"Unit {details.Input.UnitNumber} removed.");
    }

    /// <summary>
    /// Regions refreshed after a unit changes: only that property's unit panel and the portfolio numbers. The rest of
    /// the property list is unaffected (unit changes never alter whether a property can be removed).
    /// </summary>
    /// <param name="propertyId">The unit's property.</param>
    /// <returns>The space-separated region selectors for <c>data-refresh</c>.</returns>
    public static string RefreshRegions(int propertyId) => $"#units-{propertyId} #portfolio-summary";

    /// <summary>
    /// Active types plus the unit's <b>persisted</b> type (looked up by id, not taken from the post), so an inactive
    /// type is offered only on the unit that already has it.
    /// </summary>
    private async Task LoadUnitTypeOptionsAsync(UnitFormViewModel model, CancellationToken cancellationToken)
    {
        var types = await properties.GetSelectableUnitTypesAsync(model.Id, cancellationToken);
        model.UnitTypeOptions = types
            .Select(t => new SelectListItem(t.IsActive ? t.Name : $"{t.Name} (inactive)", t.Id.ToString(), t.Id == model.UnitTypeId))
            .ToList();
    }

    /// <summary>The remove-unit confirmation dialog; on success that property's unit panel and the summary refresh.</summary>
    private ConfirmViewModel ConfirmModel(UnitDetails unit) => new()
    {
        Title = "Remove unit",
        Message = $"Remove unit {unit.Input.UnitNumber} from {unit.PropertyName}? This cannot be undone.",
        ActionUrl = Url.Action(nameof(Delete), new { id = unit.Id })!,
        Refresh = RefreshRegions(unit.PropertyId),
    };
}
