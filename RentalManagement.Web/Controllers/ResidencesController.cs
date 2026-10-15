// =====================================================================================================================
// Module: RentalManagement.Web / Controllers / ResidencesController.cs
// Purpose: The add/edit/remove residence modals of the application's Residence History section, and the endpoint
//          that re-renders the residence list after a change. Applicant-only; every action checks ownership and
//          editability before showing a form, and ApplicationService checks again before saving.
// =====================================================================================================================

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalManagement.Core.Domain;
using RentalManagement.Core.Services;
using RentalManagement.Core.Services.Applications;
using RentalManagement.Web.Infrastructure;
using RentalManagement.Web.ViewComponents;
using RentalManagement.Web.ViewModels;

namespace RentalManagement.Web.Controllers;

/// <summary>
/// Add, edit and remove prior residences through modals. Only the applicant who owns the application may use these,
/// and only while it is Draft or Returned: the GETs check before showing a form, and the service checks again on
/// every POST. A success refreshes <c>#residence-list</c> (the ResidenceList view component).
/// </summary>
[Authorize(Roles = Roles.Applicant)]
public sealed class ResidencesController(ApplicationService applications) : AppController
{
    /// <summary>The page region reloaded after a residence modal succeeds.</summary>
    public const string RefreshRegion = "#residence-list";

    /// <summary>The ResidenceList view component alone, for refreshing <c>#residence-list</c>.</summary>
    /// <param name="applicationId">The application whose residences to list.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The rendered view component (editable only if the application still is), or 404.</returns>
    [HttpGet]
    public async Task<IActionResult> List(int applicationId, CancellationToken cancellationToken)
    {
        var details = await applications.GetAsync(applicationId, CurrentUserId, isManager: false, cancellationToken);
        return details is null
            ? NotFound()
            : ViewComponent(typeof(ResidenceListViewComponent), new { applicationId, editable = details.IsEditableBy(CurrentUserId) });
    }

    /// <summary>The empty "Add residence" modal.</summary>
    /// <param name="applicationId">The application to add to.</param>
    /// <param name="cancellationToken">Cancels the access check.</param>
    /// <returns>The form partial, a 409 explanation when not editable, or 404.</returns>
    [HttpGet]
    public async Task<IActionResult> Create(int applicationId, CancellationToken cancellationToken)
    {
        if (await CheckEditableAsync(applicationId, cancellationToken) is { } denied)
        {
            return denied;
        }

        return PartialView("_ResidenceForm", new ResidenceFormViewModel { ApplicationId = applicationId });
    }

    /// <summary>Adds a residence from the modal.</summary>
    /// <param name="model">The posted form.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>JSON success, the form with errors (422), or 404.</returns>
    [HttpPost]
    public async Task<IActionResult> Create(ResidenceFormViewModel model, CancellationToken cancellationToken)
    {
        model.Id = null;
        if (!ModelState.IsValid)
        {
            return ModalInvalid("_ResidenceForm", model);
        }

        var result = await applications.AddResidenceAsync(model.ApplicationId, CurrentUserId, model.ToInput(), cancellationToken);
        return Respond(result, model, "Residence added.");
    }

    /// <summary>The "Edit residence" modal, pre-filled.</summary>
    /// <param name="id">The residence id.</param>
    /// <param name="cancellationToken">Cancels the queries.</param>
    /// <returns>The form partial, a 409 explanation when not editable, or 404.</returns>
    [HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken cancellationToken)
    {
        var residence = await applications.GetResidenceAsync(id, CurrentUserId, cancellationToken);
        if (residence is null)
        {
            return NotFound();
        }

        if (await CheckEditableAsync(residence.ApplicationId, cancellationToken) is { } denied)
        {
            return denied;
        }

        return PartialView("_ResidenceForm", ResidenceFormViewModel.From(residence));
    }

    /// <summary>Saves a residence edit from the modal.</summary>
    /// <param name="id">The residence id (from the route).</param>
    /// <param name="model">The posted form.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>JSON success, the form with errors (422), or 404.</returns>
    [HttpPost]
    public async Task<IActionResult> Edit([FromRoute] int id, ResidenceFormViewModel model, CancellationToken cancellationToken)
    {
        // The owning application comes from the database, not from the posted form.
        var residence = await applications.GetResidenceAsync(id, CurrentUserId, cancellationToken);
        if (residence is null)
        {
            return NotFound();
        }

        model.Id = id;
        model.ApplicationId = residence.ApplicationId;
        if (!ModelState.IsValid)
        {
            return ModalInvalid("_ResidenceForm", model);
        }

        var result = await applications.UpdateResidenceAsync(id, CurrentUserId, model.ToInput(), cancellationToken);
        return Respond(result, model, "Residence saved.");
    }

    /// <summary>The "Remove residence" confirmation modal.</summary>
    /// <param name="id">The residence id.</param>
    /// <param name="cancellationToken">Cancels the queries.</param>
    /// <returns>The confirmation partial, a 409 explanation when not editable, or 404.</returns>
    [HttpGet]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var residence = await applications.GetResidenceAsync(id, CurrentUserId, cancellationToken);
        if (residence is null)
        {
            return NotFound();
        }

        if (await CheckEditableAsync(residence.ApplicationId, cancellationToken) is { } denied)
        {
            return denied;
        }

        return PartialView("_Confirm", ConfirmModel(residence));
    }

    /// <summary>Removes the residence (removing the last one makes the section incomplete again).</summary>
    /// <param name="id">The residence id.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>JSON success, the confirmation with the reason (422), or 404.</returns>
    [HttpPost, ActionName(nameof(Delete))]
    public async Task<IActionResult> DeleteConfirmed([FromRoute] int id, CancellationToken cancellationToken)
    {
        var residence = await applications.GetResidenceAsync(id, CurrentUserId, cancellationToken);
        if (residence is null)
        {
            return NotFound();
        }

        var result = await applications.RemoveResidenceAsync(id, CurrentUserId, cancellationToken);
        if (!result.Succeeded)
        {
            AddError(result);
            return ModalInvalid("_Confirm", ConfirmModel(residence));
        }

        return ModalSuccess("Residence removed.");
    }

    /// <summary>Maps a residence save result to the modal contract: JSON success, 404, or the form with errors (422).</summary>
    private IActionResult Respond(OperationResult result, ResidenceFormViewModel model, string successMessage)
    {
        if (result.Succeeded)
        {
            return ModalSuccess(successMessage);
        }

        if (result.Kind == FailureKind.NotFound)
        {
            return NotFound();
        }

        AddError(result);
        return ModalInvalid("_ResidenceForm", model);
    }

    /// <summary>404 for someone else's application; the reason in a modal (409) when it is no longer editable.</summary>
    private async Task<IActionResult?> CheckEditableAsync(int applicationId, CancellationToken cancellationToken)
    {
        var details = await applications.GetAsync(applicationId, CurrentUserId, isManager: false, cancellationToken);
        if (details is null)
        {
            return NotFound();
        }

        if (details.IsEditableBy(CurrentUserId))
        {
            return null;
        }

        Response.StatusCode = StatusCodes.Status409Conflict;
        return PartialView("_ModalMessage", "This application is no longer editable, so its residences can't be changed.");
    }

    /// <summary>The remove-residence confirmation dialog; on success the residence list refreshes.</summary>
    private ConfirmViewModel ConfirmModel(ResidenceDetails residence) => new()
    {
        Title = "Remove residence",
        Message = $"Remove {residence.Input.Street}, {residence.Input.City} from your residence history?",
        ActionUrl = Url.Action(nameof(Delete), new { id = residence.Id })!,
        Refresh = RefreshRegion,
    };
}
