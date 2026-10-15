// =====================================================================================================================
// Module: RentalManagement.Web / Controllers / PropertiesController.cs
// Purpose: Property maintenance for property managers: the "Properties & units" page, and add/edit/remove property
//          modals that follow the modal contract (partial on GET, same partial with 422 when invalid, JSON on
//          success), plus the endpoints that re-render the property list and portfolio summary after a change.
// =====================================================================================================================

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalManagement.Core.Domain;
using RentalManagement.Core.Services.Properties;
using RentalManagement.Web.Infrastructure;
using RentalManagement.Web.ViewComponents;
using RentalManagement.Web.ViewModels;

namespace RentalManagement.Web.Controllers;

/// <summary>Property maintenance for property managers. Add, edit and remove go through modals.</summary>
[Authorize(Roles = Roles.PropertyManager)]
public sealed class PropertiesController(PropertyService properties) : AppController
{
    /// <summary>Region refreshed after a property is added, edited or removed.</summary>
    public const string ListRegion = "#property-list #portfolio-summary";

    /// <summary>The "Properties &amp; units" page: portfolio summary plus every property with its unit panel.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The page view.</returns>
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken) =>
        View(await properties.GetPropertiesAsync(cancellationToken));

    /// <summary>The property list alone, for refreshing <c>#property-list</c>.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The list partial.</returns>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        PartialView("_PropertyList", await properties.GetPropertiesAsync(cancellationToken));

    /// <summary>The portfolio summary view component alone, for refreshing <c>#portfolio-summary</c>.</summary>
    /// <returns>The rendered view component.</returns>
    [HttpGet]
    public IActionResult Summary() => ViewComponent(typeof(PortfolioSummaryViewComponent));

    /// <summary>The empty "Add property" modal.</summary>
    /// <returns>The form partial.</returns>
    [HttpGet]
    public IActionResult Create() => PartialView("_PropertyForm", new PropertyFormViewModel());

    /// <summary>Adds a property from the modal.</summary>
    /// <param name="model">The posted form.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>JSON success, or the form with errors (422).</returns>
    [HttpPost]
    public async Task<IActionResult> Create(PropertyFormViewModel model, CancellationToken cancellationToken)
    {
        model.Id = null;
        if (!ModelState.IsValid)
        {
            return ModalInvalid("_PropertyForm", model);
        }

        var result = await properties.CreatePropertyAsync(model.ToInput(), cancellationToken);
        if (!result.Succeeded)
        {
            AddError(result);
            return ModalInvalid("_PropertyForm", model);
        }

        return ModalSuccess($"Property \"{model.Name.Trim()}\" added.");
    }

    /// <summary>The "Edit property" modal, pre-filled and carrying the concurrency stamp.</summary>
    /// <param name="id">The property id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The form partial, or 404.</returns>
    [HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken cancellationToken)
    {
        var details = await properties.GetPropertyAsync(id, cancellationToken);
        return details is null ? NotFound() : PartialView("_PropertyForm", PropertyFormViewModel.From(details));
    }

    /// <summary>Saves a property edit from the modal; a stale form is rejected with a "reload" message.</summary>
    /// <param name="id">The property id (from the route, not the form).</param>
    /// <param name="model">The posted form.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>JSON success, or the form with errors (422).</returns>
    [HttpPost]
    public async Task<IActionResult> Edit([FromRoute] int id, PropertyFormViewModel model, CancellationToken cancellationToken)
    {
        model.Id = id;
        if (!ModelState.IsValid)
        {
            return ModalInvalid("_PropertyForm", model);
        }

        var result = await properties.UpdatePropertyAsync(id, model.ToInput(), model.ConcurrencyStamp, cancellationToken);
        if (!result.Succeeded)
        {
            AddError(result);
            return ModalInvalid("_PropertyForm", model);
        }

        return ModalSuccess($"Property \"{model.Name.Trim()}\" saved.");
    }

    /// <summary>The "Remove property" confirmation modal.</summary>
    /// <param name="id">The property id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The confirmation partial, or 404.</returns>
    [HttpGet]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var details = await properties.GetPropertyAsync(id, cancellationToken);
        return details is null ? NotFound() : PartialView("_Confirm", ConfirmModel(id, details.Input.Name));
    }

    /// <summary>Removes the property and its units, unless a unit has applications or leases.</summary>
    /// <param name="id">The property id.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>JSON success, or the confirmation with the reason (422).</returns>
    [HttpPost, ActionName(nameof(Delete))]
    public async Task<IActionResult> DeleteConfirmed([FromRoute] int id, CancellationToken cancellationToken)
    {
        var details = await properties.GetPropertyAsync(id, cancellationToken);
        var result = await properties.DeletePropertyAsync(id, cancellationToken);
        if (!result.Succeeded)
        {
            AddError(result);
            return ModalInvalid("_Confirm", ConfirmModel(id, details?.Input.Name ?? "this property"));
        }

        return ModalSuccess("Property removed.");
    }

    /// <summary>The remove-property confirmation dialog; on success the list and summary refresh.</summary>
    private ConfirmViewModel ConfirmModel(int id, string name) => new()
    {
        Title = "Remove property",
        Message = $"Remove \"{name}\" and all of its units? This cannot be undone.",
        ActionUrl = Url.Action(nameof(Delete), new { id })!,
        Refresh = ListRegion,
    };
}
