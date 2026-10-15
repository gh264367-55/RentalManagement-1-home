// =====================================================================================================================
// Module: RentalManagement.Web / Infrastructure / AppController.cs
// Purpose: Shared base class for the app's controllers. Implements the server side of the modal contract (invalid →
//          same partial with HTTP 422, success → JSON), copies service errors into ModelState next to their fields,
//          and exposes the signed-in user's id.
// =====================================================================================================================

using Microsoft.AspNetCore.Mvc;
using RentalManagement.Core.Services;

namespace RentalManagement.Web.Infrastructure;

/// <summary>
/// Base controller that implements the modal contract used by <c>wwwroot/js/site.js</c>:
/// <list type="bullet">
///   <item>GET returns the form as a partial view, which the script puts in the modal;</item>
///   <item>an invalid POST returns the <b>same partial</b> with HTTP 422 and the validation messages, and the
///   script swaps it into the modal in place;</item>
///   <item>a successful POST returns <c>{ "success": true, "message": … }</c>; the script closes the modal and
///   reloads the page regions named in the form's <c>data-refresh</c> attribute.</item>
/// </list>
/// Convention: POST actions take the record id as <c>[FromRoute] int id</c>. MVC binds simple parameters from form
/// fields before route values, so without it a posted field named "id" would silently retarget the action.
/// </summary>
public abstract class AppController : Controller
{
    /// <summary>Re-renders a modal form with its validation messages (HTTP 422 Unprocessable Content).</summary>
    /// <param name="partialName">The form's partial view, e.g. "_UnitForm".</param>
    /// <param name="model">The posted model, so the user's input is kept.</param>
    /// <returns>The partial view result with status 422.</returns>
    protected PartialViewResult ModalInvalid(string partialName, object model)
    {
        Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        return PartialView(partialName, model);
    }

    /// <summary>Tells the script to close the modal and refresh the affected regions.</summary>
    /// <param name="message">Optional confirmation shown as a toast, e.g. "Unit 101 added."</param>
    /// <returns>A JSON result <c>{ success: true, message }</c>.</returns>
    protected JsonResult ModalSuccess(string? message = null) => Json(new { success = true, message });

    /// <summary>
    /// The failure answer of a fetch-posted action that has no form to re-render (e.g. Claim):
    /// <c>{ success: false, message }</c> with 404 (not found / not visible), 409 (state conflict) or 422 (invalid).
    /// site.js shows the message and refreshes the affected regions, so stale rows are corrected.
    /// </summary>
    /// <param name="result">The failed service result.</param>
    /// <returns>The JSON result with the matching status code.</returns>
    protected JsonResult ActionFailed(OperationResult result)
    {
        Response.StatusCode = result.Kind switch
        {
            FailureKind.NotFound => StatusCodes.Status404NotFound,
            FailureKind.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status422UnprocessableEntity,
        };
        return Json(new { success = false, message = result.Error });
    }

    /// <summary>
    /// Copies a failed service result into ModelState, next to the fields they belong to. Service field names match
    /// the view model property names; <paramref name="prefix"/> is the view model path to them (e.g. "ApplicantInfo").
    /// A null field becomes a form-level error shown in the validation summary.
    /// </summary>
    /// <param name="result">The failed service result.</param>
    /// <param name="prefix">The view-model path of the fields, or null when they are top-level properties.</param>
    protected void AddError(OperationResult result, string? prefix = null)
    {
        foreach (var error in result.Errors)
        {
            var key = error.Field is null ? string.Empty
                : string.IsNullOrEmpty(prefix) ? error.Field
                : $"{prefix}.{error.Field}";
            ModelState.AddModelError(key, error.Message);
        }
    }

    /// <summary>The signed-in user's id (the Identity user id claim).</summary>
    protected string CurrentUserId => User.GetRequiredUserId();
}
